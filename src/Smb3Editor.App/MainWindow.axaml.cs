using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Smb3Editor.Core;

namespace Smb3Editor.App;

public sealed partial class MainWindow : Window
{
    private readonly IRomCompiler _compiler = new RomCompiler();
    private readonly IBpsCodec _bpsCodec = new BpsCodec();
    private readonly IEmulatorLauncher _emulatorLauncher = new EmulatorLauncher();
    private readonly ISmb3LevelRenderer _safetyRenderer = new Smb3LevelRenderer();
    private readonly UndoRedoHistory<LevelDocument> _history = new();
    private readonly DispatcherTimer _autosaveTimer;
    private readonly List<Diagnostic> _diagnostics = [];
    private IReadOnlyList<Diagnostic> _activeRenderDiagnostics = [];
    private readonly List<CatalogEntry> _catalog;
    private RomImage? _rom;
    private ProjectDocumentV2? _project;
    private LevelDocument? _document;
    private string? _projectPath;
    private AppSettingsV1 _appSettings = new();
    private bool _refreshingInspector;
    private bool _lastPointerWasInsidePropertiesPane;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? startupRomPath)
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, MainWindow_PointerPressed, RoutingStrategies.Tunnel);
        EditorCanvas.EditCommitted += EditorCanvas_EditCommitted;
        EditorCanvas.ActiveRenderDiagnosticsChanged += SetActiveRenderDiagnostics;
        EditorCanvas.ZoomRequested += EditorCanvas_ZoomRequested;
        EditorCanvas.SelectionDescriptionChanged += text => SelectionText.Text = text;
        _catalog = BuildCatalog(1);
        CatalogList.ItemsSource = _catalog;
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _autosaveTimer.Tick += AutosaveTimer_Tick;
        AddDiagnostics([Diagnostics.Info("READY", "Open a verified US PRG0 or PRG1 ROM to begin.")]);
        var settings = AppSettingsStore.Load();
        AddDiagnostics(settings.Diagnostics);
        if (settings.IsSuccess)
        {
            _appSettings = settings.Value!;
        }
        EmulatorPathBox.Text = _appSettings.EmulatorPath;
        EmulatorArgumentsBox.Text = string.Join(Environment.NewLine, _appSettings.EmulatorArguments ?? ["{rom}"]);

        string? romToOpen = null;
        if (!string.IsNullOrWhiteSpace(startupRomPath) && File.Exists(startupRomPath))
        {
            romToOpen = startupRomPath;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_appSettings.LastRomPath))
            {
                if (File.Exists(_appSettings.LastRomPath))
                {
                    romToOpen = _appSettings.LastRomPath;
                }
                else
                {
                    AddDiagnostics([Diagnostics.Info("ROM_REMEMBERED_MISSING", "The previously used ROM is no longer at its saved path.")]);
                }
            }
        }

        if (romToOpen is not null)
        {
            OpenRom(romToOpen, createProject: true);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C)
        {
            EditorCanvas.CopySelection();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V)
        {
            EditorCanvas.PasteSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && e.Source is not TextBox)
        {
            EditorCanvas.DeleteSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && e.Source is not TextBox)
        {
            EditorCanvas.DeleteSelection();
            e.Handled = true;
        }
    }

    private async void OpenRom_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a verified SMB3 ROM",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("NES ROM") { Patterns = ["*.nes", "*.rom"] }]
        });
        if (files.Count == 0)
        {
            return;
        }

        OpenRom(files[0].Path.LocalPath, createProject: true);
    }

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SMB3 project",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SMB3 project") { Patterns = ["*.smb3proj"] }]
        });
        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].Path.LocalPath;
        var loaded = ProjectStore.Load(path);
        AddDiagnostics(loaded.Diagnostics);
        if (!loaded.IsSuccess)
        {
            return;
        }

        _project = loaded.Value!;
        _projectPath = path;
        if (!OpenRom(_project.Source.RomPathHint, createProject: false))
        {
            AddDiagnostics([Diagnostics.Error("PROJECT_ROM", "The project's source ROM is unavailable or no longer matches. Open the verified source ROM manually.")]);
            return;
        }

        EmulatorPathBox.Text = _project.EditorState.EmulatorPath;
        EmulatorArgumentsBox.Text = string.Join(Environment.NewLine, _project.EditorState.EmulatorArguments ?? ["{rom}"]);
        SaveGlobalEmulatorSettings();
        EditorCanvas.Zoom = Math.Clamp(_project.EditorState.Zoom, 0.25, 8.0);
        if (_project.EditorState.LastAreaId is string area && _project.ModifiedAreas.TryGetValue(area, out var modified))
        {
            SetDocument(modified, clearHistory: true);
        }
    }

    private async void SaveProject_Click(object? sender, RoutedEventArgs e) => await SaveProjectAsync(forcePicker: _projectPath is null);

    private async void ExportRom_Click(object? sender, RoutedEventArgs e)
    {
        var artifact = CompileCurrentProject();
        if (!artifact.IsSuccess)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export playable ROM",
            SuggestedFileName = "SMB3-Edited.nes",
            DefaultExtension = "nes",
            FileTypeChoices = [new FilePickerFileType("NES ROM") { Patterns = ["*.nes"] }]
        });
        if (file is null || _rom is null)
        {
            return;
        }

        var path = file.Path.LocalPath;
        if (PathsEqual(path, _rom.SourcePath))
        {
            AddDiagnostics([Diagnostics.Error("EXPORT_SOURCE", "Export cannot overwrite the immutable source ROM.")]);
            return;
        }

        AddDiagnostics(AtomicFile.Write(path, artifact.Value!.RomBytes, maintainBackup: true).Diagnostics);
    }

    private async void ExportBps_Click(object? sender, RoutedEventArgs e)
    {
        var artifact = CompileCurrentProject();
        if (!artifact.IsSuccess || _rom is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export BPS patch",
            SuggestedFileName = "SMB3-Edited.bps",
            DefaultExtension = "bps",
            FileTypeChoices = [new FilePickerFileType("BPS patch") { Patterns = ["*.bps"] }]
        });
        if (file is null)
        {
            return;
        }

        var patch = _bpsCodec.Create(_rom.Bytes, artifact.Value!.RomBytes);
        var verification = _bpsCodec.Apply(_rom.Bytes, patch);
        if (!verification.IsSuccess || !verification.Value!.SequenceEqual(artifact.Value.RomBytes))
        {
            AddDiagnostics(verification.Diagnostics.Append(Diagnostics.Error("BPS_VERIFY", "The generated BPS patch did not reproduce the compiled ROM.")));
            return;
        }

        AddDiagnostics(AtomicFile.Write(file.Path.LocalPath, patch, maintainBackup: true).Diagnostics);
    }

    private void PlayTest_Click(object? sender, RoutedEventArgs e)
    {
        SaveGlobalEmulatorSettings();
        var artifact = CompileCurrentProject();
        if (!artifact.IsSuccess)
        {
            return;
        }

        var emulatorPath = EmulatorPathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(emulatorPath))
        {
            AddDiagnostics([Diagnostics.Error("EMULATOR_CONFIG", "Choose an external emulator before play-testing.")]);
            return;
        }

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Smb3Editor", "Playtest");
        var romPath = Path.Combine(directory, "playtest.nes");
        var written = AtomicFile.Write(romPath, artifact.Value!.RomBytes, maintainBackup: false);
        AddDiagnostics(written.Diagnostics);
        if (!written.IsSuccess)
        {
            return;
        }

        var arguments = (EmulatorArgumentsBox.Text ?? "{rom}")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        AddDiagnostics(_emulatorLauncher.Launch(new EmulatorConfiguration(emulatorPath, arguments), romPath).Diagnostics);
    }

    private async void ChooseEmulator_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose emulator executable",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Executable") { Patterns = ["*.exe"] }]
        });
        if (files.Count > 0)
        {
            EmulatorPathBox.Text = files[0].Path.LocalPath;
            UpdateProjectEditorSettings();
            SaveGlobalEmulatorSettings();
        }
    }

    private void PropertiesToggle_Click(object? sender, RoutedEventArgs e) =>
        WorkspaceSplitView.IsPaneOpen = PropertiesToggle.IsChecked == true;

    private void WorkspaceSplitView_PaneClosing(object? sender, CancelRoutedEventArgs e)
    {
        if (_lastPointerWasInsidePropertiesPane)
        {
            e.Cancel = true;
        }
    }

    private void WorkspaceSplitView_PaneClosed(object? sender, RoutedEventArgs e) => PropertiesToggle.IsChecked = false;

    private void MainWindow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(WorkspaceSplitView);
        _lastPointerWasInsidePropertiesPane = WorkspaceSplitView.IsPaneOpen &&
                                              point.X >= WorkspaceSplitView.Bounds.Width - WorkspaceSplitView.OpenPaneLength &&
                                              point.X <= WorkspaceSplitView.Bounds.Width &&
                                              point.Y >= 0 && point.Y <= WorkspaceSplitView.Bounds.Height;
    }

    private void HeaderValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        => ApplyHeaderValues();

    private void HeaderSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyHeaderValues();

    private void ApplyHeaderValues()
    {
        if (_document is null || _refreshingInspector)
        {
            return;
        }

        var previous = _document;
        var screens = Math.Clamp((int)(ScreenCountBox.Value ?? 1), 1, 16);
        var backgroundPalette = (BackgroundPaletteBox.SelectedItem as PaletteChoice)?.Value ?? _document.Header.BackgroundPalette;
        var objectPalette = (ObjectPaletteBox.SelectedItem as PaletteChoice)?.Value ?? _document.Header.ObjectPalette;
        var music = (MusicBox.SelectedItem as NamedValue)?.Value ?? _document.Header.Music;
        var time = (TimeBox.SelectedItem as NamedValue)?.Value ?? _document.Header.TimeSetting;
        var header = _document.Header.WithEditableSettings(screens, backgroundPalette, objectPalette, music, time);
        _document = _document with { Header = header };
        _history.Record(previous);
        SetDocument(_document, clearHistory: false);
        MarkProjectChanged();
    }

    private void AddCatalog_Click(object? sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is not CatalogEntry entry)
        {
            return;
        }

        if (entry.IsEnemy)
        {
            EditorCanvas.AddEnemy((byte)entry.Id);
        }
        else
        {
            EditorCanvas.AddFixedGenerator(entry.Id);
        }
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object? sender, RoutedEventArgs e) => Redo();
    private void Copy_Click(object? sender, RoutedEventArgs e) => EditorCanvas.CopySelection();
    private void Paste_Click(object? sender, RoutedEventArgs e) => EditorCanvas.PasteSelection();
    private void Delete_Click(object? sender, RoutedEventArgs e) => EditorCanvas.DeleteSelection();

    private void LevelList_SelectionChanged(object? sender, EventArgs e)
    {
        if (_rom is null || LevelList.SelectedItem is not LevelLocation location)
        {
            return;
        }

        if (_project?.ModifiedAreas.TryGetValue(location.AreaId, out var modified) == true)
        {
            SetDocument(modified, clearHistory: true);
            return;
        }

        var decoded = Smb3LevelCodec.Decode(_rom, location);
        AddDiagnostics(decoded.Diagnostics);
        if (decoded.IsSuccess)
        {
            SetDocument(decoded.Value!, clearHistory: true);
        }
    }

    private void EditorCanvas_EditCommitted(object? sender, LevelEditCommittedEventArgs e)
    {
        _history.Record(e.Previous);
        _document = e.Current;
        MarkProjectChanged();
        RefreshInspector();
    }

    private void SetActiveRenderDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        _activeRenderDiagnostics = diagnostics;
        RefreshDiagnosticsList();
        var error = diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        SafetyBanner.IsVisible = error is not null;
        if (error is not null)
        {
            var summary = error.Message.Split(". ", 2, StringSplitOptions.None)[0];
            SafetyBannerText.Text = $"Unsafe level: {summary} — click for details";
        }
    }

    private void SafetyBanner_Click(object? sender, RoutedEventArgs e)
    {
        DiagnosticsPanel.IsExpanded = true;
    }

    private void EditorCanvas_ZoomRequested(object? sender, CanvasZoomRequestedEventArgs e)
    {
        var oldOffset = LevelScrollViewer.Offset;
        var viewportPoint = new Avalonia.Point(e.Pointer.X - oldOffset.X, e.Pointer.Y - oldOffset.Y);
        EditorCanvas.Zoom = e.NewZoom;
        LevelScrollViewer.UpdateLayout();
        var targetX = (e.LogicalPoint.X * e.NewZoom) - viewportPoint.X;
        var targetY = (e.LogicalPoint.Y * e.NewZoom) - viewportPoint.Y;
        LevelScrollViewer.Offset = new Avalonia.Vector(
            Math.Clamp(targetX, 0, Math.Max(0, LevelScrollViewer.Extent.Width - LevelScrollViewer.Viewport.Width)),
            Math.Clamp(targetY, 0, Math.Max(0, LevelScrollViewer.Extent.Height - LevelScrollViewer.Viewport.Height)));
    }

    private void AutosaveTimer_Tick(object? sender, EventArgs e)
    {
        _autosaveTimer.Stop();
        if (_projectPath is not null && _project is not null)
        {
            AddDiagnostics(ProjectStore.Save(_project, _projectPath).Diagnostics);
        }
    }

    private bool OpenRom(string path, bool createProject)
    {
        var loaded = RomImage.Load(path);
        AddDiagnostics(loaded.Diagnostics);
        if (!loaded.IsSuccess)
        {
            RomStatusText.Text = "ROM rejected — see diagnostics";
            return false;
        }

        _rom = loaded.Value!;
        EditorCanvas.SourceRom = _rom;
        _appSettings = _appSettings with { LastRomPath = _rom.SourcePath };
        AddDiagnostics(AppSettingsStore.Save(_appSettings).Diagnostics);
        if (createProject)
        {
            _project = ProjectDocumentV2.Create(_rom);
            _projectPath = null;
        }

        RomStatusText.Text = $"{_rom.Profile.DisplayName}\nSHA-1 {_rom.Sha1[..12]}...\nSource remains read-only";
        RefreshLevelList();
        if (_rom.Profile.Levels.Count == 0)
        {
            AddDiagnostics([Diagnostics.Warning("PROFILE_CATALOG", "This ROM revision is verified, but its editable area catalog is not yet enabled in this build.")]);
            return true;
        }

        LevelList.SelectedItem = _rom.Profile.Levels.TryGetValue("W1-1", out var firstLevel)
            ? firstLevel
            : _rom.Profile.Levels.Values.OrderBy(static level => level.DisplayName, StringComparer.Ordinal).First();
        return true;
    }

    private async Task SaveProjectAsync(bool forcePicker)
    {
        if (_project is null)
        {
            AddDiagnostics([Diagnostics.Error("PROJECT_NONE", "Open a verified ROM before saving a project.")]);
            return;
        }

        UpdateProjectEditorSettings();
        if (forcePicker || _projectPath is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save SMB3 project",
                SuggestedFileName = "My-SMB3-Hack.smb3proj",
                DefaultExtension = "smb3proj",
                FileTypeChoices = [new FilePickerFileType("SMB3 project") { Patterns = ["*.smb3proj"] }]
            });
            if (file is null)
            {
                return;
            }

            _projectPath = file.Path.LocalPath;
        }

        AddDiagnostics(ProjectStore.Save(_project, _projectPath).Diagnostics);
    }

    private OperationResult<BuildArtifact> CompileCurrentProject()
    {
        if (_project is null || _rom is null)
        {
            var missing = OperationResult<BuildArtifact>.Failure(Diagnostics.Error("BUILD_NONE", "Open a verified ROM or project before exporting."));
            AddDiagnostics(missing.Diagnostics);
            return missing;
        }

        if (_document is not null)
        {
            _project = _project.WithArea(_document);
        }

        foreach (var area in _project.ModifiedAreas.Values)
        {
            var safety = _safetyRenderer.Render(_rom, area);
            if (!safety.IsSuccess)
            {
                var diagnostics = safety.Diagnostics
                    .Append(Diagnostics.Error("BUILD_ACTIVE_RENDER", $"Export and play-test are blocked because {area.DisplayName} has a proven unsafe generator state."))
                    .ToArray();
                var blocked = OperationResult<BuildArtifact>.Failure(diagnostics);
                AddDiagnostics(blocked.Diagnostics);
                return blocked;
            }
        }

        var result = _compiler.Compile(_project, _rom);
        AddDiagnostics(result.Diagnostics);
        return result;
    }

    private void Undo()
    {
        if (_document is null || !_history.CanUndo)
        {
            return;
        }

        _document = _history.Undo(_document);
        SetDocument(_document, clearHistory: false);
        MarkProjectChanged();
    }

    private void Redo()
    {
        if (_document is null || !_history.CanRedo)
        {
            return;
        }

        _document = _history.Redo(_document);
        SetDocument(_document, clearHistory: false);
        MarkProjectChanged();
    }

    private void SetDocument(LevelDocument document, bool clearHistory)
    {
        _document = document;
        EditorCanvas.Document = document;
        RefreshCatalog(document.Tileset);
        if (clearHistory)
        {
            _history.Clear();
        }

        RefreshInspector();
    }

    private void MarkProjectChanged()
    {
        if (_project is null || _document is null)
        {
            return;
        }

        _project = _project.WithArea(_document);
        UpdateProjectEditorSettings();
        if (_projectPath is not null)
        {
            _autosaveTimer.Stop();
            _autosaveTimer.Start();
        }
    }

    private void UpdateProjectEditorSettings()
    {
        if (_project is null)
        {
            return;
        }

        var arguments = (EmulatorArgumentsBox.Text ?? "{rom}")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _project = _project with
        {
            EditorState = _project.EditorState with
            {
                LastAreaId = _document?.AreaId,
                Zoom = EditorCanvas.Zoom,
                EmulatorPath = EmulatorPathBox.Text,
                EmulatorArguments = arguments
            }
        };
    }

    private void RefreshLevelList()
    {
        var levels = _rom?.Profile.Levels.Values
            .OrderBy(static level => level.DisplayName, StringComparer.Ordinal)
            .ToArray() ?? [];
        LevelList.ItemsSource = levels;
    }

    private void RefreshInspector()
    {
        if (_document is null)
        {
            SpaceText.Text = "No area loaded";
            return;
        }

        _refreshingInspector = true;
        ScreenCountBox.Value = _document.Header.ScreenCount;
        BackgroundPaletteBox.ItemsSource = Enumerable.Range(0, 8)
            .Select(index => BuildPaletteChoice(index, objects: false))
            .ToArray();
        ObjectPaletteBox.ItemsSource = Enumerable.Range(0, 4)
            .Select(index => BuildPaletteChoice(index, objects: true))
            .ToArray();
        MusicBox.ItemsSource = MusicChoices;
        TimeBox.ItemsSource = TimeChoices;
        BackgroundPaletteBox.SelectedItem = ((IEnumerable<PaletteChoice>)BackgroundPaletteBox.ItemsSource).First(item => item.Value == _document.Header.BackgroundPalette);
        ObjectPaletteBox.SelectedItem = ((IEnumerable<PaletteChoice>)ObjectPaletteBox.ItemsSource).First(item => item.Value == _document.Header.ObjectPalette);
        MusicBox.SelectedItem = MusicChoices.First(item => item.Value == _document.Header.Music);
        TimeBox.SelectedItem = TimeChoices.First(item => item.Value == _document.Header.TimeSetting);
        _refreshingInspector = false;
        var layout = Smb3LevelCodec.EncodeLayout(_document);
        var enemies = Smb3LevelCodec.EncodeEnemies(_document);
        SpaceText.Text = $"Layout: {layout.Value?.Length ?? 0} / {_document.OriginalLayoutLength} bytes\n" +
                         $"Enemies: {enemies.Value?.Length ?? 0} / {_document.OriginalEnemyLength} bytes\n" +
                         $"Used by: {string.Join(", ", _document.UsedBy)}";
    }

    private void AddDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            _diagnostics.Add(diagnostic);
        }

        if (_diagnostics.Count > 250)
        {
            _diagnostics.RemoveRange(0, _diagnostics.Count - 250);
        }

        RefreshDiagnosticsList();
    }

    private void RefreshDiagnosticsList()
    {
        DiagnosticsList.ItemsSource = _diagnostics
            .Concat(_activeRenderDiagnostics)
            .AsEnumerable()
            .Reverse()
            .Select(static diagnostic => diagnostic.ToString())
            .ToArray();
    }

    private void RefreshCatalog(int tileset)
    {
        _catalog.Clear();
        _catalog.AddRange(BuildCatalog(tileset));
        ApplyCatalogFilter();
    }

    private void ApplyCatalogFilter()
    {
        CatalogList.ItemsSource = _catalog.ToArray();
        if (_catalog.Count > 0 && CatalogList.SelectedItem is null)
        {
            CatalogList.SelectedItem = _catalog[0];
        }
    }

    private void SaveGlobalEmulatorSettings()
    {
        var arguments = (EmulatorArgumentsBox.Text ?? "{rom}")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _appSettings = _appSettings with
        {
            EmulatorPath = EmulatorPathBox.Text?.Trim(),
            EmulatorArguments = arguments
        };
        AddDiagnostics(AppSettingsStore.Save(_appSettings).Diagnostics);
    }

    private static List<CatalogEntry> BuildCatalog(int tileset)
    {
        var items = ObjectCatalogNames.ForTileset(tileset)
            .Select(item => new CatalogEntry(false, item.Id, $"{item.Name} (${item.Id:X2}, {item.Id})"))
            .ToList();
        items.AddRange(Smb3LevelRenderer.EnemyCatalog
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(item => new CatalogEntry(true, item.Key,
                $"{item.Value} (${item.Key:X2}, {item.Key})")));
        return items;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record CatalogEntry(bool IsEnemy, int Id, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record NamedValue(int Value, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record PaletteChoice(int Value, string Name, IReadOnlyList<Avalonia.Media.IBrush> Colors);

    private PaletteChoice BuildPaletteChoice(int index, bool objects)
    {
        if (_rom is null || _document is null) return new(index, index.ToString(), []);
        var preview = Smb3LevelRenderer.ReadPalettePreview(_rom, _document, objects, index);
        var colors = preview.IsSuccess
            ? preview.Value!.Select(argb => (Avalonia.Media.IBrush)new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromUInt32(argb))).ToArray()
            : [];
        return new PaletteChoice(index, index.ToString(), colors);
    }

    private static readonly NamedValue[] MusicChoices =
    [
        new(0, "0 - Overworld"), new(1, "1 - Underground"),
        new(2, "2 - Underwater"), new(3, "3 - Fortress"),
        new(4, "4 - Boss"), new(5, "5 - Airship"),
        new(6, "6 - Battle"), new(7, "7 - Toad House"),
        new(8, "8 - Athletic"), new(9, "9 - Throne Room"),
        new(10, "10 - Sky"), new(11, "11 - Unused"),
        new(12, "12 - Unused"), new(13, "13 - Unused"),
        new(14, "14 - Unused"), new(15, "15 - Unused")
    ];

    private static readonly NamedValue[] TimeChoices =
    [
        new(0, "300 seconds"), new(1, "400 seconds"),
        new(2, "200 seconds"), new(3, "Unlimited")
    ];
}

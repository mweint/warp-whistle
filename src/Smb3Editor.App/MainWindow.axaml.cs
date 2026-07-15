using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Smb3Editor.Core;

namespace Smb3Editor.App;

internal sealed record PatchOverrideOption(string Name, bool? Value)
{
    public override string ToString() => Name;
}
internal sealed record OverworldTileChoice(byte Value)
{
    public override string ToString() => $"Tile ${Value:X2}";
}
internal sealed record OverworldTileEntry(byte Value, CatalogPreviewData Preview, bool IsBlank)
{
    public string ToolTip => $"Map tile ${Value:X2}";
    public int PaletteGroup => (Value >> 6) + 1;
}
internal sealed record OverworldScreenChoice(int Value, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class MainWindow : Window
{
    public static readonly StyledProperty<double> CatalogTileSizeProperty =
        AvaloniaProperty.Register<MainWindow, double>(nameof(CatalogTileSize), 60);

    public double CatalogTileSize
    {
        get => GetValue(CatalogTileSizeProperty);
        private set => SetValue(CatalogTileSizeProperty, value);
    }

#if WARPWHISTLE_TRACE
    private static readonly bool TraceToolsEnabled = true;
#else
    private static readonly bool TraceToolsEnabled = false;
#endif

    private readonly IRomCompiler _compiler = new RomCompiler();
    private readonly IDirectLevelTestBuilder _directLevelTestBuilder = new DirectLevelTestBuilder();
    private readonly IBpsCodec _bpsCodec = new BpsCodec();
    private readonly IEmulatorLauncher _emulatorLauncher = new EmulatorLauncher();
    private readonly ISmb3LevelRenderer _safetyRenderer = new Smb3LevelRenderer();
    private readonly UndoRedoHistory<LevelDocument> _history = new();
    private readonly UndoRedoHistory<OverworldDocument> _overworldHistory = new();
    private readonly List<Diagnostic> _diagnostics = [];
    private IReadOnlyList<Diagnostic> _activeRenderDiagnostics = [];
    private EditorActionFeedback? _activePersistentFeedback;
    private readonly List<CatalogEntry> _catalog = [];
    private readonly ObservableCollection<CatalogEntry> _recentCatalog = [];
    private readonly Dictionary<string, (bool IsEnemy, bool IsVariable, int Id)> _catalogVariantSelections = new(StringComparer.Ordinal);
    private CatalogVariantFamilies _catalogVariantFamilies = null!;
    private readonly CatalogPreviewMemoryCache _catalogPreviewCache = new();
    private CancellationTokenSource? _catalogPreviewCancellation;
    private CatalogEntry? _activeCatalogEntry;
    private RomImage? _rom;
    private ProjectDocumentV2? _project;
    private ProjectDocumentV2? _savedProject;
    private LevelDocument? _document;
    private string? _projectPath;
    private AppSettingsV1 _appSettings = new();
    private bool _refreshingInspector;
    private bool _refreshingCatalog;
    private bool _paletteObjectsEditing;
    private int _catalogFilter;
    private int _catalogColumns = 5;
    private bool _groupCatalogVariants = true;
    private byte[] _editingPaletteColors = new byte[16];
    private PaletteEditorWindow? _paletteEditor;
    private OverworldPaletteEditorWindow? _overworldPaletteEditor;
    private string _playMode = "rom";
    private int _zoomSequence;
    private string? _activeAreaId;
    private bool _isProjectDirty;
    private bool _suppressLevelSelection;
    private bool _closingApproved;
    private bool _refreshingPatches;
    private readonly Dictionary<ComboBox, string> _patchOverrideIds = [];
    private IReadOnlyList<OverworldDocument> _overworlds = [];
    private IReadOnlyList<OverworldTileEntry> _overworldTileEntries = [];
    private OverworldDocument? _overworldPaintStart;
    private OverworldDocument? _overworldNodeMoveStart;
    private byte _selectedOverworldTile = 0xFE;
    private OverworldLevelPointer? _selectedOverworldNode;
    private OverworldLockBridge? _selectedOverworldLock;
    private bool _refreshingOverworldNode;
    private readonly Dictionary<(int Palette, bool Sprites), byte[]> _overworldPaletteDrafts = [];
    private readonly DispatcherTimer _overworldAnimationTimer = new() { Interval = TimeSpan.FromSeconds(1d / 60d) };
    private int _overworldAnimationFrame;
    private int _overworldAnimationTicks;

    private static string LegacyPreviewCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpWhistle", "preview-cache");

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? startupRomPath)
    {
        InitializeComponent();
        TracePlayLevelToggle.IsVisible = TraceToolsEnabled;
        OpenTraceLogsMenuItem.IsVisible = TraceToolsEnabled;
        WorkspacePaths.Configure(AppContext.BaseDirectory);
        _catalogVariantFamilies = CatalogVariantFamilies.Load(BundledContentPaths.ItemGroupsPath, out var itemsConfigError);
        if (itemsConfigError is not null)
            AddDiagnostics([Diagnostics.Warning("ITEMS_CONFIG", itemsConfigError)]);
        if (SidebarTabs.Items.Count >= 2)
        {
            var properties = SidebarTabs.Items[0];
            SidebarTabs.Items.RemoveAt(0);
            SidebarTabs.Items.Insert(1, properties);
        }
        EditorCanvas.EditCommitted += EditorCanvas_EditCommitted;
        EditorCanvas.ActiveRenderDiagnosticsChanged += SetActiveRenderDiagnostics;
        EditorCanvas.ActionFeedbackAvailable += PresentEditorFeedback;
        EditorCanvas.PersistentActionFeedbackCleared += ClearPersistentEditorFeedback;
        EditorCanvas.ZoomRequested += EditorCanvas_ZoomRequested;
        EditorCanvas.PanRequested += EditorCanvas_PanRequested;
        EditorCanvas.EdgeScrollRequested += EditorCanvas_EdgeScrollRequested;
        EditorCanvas.CatalogPlacementRequested += PlaceCatalogAt;
        EditorCanvas.SelectionDescriptionChanged += text => SelectionText.Text = text;
        EditorCanvas.CanvasItemSelected += ClearCatalogPasteSource;
        OverworldCanvas.LevelPointerSelected += OverworldCanvas_LevelPointerSelected;
        OverworldCanvas.TilePaintRequested += OverworldCanvas_TilePaintRequested;
        OverworldCanvas.PaintStarted += OverworldCanvas_PaintStarted;
        OverworldCanvas.PaintCompleted += OverworldCanvas_PaintCompleted;
        OverworldCanvas.ZoomRequested += OverworldCanvas_ZoomRequested;
        OverworldCanvas.PanRequested += OverworldCanvas_PanRequested;
        OverworldCanvas.NodeMoveStarted += OverworldCanvas_NodeMoveStarted;
        OverworldCanvas.NodeMoveRequested += OverworldCanvas_NodeMoveRequested;
        OverworldCanvas.NodeMoveCompleted += OverworldCanvas_NodeMoveCompleted;
        OverworldCanvas.LockBridgeSelected += OverworldCanvas_LockBridgeSelected;
        _overworldAnimationTimer.Tick += OverworldAnimationTimer_Tick;
        _catalog.AddRange(BuildCatalog(1));
        ApplyCatalogFilter();
        RefreshPatches();
        AddDiagnostics([Diagnostics.Info("READY", "Open a verified US PRG0 or PRG1 ROM to begin.")]);
        if (Directory.Exists(LegacyPreviewCacheDirectory))
            AddDiagnostics([Diagnostics.Info("LEGACY_PREVIEW_CACHE", "A legacy item-preview cache remains in LocalAppData. Warp Whistle no longer writes preview images there.")]);
        var settings = AppSettingsStore.Load();
        AddDiagnostics(settings.Diagnostics);
        if (settings.IsSuccess)
        {
            _appSettings = settings.Value!;
        }
        _groupCatalogVariants = _appSettings.GroupCatalogVariants != false;
        CatalogGroupVariantsToggle.IsChecked = _groupCatalogVariants;
        ApplyCatalogFilter();
        var configuredEmulator = !string.IsNullOrWhiteSpace(_appSettings.EmulatorPath) && File.Exists(_appSettings.EmulatorPath)
            ? _appSettings.EmulatorPath
            : FindExternalMesen();
        if (!string.IsNullOrWhiteSpace(configuredEmulator) && !string.Equals(configuredEmulator, _appSettings.EmulatorPath, StringComparison.OrdinalIgnoreCase))
        {
            _appSettings = _appSettings with { EmulatorPath = configuredEmulator };
            AddDiagnostics([Diagnostics.Info("EMULATOR_EXTERNAL_FOUND", "Using Mesen from the Emulators workspace folder.")]);
            AddDiagnostics(AppSettingsStore.Save(_appSettings).Diagnostics);
        }
        EmulatorPathBox.Text = configuredEmulator;
        EmulatorArgumentsBox.Text = string.Join(Environment.NewLine, _appSettings.EmulatorArguments ?? ["{rom}"]);
        _playMode = "rom";
        _playMode = _appSettings.PlayMode == "level" ? "level" : "rom";
        UpdatePlayModeUi();

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

            if (romToOpen is null)
            {
                romToOpen = FindExternalRom();
            }
        }

        if (romToOpen is not null)
        {
            OpenRom(romToOpen, createProject: true);
        }
    }

    private string? FindExternalRom()
    {
        var directories = new[]
        {
            WorkspacePaths.RomsDirectory
        };
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var candidate in Directory.EnumerateFiles(directory, "*.nes", SearchOption.TopDirectoryOnly).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (!RomImage.Load(candidate).IsSuccess) continue;
                    AddDiagnostics([Diagnostics.Info("ROM_WORKSPACE_FOUND", $"Using verified ROM from {Path.GetFileName(directory)}: {Path.GetFileName(candidate)}")]);
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddDiagnostics([Diagnostics.Warning("ROM_WORKSPACE_READ", $"Could not read {Path.GetFileName(directory)}.")]);
            }
        }
        return null;
    }

    private static string? FindExternalMesen()
    {
        var directories = new[]
        {
            WorkspacePaths.EmulatorsDirectory
        };
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                var mesen = Directory.EnumerateFiles(directory, "Mesen.exe", SearchOption.AllDirectories)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (mesen is not null) return mesen;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
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
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S)
        {
            _ = SaveProjectAsync(forcePicker: _projectPath is null);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A && e.Source is not TextBox)
        {
            EditorCanvas.SelectAllItems();
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

        if (!await ResolveUnsavedChangesAsync("opening another ROM")) return;
        OpenRom(files[0].Path.LocalPath, createProject: true);
    }

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Warp Whistle project",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Warp Whistle project") { Patterns = ["*.wwproj"] }]
        });
        if (files.Count == 0)
        {
            return;
        }

        if (!await ResolveUnsavedChangesAsync("opening another project")) return;

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
        _savedProject = _project;
        _isProjectDirty = false;
        UpdateSaveState();
    }

    private async void SaveProject_Click(object? sender, RoutedEventArgs e)
    {
        await SaveProjectAsync(forcePicker: _projectPath is null);
    }

    private void OpenWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var directory in new[]
                     {
                         WorkspacePaths.RomsDirectory, WorkspacePaths.EmulatorsDirectory,
                         WorkspacePaths.ProjectsDirectory, WorkspacePaths.ExportsDirectory, WorkspacePaths.DataDirectory
                     })
                Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = WorkspacePaths.RootDirectory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AddDiagnostics([Diagnostics.Warning("WORKSPACE_OPEN", "Could not open the workspace folder.")]);
        }
    }

    private void OpenTraceLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (!TraceToolsEnabled) return;
        try
        {
            Directory.CreateDirectory(WorkspacePaths.PlaytestDirectory);
            Process.Start(new ProcessStartInfo { FileName = WorkspacePaths.PlaytestDirectory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AddDiagnostics([Diagnostics.Warning("TRACE_LOG_OPEN", "Could not open the trace-log folder.")]);
        }
    }

    private void OpenLegacyPreviewCache_Click(object? sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(LegacyPreviewCacheDirectory))
        {
            AddDiagnostics([Diagnostics.Info("LEGACY_PREVIEW_CACHE", "No legacy preview cache was found.")]);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = LegacyPreviewCacheDirectory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AddDiagnostics([Diagnostics.Warning("LEGACY_PREVIEW_CACHE", "Could not open the legacy preview cache.")]);
        }
    }

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

    private void PlayButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_playMode == "level") PlayCurrentLevel_Click(sender, e);
        else PlayTest_Click(sender, e);
    }

    private void SwitchPlayMode_Click(object? sender, RoutedEventArgs e) => SwitchPlayMode();

    private void SwitchPlayMode()
    {
        _playMode = _playMode == "rom" ? "level" : "rom";
        UpdatePlayModeUi();
        _appSettings = _appSettings with { PlayMode = _playMode };
        AddDiagnostics(AppSettingsStore.Save(_appSettings).Diagnostics);
    }

    private void UpdatePlayModeUi()
    {
        PlayButton.Content = _playMode == "level" ? "Play Level" : "Play ROM";
        PlayModeMenuItem.Header = _playMode == "level" ? "Play ROM" : "Play Level";
    }

    private Task<bool> EnsureProjectSavedForPlayAsync() =>
        _projectPath is null || _isProjectDirty
            ? SaveProjectAsync(forcePicker: _projectPath is null)
            : Task.FromResult(true);

    private async void PlayTest_Click(object? sender, RoutedEventArgs e)
    {
        ClearPlayFeedback();
        RomStatusText.Text = "Preparing Play ROM...";
        if (!await EnsureProjectSavedForPlayAsync()) return;
        SaveGlobalEmulatorSettings();
        if (!await EnsureEmulatorConfiguredAsync())
        {
            ShowPlayFailure("Play ROM needs a configured emulator.");
            return;
        }

        var tracePlayRom = TraceToolsEnabled && TracePlayLevelToggle.IsChecked == true;
        if (tracePlayRom && !Path.GetFileNameWithoutExtension(EmulatorPathBox.Text!.Trim()).Contains("mesen", StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostics([Diagnostics.Error("PLAY_TRACE_EMULATOR", "Play ROM tracing requires Mesen or Mesen 2.")]);
            ShowPlayFailure("Play ROM trace requires Mesen.");
            return;
        }

        var artifact = CompileCurrentProject();
        if (!artifact.IsSuccess)
        {
            ShowPlayFailure("Play ROM is blocked — open Diagnostics for the exact reason.");
            return;
        }

        var emulatorPath = EmulatorPathBox.Text!.Trim();

        var directory = WorkspacePaths.PlaytestDirectory;
        var romPath = Path.Combine(directory, "playtest.nes");
        var written = AtomicFile.Write(romPath, artifact.Value!.RomBytes, maintainBackup: false);
        AddDiagnostics(written.Diagnostics);
        if (!written.IsSuccess)
        {
            ShowPlayFailure("Could not write the temporary Play ROM.");
            return;
        }

        IReadOnlyList<string> arguments;
        if (tracePlayRom)
        {
            var logPath = Path.Combine(directory, "retry-trace.log");
            var tracePath = Path.Combine(directory, "trace-retry.lua");
            var metadata = $"TRACE_META UTC={DateTimeOffset.UtcNow:O} PROFILE={_rom!.Profile.Id} SHA1={_rom.Sha1} TRACE=retry DIRECT_LEVEL=false\n";
            var logWrite = AtomicFile.Write(logPath, Encoding.UTF8.GetBytes(metadata), maintainBackup: false);
            AddDiagnostics(logWrite.Diagnostics);
            if (!logWrite.IsSuccess)
            {
                ShowPlayFailure("Could not create the Play ROM trace log.");
                return;
            }

            var bundledScript = Path.Combine(AppContext.BaseDirectory, "trace-retry.lua");
            if (!File.Exists(bundledScript)) bundledScript = Path.Combine(AppContext.BaseDirectory, "tools", "retry-trace.lua");
            if (!File.Exists(bundledScript))
            {
                AddDiagnostics([Diagnostics.Error("PLAY_TRACE_SCRIPT", "The bundled retry trace script is missing from this build.")]);
                ShowPlayFailure("The Play ROM trace script is missing.");
                return;
            }

            var scriptWrite = AtomicFile.Write(tracePath, Encoding.UTF8.GetBytes(File.ReadAllText(bundledScript).Replace("@@TRACE_LOG_PATH@@", logPath.Replace('\\', '/'), StringComparison.Ordinal)), maintainBackup: false);
            AddDiagnostics(scriptWrite.Diagnostics);
            if (!scriptWrite.IsSuccess)
            {
                ShowPlayFailure("Could not prepare the Play ROM trace script.");
                return;
            }
            arguments = ["{rom}", tracePath];
        }
        else
        {
            arguments = (EmulatorArgumentsBox.Text ?? "{rom}")
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        var launched = _emulatorLauncher.Launch(new EmulatorConfiguration(emulatorPath, arguments), romPath);
        AddDiagnostics(launched.Diagnostics);
        if (!launched.IsSuccess) ShowPlayFailure("Could not start the emulator.");
        else RomStatusText.Text = "Launching Play ROM...";
    }

    private async void PlayCurrentLevel_Click(object? sender, RoutedEventArgs e)
    {
        ClearPlayFeedback();
        RomStatusText.Text = "Preparing Play Level...";
        if (!await EnsureProjectSavedForPlayAsync()) return;
        SaveGlobalEmulatorSettings();
        if (!await EnsureEmulatorConfiguredAsync())
        {
            ShowPlayFailure("Play Level needs a configured emulator.");
            return;
        }

        var tracePlayLevel = TraceToolsEnabled && TracePlayLevelToggle.IsChecked == true;
        if (tracePlayLevel && !Path.GetFileNameWithoutExtension(EmulatorPathBox.Text!.Trim()).Contains("mesen", StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostics([Diagnostics.Error("PLAY_LEVEL_TRACE_EMULATOR", "Play Level tracing requires Mesen or Mesen 2.")]);
            ShowPlayFailure("Play Level trace requires Mesen.");
            return;
        }

        if (_rom is null || _document is null || !_rom.Profile.Levels.TryGetValue(_document.AreaId, out var selectedLevel))
        {
            AddDiagnostics([Diagnostics.Error("PLAY_LEVEL_NONE", "Select a verified level before using Play Level.")]);
            ShowPlayFailure("Select a verified level before Play Level.");
            return;
        }

        var compiled = CompileCurrentProject();
        if (!compiled.IsSuccess)
        {
            ShowPlayFailure("Play Level is blocked — open Diagnostics for the exact reason.");
            return;
        }

        var directTest = _directLevelTestBuilder.Build(compiled.Value!, _rom, selectedLevel);
        AddDiagnostics(directTest.Diagnostics);
        if (!directTest.IsSuccess)
        {
            var reason = directTest.Diagnostics.FirstOrDefault(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)?.Message;
            ShowPlayFailure(string.IsNullOrWhiteSpace(reason)
                ? "Play Level could not prepare its temporary test ROM."
                : $"Play Level: {reason}");
            return;
        }

        var directory = WorkspacePaths.PlaytestDirectory;
        TryCleanDirectPlaytests(directory);
        var romPath = Path.Combine(directory, $"play-level-{selectedLevel.AreaId}-{Guid.NewGuid():N}.nes");
        var written = AtomicFile.Write(romPath, directTest.Value!.RomBytes, maintainBackup: false);
        AddDiagnostics(written.Diagnostics);
        if (!written.IsSuccess)
        {
            ShowPlayFailure("Could not write the temporary Play Level ROM.");
            return;
        }

        try
        {
            var verification = _directLevelTestBuilder.VerifyReadback(directTest.Value, File.ReadAllBytes(romPath));
            AddDiagnostics(verification.Diagnostics);
            if (!verification.IsSuccess)
            {
                ShowPlayFailure("The temporary Play Level ROM did not verify.");
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddDiagnostics([Diagnostics.Error("PLAY_LEVEL_READBACK", $"The temporary level test could not be verified: {ex.Message}")]);
            ShowPlayFailure("The temporary Play Level ROM could not be verified.");
            return;
        }

        RomStatusText.Text = tracePlayLevel
            ? $"Tracing {selectedLevel.DisplayName} as Small Mario"
            : $"Launching {selectedLevel.DisplayName} as Small Mario";
        IReadOnlyList<string> arguments;
        if (tracePlayLevel)
        {
            var logPath = Path.Combine(directory, "autoscroll-trace.log");
            var tracePath = Path.Combine(directory, "trace-autoscroll.lua");
            var metadata = $"TRACE_META UTC={DateTimeOffset.UtcNow:O} PROFILE={_rom.Profile.Id} SHA1={_rom.Sha1} AREA={selectedLevel.AreaId} TRACE=autoscroll DIRECT_LEVEL=true\n";
            var logWrite = AtomicFile.Write(logPath, Encoding.UTF8.GetBytes(metadata), maintainBackup: false);
            AddDiagnostics(logWrite.Diagnostics);
            if (!logWrite.IsSuccess)
            {
                ShowPlayFailure("Could not create the Play Level trace log.");
                return;
            }

            var bundledScript = Path.Combine(AppContext.BaseDirectory, "trace-autoscroll.lua");
            if (!File.Exists(bundledScript)) bundledScript = Path.Combine(AppContext.BaseDirectory, "tools", "autoscroll-trace.lua");
            if (!File.Exists(bundledScript))
            {
                AddDiagnostics([Diagnostics.Error("PLAY_LEVEL_TRACE_SCRIPT", "The bundled auto-scroll trace script is missing from this build.")]);
                ShowPlayFailure("The Play Level trace script is missing.");
                return;
            }

            var scriptWrite = AtomicFile.Write(tracePath, Encoding.UTF8.GetBytes(File.ReadAllText(bundledScript).Replace("@@TRACE_LOG_PATH@@", logPath.Replace('\\', '/'), StringComparison.Ordinal)), maintainBackup: false);
            AddDiagnostics(scriptWrite.Diagnostics);
            if (!scriptWrite.IsSuccess)
            {
                ShowPlayFailure("Could not prepare the Play Level trace script.");
                return;
            }
            arguments = ["{rom}", tracePath];
        }
        else
        {
            arguments = (EmulatorArgumentsBox.Text ?? "{rom}")
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        var launched = _emulatorLauncher.Launch(new EmulatorConfiguration(EmulatorPathBox.Text!.Trim(), arguments), romPath);
        AddDiagnostics(launched.Diagnostics);
        if (!launched.IsSuccess) ShowPlayFailure("Could not start the emulator.");
        else DesignerNotice.IsVisible = false;
    }

    private void ClearPlayFeedback()
    {
        _diagnostics.RemoveAll(static diagnostic =>
            diagnostic.Code.StartsWith("PLAY_", StringComparison.Ordinal) ||
            diagnostic.Code.StartsWith("PATCH_", StringComparison.Ordinal) ||
            diagnostic.Code.StartsWith("CONTINUOUS_", StringComparison.Ordinal) ||
            diagnostic.Code.StartsWith("ASM_PATCH", StringComparison.Ordinal) ||
            diagnostic.Code.StartsWith("BUILD_", StringComparison.Ordinal));
        DesignerNotice.IsVisible = false;
        RefreshDiagnosticsList();
    }

    private void ShowPlayFailure(string message)
    {
        RomStatusText.Text = message;
        DesignerNoticeText.Text = message;
        DesignerNotice.IsVisible = true;
    }

    private static void TryCleanDirectPlaytests(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(directory, "play-level-*.nes"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // A locked emulator image is disposable and will be retried later.
        }
        catch (UnauthorizedAccessException)
        {
            // A locked emulator image is disposable and will be retried later.
        }
    }

    private void ApplyPalette_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _document is null || !TryReadPaletteValues(out var colors, out var objects, out var slot)) return;
        SetPaletteColors(objects, slot, colors);
    }

    private void OpenPaletteEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (_rom is null || _document is null || _project is null)
        {
            AddDiagnostics([Diagnostics.Error("PALETTE_NONE", "Open a verified ROM and level before editing palettes.")]);
            return;
        }

        if (_paletteEditor is null)
        {
            _paletteEditor = new PaletteEditorWindow(GetPaletteSlot, GetStockPaletteSlot, PreviewPaletteDrafts, CommitPaletteDrafts, CancelPaletteDrafts);
            _paletteEditor.Closed += (_, _) => _paletteEditor = null;
            _paletteEditor.Show(this);
        }
        else
        {
            _paletteEditor.RefreshFromHost();
            _paletteEditor.Activate();
        }
    }

    private PaletteSlotInfo? GetPaletteSlot(bool objects, int slot)
    {
        if (_rom is null || _document is null || _project is null) return null;
        var limit = objects ? 4 : 8;
        if (slot < 0 || slot >= limit) return null;
        var overrideEntry = (_project.PaletteOverrides ?? []).LastOrDefault(item => item.Tileset == _document.Tileset && item.Objects == objects && item.Slot == slot);
        var source = Smb3LevelRenderer.ReadPaletteIndices(_rom, _document, objects, slot);
        var colors = overrideEntry?.Colors.Count == 16
            ? overrideEntry.Colors.ToArray()
            : source.IsSuccess ? source.Value!.ToArray() : new byte[16];
        var label = (_project.PaletteSlotLabels ?? []).LastOrDefault(item => item.Tileset == _document.Tileset && item.Objects == objects && item.Slot == slot)?.Name;
        var name = label ?? string.Empty;
        return new PaletteSlotInfo(objects, slot, name, overrideEntry is not null, colors);
    }

    private PaletteSlotInfo? GetStockPaletteSlot(bool objects, int slot)
    {
        if (_rom is null || _document is null || _project is null) return null;
        var source = Smb3LevelRenderer.ReadPaletteIndices(_rom, _document, objects, slot);
        if (!source.IsSuccess) return null;
        var name = (_project.PaletteSlotLabels ?? []).LastOrDefault(item => item.Tileset == _document.Tileset && item.Objects == objects && item.Slot == slot)?.Name ?? string.Empty;
        return new PaletteSlotInfo(objects, slot, name, false, source.Value!.ToArray());
    }

    private void PreviewPaletteDrafts(IReadOnlyList<PaletteSlotInfo> drafts) =>
        EditorCanvas.PaletteOverrides = BuildPaletteOverrides(drafts);

    private void CancelPaletteDrafts()
    {
        EditorCanvas.PaletteOverrides = _project?.PaletteOverrides ?? [];
        RefreshInspector();
    }

    private void CommitPaletteDrafts(IReadOnlyList<PaletteSlotInfo> drafts)
    {
        if (_project is null || _document is null) return;
        var keys = drafts.Select(item => (item.Objects, item.Slot)).ToHashSet();
        var overrides = (_project.PaletteOverrides ?? []).Where(item => item.Tileset != _document.Tileset || !keys.Contains((item.Objects, item.Slot))).ToList();
        overrides.AddRange(drafts.Where(static item => item.IsModified).Select(item => new PaletteOverride(_document.Tileset, item.Objects, item.Slot, item.Colors.ToArray())));
        var labels = (_project.PaletteSlotLabels ?? []).Where(item => item.Tileset != _document.Tileset || !keys.Contains((item.Objects, item.Slot))).ToList();
        labels.AddRange(drafts.Where(item => !string.IsNullOrWhiteSpace(item.Name)).Select(item => new PaletteSlotLabel(_document.Tileset, item.Objects, item.Slot, item.Name)));
        _project = _project with { PaletteOverrides = overrides, PaletteSlotLabels = labels };
        EditorCanvas.PaletteOverrides = overrides;
        MarkProjectChanged();
        UpdateSaveState();
        RefreshInspector();
    }

    private IReadOnlyList<PaletteOverride> BuildPaletteOverrides(IReadOnlyList<PaletteSlotInfo> drafts)
    {
        if (_project is null || _document is null) return [];
        var keys = drafts.Select(item => (item.Objects, item.Slot)).ToHashSet();
        return (_project.PaletteOverrides ?? []).Where(item => item.Tileset != _document.Tileset || !keys.Contains((item.Objects, item.Slot)))
            .Concat(drafts.Where(static item => item.IsModified).Select(item => new PaletteOverride(_document.Tileset, item.Objects, item.Slot, item.Colors.ToArray())))
            .ToArray();
    }

    private void SetPaletteColors(bool objects, int slot, IReadOnlyList<byte> colors)
    {
        if (_project is null || _document is null || (colors.Count != 0 && colors.Count != 16)) return;
        var overrides = (_project.PaletteOverrides ?? []).Where(item => !(item.Tileset == _document.Tileset && item.Objects == objects && item.Slot == slot)).ToList();
        if (colors.Count > 0)
        {
            overrides.Add(new PaletteOverride(_document.Tileset, objects, slot, colors.Select(static color => (byte)(color & 0x3F)).ToArray()));
        }
        _project = _project with { PaletteOverrides = overrides };
        EditorCanvas.PaletteOverrides = overrides;
        MarkProjectChanged();
        RefreshInspector();
        _paletteEditor?.RefreshFromHost();
    }

    private void SetPaletteName(bool objects, int slot, string name)
    {
        if (_project is null || _document is null) return;
        var labels = (_project.PaletteSlotLabels ?? []).Where(item => !(item.Tileset == _document.Tileset && item.Objects == objects && item.Slot == slot)).ToList();
        if (!string.IsNullOrWhiteSpace(name)) labels.Add(new PaletteSlotLabel(_document.Tileset, objects, slot, name));
        _project = _project with { PaletteSlotLabels = labels };
        MarkProjectChanged();
        RefreshInspector();
    }

    private void TogglePaletteLibrary_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryReadPaletteValues(out var colors, out var objects, out var slot)) return;
        var loaded = PaletteLibraryStore.Load();
        AddDiagnostics(loaded.Diagnostics);
        if (!loaded.IsSuccess) return;
        var palettes = loaded.Value!.ToList();
        var name = $"Tileset {_document?.Tileset ?? 0} {(objects ? "Object" : "Background")} {slot}";
        var existing = palettes.FindIndex(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        if (existing >= 0)
        {
            palettes.RemoveAt(existing);
            PaletteHeartButton.Content = "♡";
        }
        else
        {
            palettes.Add(new SavedPalette(name, objects, colors));
            PaletteHeartButton.Content = "♥";
        }
        AddDiagnostics(PaletteLibraryStore.Save(palettes).Diagnostics);
    }

    private void ApplyPaletteLibrary_Click(object? sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var loaded = PaletteLibraryStore.Load();
        AddDiagnostics(loaded.Diagnostics);
        if (!loaded.IsSuccess || loaded.Value!.Count == 0)
        {
            AddDiagnostics([Diagnostics.Info("PALETTE_LIBRARY_EMPTY", "Save a palette to the library before applying one.")]);
            return;
        }
        var palette = loaded.Value!.Last();
        var slot = palette.Objects ? _document.Header.ObjectPalette : _document.Header.BackgroundPalette;
        _editingPaletteColors = palette.Colors.Take(16).Concat(Enumerable.Repeat((byte)0, 16)).Take(16).ToArray();
        if (palette.Objects) ObjectPaletteBox.SelectedItem = ((IEnumerable<PaletteChoice>)ObjectPaletteBox.ItemsSource!).First(item => item.Value == slot);
        else BackgroundPaletteBox.SelectedItem = ((IEnumerable<PaletteChoice>)BackgroundPaletteBox.ItemsSource!).First(item => item.Value == slot);
        ApplyPalette_Click(sender, e);
    }

    private bool TryReadPaletteValues(out byte[] colors, out bool objects, out int slot)
    {
        colors = [];
        objects = false;
        slot = 0;
        if (_document is null) return false;
        objects = _paletteObjectsEditing;
        slot = objects ? _document.Header.ObjectPalette : _document.Header.BackgroundPalette;
        colors = _editingPaletteColors.ToArray();
        return true;
    }

    private async void ChooseEmulator_Click(object? sender, RoutedEventArgs e)
    {
        await EnsureEmulatorConfiguredAsync();
    }

    private async Task<bool> EnsureEmulatorConfiguredAsync()
    {
        if (!string.IsNullOrWhiteSpace(EmulatorPathBox.Text) && File.Exists(EmulatorPathBox.Text.Trim()))
        {
            return true;
        }

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
            return true;
        }

        AddDiagnostics([Diagnostics.Error("EMULATOR_CONFIG", "Choose an external emulator before play-testing.")]);
        return false;
    }

    private void HeaderValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        => ApplyHeaderValues();

    private void HeaderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ObjectPaletteBox)) _paletteObjectsEditing = true;
        else if (ReferenceEquals(sender, BackgroundPaletteBox)) _paletteObjectsEditing = false;
        ApplyHeaderValues();
    }

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
        PlaceCatalogAt(1, 1);
    }

    private void PlaceCatalogAt(int x, int y)
    {
        if (_activeCatalogEntry is { } entry)
        {
            if (entry.IsEnemy) EditorCanvas.AddEnemy((byte)entry.Id, x, y);
            else if (entry.IsVariable) EditorCanvas.AddVariableGenerator(entry.Id, x, y);
            else EditorCanvas.AddFixedGenerator(entry.Id, x, y);
            RememberRecent(entry);
            return;
        }

        EditorCanvas.CopySelectionTo(x, y);
    }

    private void CatalogFilterChanged(object? sender, TextChangedEventArgs e) => ApplyCatalogFilter();

    private void CatalogScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } box) return;
        _catalogFilter = box.SelectedIndex;
        ApplyCatalogFilter();
    }

    private void CatalogSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingCatalog || sender is not ListBox { SelectedItem: CatalogEntry entry }) return;
        _activeCatalogEntry = entry;
        EditorCanvas.ClearSelection();
        _refreshingCatalog = true;
        CatalogGrid.SelectedItem = entry;
        CatalogListView.SelectedItem = entry;
        _refreshingCatalog = false;
    }

    private void ClearCatalogPasteSource()
    {
        if (_activeCatalogEntry is null) return;
        _activeCatalogEntry = null;
        _refreshingCatalog = true;
        CatalogGrid.SelectedItem = null;
        CatalogListView.SelectedItem = null;
        _refreshingCatalog = false;
    }

    private void RememberRecent(CatalogEntry entry)
    {
        entry = entry.AsVariant();
        foreach (var existing in _recentCatalog.Where(item => item.IsEnemy == entry.IsEnemy && item.IsVariable == entry.IsVariable && item.Id == entry.Id).ToArray())
            _recentCatalog.Remove(existing);
        _recentCatalog.Insert(0, entry);
        while (_recentCatalog.Count > 24) _recentCatalog.RemoveAt(_recentCatalog.Count - 1);
    }

    private void GroupCatalogVariants_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton item) return;
        _groupCatalogVariants = item.IsChecked == true;
        _appSettings = _appSettings with { GroupCatalogVariants = _groupCatalogVariants };
        AddDiagnostics(AppSettingsStore.Save(_appSettings).Diagnostics);
        ApplyCatalogFilter();
    }


    private void CatalogItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: CatalogEntry { Variants.Count: > 1 } entry } target ||
            e.GetCurrentPoint(target).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
            return;
        ShowCatalogVariantFlyout(target, entry);
        e.Handled = true;
    }

    private void ShowCatalogVariantFlyout(Control target, CatalogEntry entry)
    {
        var variants = entry.Variants ?? [];
        var flyout = new MenuFlyout();
        foreach (var variant in variants)
        {
            var active = entry.SameIdentity(variant);
            var item = new MenuItem
            {
                Tag = variant,
                Header = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new CatalogPreview
                        {
                            Preview = variant.Preview,
                            Width = 36,
                            Height = 36
                        },
                        new TextBlock
                        {
                            Text = active ? $"✓ {variant.Display}" : variant.Display,
                            MaxWidth = 360,
                            TextWrapping = TextWrapping.Wrap,
                            FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                        }
                    }
                }
            };
            Avalonia.Automation.AutomationProperties.SetName(item, $"Choose {variant.Display}");
            item.Click += (_, args) =>
            {
                SelectCatalogVariant(variant);
                args.Handled = true;
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(target);
    }

    private void SelectCatalogVariant(CatalogEntry variant)
    {
        if (variant.FamilyId is null) return;
        _catalogVariantSelections[variant.FamilyId] = variant.Identity;
        _activeCatalogEntry = variant;
        ApplyCatalogFilter();
    }

    private void CatalogViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } box || CatalogGrid is null || CatalogListView is null) return;
        var grid = box.SelectedIndex == 0;
        CatalogGrid.IsVisible = grid;
        CatalogListView.IsVisible = !grid;
        if (CatalogTileSizeControls is not null) CatalogTileSizeControls.IsVisible = grid;
    }

    private void CatalogTileSizeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider slider) return;
        var columns = Math.Clamp((int)Math.Round(e.NewValue), 2, 7);
        if (Math.Abs(columns - e.NewValue) > 0.01)
        {
            slider.Value = columns;
            return;
        }
        _catalogColumns = columns;
        UpdateCatalogTileSize();
    }

    private void CatalogGridSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateCatalogTileSize();

    private void UpdateCatalogTileSize()
    {
        if (CatalogGrid is null || CatalogGrid.Bounds.Width <= 0) return;
        // Each item owns four pixels of padding. Reserve scrollbar space so the
        // requested count still fits after the catalog becomes scrollable.
        CatalogTileSize = Math.Max(24, Math.Floor((CatalogGrid.Bounds.Width - 14) / _catalogColumns) - 4);
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object? sender, RoutedEventArgs e) => Redo();
    private void Copy_Click(object? sender, RoutedEventArgs e) => EditorCanvas.CopySelection();
    private void Paste_Click(object? sender, RoutedEventArgs e) => EditorCanvas.PasteSelection();
    private void Delete_Click(object? sender, RoutedEventArgs e) => EditorCanvas.DeleteSelection();

    private async void LevelList_SelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressLevelSelection || _rom is null || LevelList.SelectedItem is not LevelLocation location || location.AreaId == _activeAreaId)
        {
            return;
        }

        if (!await ResolveUnsavedChangesAsync("switching levels"))
        {
            _suppressLevelSelection = true;
            LevelList.SelectedItem = _rom.Profile.Levels.Values.FirstOrDefault(item => item.AreaId == _activeAreaId);
            _suppressLevelSelection = false;
            return;
        }

        LoadLevel(location);
    }

    private void LoadLevel(LevelLocation location)
    {
        if (_rom is null) return;
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
        ApplyCatalogFilter();
        RefreshInspector();
        EditorCanvas.RefreshEnemyValidation();
    }

    private void PresentEditorFeedback(EditorActionFeedback feedback)
    {
        var diagnostic = feedback.Severity switch
        {
            DiagnosticSeverity.Error => Diagnostics.Error("EDITOR_ACTION", feedback.Details),
            DiagnosticSeverity.Warning => Diagnostics.Warning("EDITOR_ACTION", feedback.Details),
            _ => Diagnostics.Info("EDITOR_ACTION", feedback.Details)
        };
        if (feedback.Persistent)
        {
            _activePersistentFeedback = feedback;
            RefreshDiagnosticsList();
        }
        else
        {
            AddDiagnostics([diagnostic]);
        }
        RomStatusText.Text = feedback.Summary;
        if (feedback.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
        {
            // Keep the chrome notification brief. The actionable detail belongs in
            // Diagnostics and the affected object's tooltip.
            DesignerNoticeText.Text = feedback.Summary;
            DesignerNotice.IsVisible = !_activeRenderDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
        }
    }

    private void ClearPersistentEditorFeedback()
    {
        _activePersistentFeedback = null;
        DesignerNotice.IsVisible = false;
        RefreshDiagnosticsList();
    }

    private void DesignerNotice_Click(object? sender, RoutedEventArgs e) => DiagnosticsPanel.IsExpanded = true;

    private void ClearLevel_Click(object? sender, RoutedEventArgs e) => EditorCanvas.ClearLevel();

    private void OverworldToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (OverworldToggle.IsChecked == true)
        {
            if (_rom is null)
            {
                OverworldToggle.IsChecked = false;
                AddDiagnostics([Diagnostics.Info("OVERWORLD_ROM", "Open a verified US PRG1 ROM to view its overworld.")]);
                return;
            }
            var parsed = Smb3OverworldParser.Parse(_rom);
            AddDiagnostics(parsed.Diagnostics);
            if (!parsed.IsSuccess)
            {
                OverworldToggle.IsChecked = false;
                RomStatusText.Text = "Overworld view is unavailable for this ROM";
                return;
            }
            _overworlds = parsed.Value!;
            if (_project?.OverworldTiles is { } savedTiles)
            {
                _overworlds = _overworlds.Select(map => savedTiles.FirstOrDefault(item => item.World == map.World) is { Tiles.Count: > 0 } saved && saved.Tiles.Count == map.Tiles.Count
                    ? map with { Tiles = saved.Tiles.ToArray() }
                    : map).ToArray();
            }
            if (_project?.OverworldLevelPointers is { } savedNodes)
            {
                _overworlds = _overworlds.Select(map => map with
                {
                    LevelPointers = map.LevelPointers.Select(pointer => savedNodes.FirstOrDefault(item => item.World == map.World && item.Index == pointer.Index) is { } saved
                        ? pointer with { Screen = saved.Screen, Column = saved.Column, Row = saved.Row, ObjectSet = saved.ObjectSet, LevelOffset = saved.LevelOffset, EnemyOffset = saved.EnemyOffset }
                        : pointer).ToArray()
                }).ToArray();
            }
            if (_project?.OverworldLocksAndBridges is { } savedLocks)
            {
                _overworlds = _overworlds.Select(map => map with
                {
                    LocksAndBridges = map.LocksAndBridges.Select(item =>
                        savedLocks.FirstOrDefault(saved => saved.World == map.World && saved.Slot == item.Slot) is { } saved
                            ? item with
                            {
                                Screen = saved.Screen,
                                Column = saved.Column,
                                Row = saved.Row,
                                ReplacementTile = saved.ReplacementTile
                            }
                            : item).ToArray()
                }).ToArray();
            }
            OverworldWorldBox.ItemsSource = _overworlds;
            OverworldWorldBox.SelectedIndex = 0;
            RefreshOverworldScreenChoices(_overworlds[0]);
            BuildOverworldTilePicker(_overworlds[0]);
            OverworldCanvas.PaintTile = _selectedOverworldTile;
            _overworldAnimationFrame = 0;
            _overworldAnimationTicks = _overworlds[0].AnimationSpeed;
            if (OverworldAnimationToggle.IsChecked == true) _overworldAnimationTimer.Start();
            OverworldWorldBox.IsVisible = true;
            OverworldScreenBox.IsVisible = _overworlds[0].ScreenCount > 1;
            SetOverworldEditMode(0);
            SetLevelToolbarVisibility(false);
            OverworldAnimationToggle.IsVisible = true;
            SidebarTabs.IsVisible = false;
            OverworldTilePicker.IsVisible = true;
            OverworldNodeEditor.IsVisible = false;
            LevelScrollViewer.IsVisible = false;
            OverworldScrollViewer.IsVisible = true;
            RenderSelectedOverworld();
            RomStatusText.Text = "Overworld tiles export as fixed-size vanilla PRG1 map edits.";
        }
        else
        {
            _overworldAnimationTimer.Stop();
            _overworldPaintStart = null;
            OverworldWorldBox.IsVisible = false;
            OverworldScreenBox.IsVisible = false;
            SetLevelToolbarVisibility(true);
            OverworldAnimationToggle.IsVisible = false;
            SidebarTabs.IsVisible = true;
            OverworldTilePicker.IsVisible = false;
            OverworldNodeEditor.IsVisible = false;
            OverworldLockEditor.IsVisible = false;
            _selectedOverworldNode = null;
            _selectedOverworldLock = null;
            OverworldScrollViewer.IsVisible = false;
            LevelScrollViewer.IsVisible = true;
        }
    }

    private void OverworldAnimationToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (OverworldAnimationToggle.IsChecked == true && OverworldToggle.IsChecked == true)
        {
            _overworldAnimationTimer.Start();
            return;
        }

        _overworldAnimationTimer.Stop();
    }

    private void OverworldPaintMode_Click(object? sender, RoutedEventArgs e) => SetOverworldEditMode(0);

    private void OverworldNodeMode_Click(object? sender, RoutedEventArgs e) => SetOverworldEditMode(1);

    private void OverworldLockMode_Click(object? sender, RoutedEventArgs e) => SetOverworldEditMode(2);

    private void SetOverworldEditMode(int mode)
    {
        OverworldPaintModeToggle.IsChecked = mode == 0;
        OverworldNodeModeToggle.IsChecked = mode == 1;
        OverworldLockModeToggle.IsChecked = mode == 2;
        OverworldCanvas.EditNodes = mode == 1;
        OverworldCanvas.EditLocks = mode == 2;
        if (mode != 1)
        {
            OverworldNodeEditor.IsVisible = false;
            _selectedOverworldNode = null;
        }
        if (mode != 2) { OverworldLockEditor.IsVisible = false; _selectedOverworldLock = null; }
        RomStatusText.Text = mode switch
        {
            1 => "Edit nodes: click a white outline to choose its destination, or drag it to move it.",
            2 => "Locks: click an orange outline, then choose its post-fortress replacement tile.",
            _ => "Paint tiles: select a tile, then click or drag on the map."
        };
    }

    private void SetLevelToolbarVisibility(bool visible)
    {
        LevelLabel.IsVisible = visible;
        LevelList.IsVisible = visible;
        PlayButton.IsVisible = visible;
        TracePlayLevelToggle.IsVisible = visible && TraceToolsEnabled;
        GridToggle.IsVisible = visible;
        ClearLevelButton.IsVisible = visible;
    }

    private void OverworldWorldBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OverworldWorldBox.SelectedItem is OverworldDocument selected &&
            _overworlds.FirstOrDefault(item => item.World == selected.World) is { } world)
        {
            RefreshOverworldScreenChoices(world);
            BuildOverworldTilePicker(world);
            _overworldAnimationFrame = 0;
            _overworldAnimationTicks = world.AnimationSpeed;
        }
        RenderSelectedOverworld();
    }

    private void OverworldScreenBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        OverworldCanvas.VisibleScreen = (OverworldScreenBox.SelectedItem as OverworldScreenChoice)?.Value ?? -1;
    }

    private void RefreshOverworldScreenChoices(OverworldDocument world)
    {
        OverworldScreenBox.ItemsSource = new[] { new OverworldScreenChoice(-1, "All screens") }
            .Concat(Enumerable.Range(0, world.ScreenCount).Select(index => new OverworldScreenChoice(index, $"Screen {index + 1}"))).ToArray();
        OverworldScreenBox.SelectedIndex = 0;
        OverworldScreenBox.IsVisible = world.ScreenCount > 1;
        OverworldCanvas.VisibleScreen = -1;
    }

    private void RenderSelectedOverworld()
    {
        if (_rom is null || OverworldWorldBox.SelectedItem is not OverworldDocument selected ||
            _overworlds.FirstOrDefault(item => item.World == selected.World) is not { } world) return;
        RenderOverworld(world);
    }

    private void RenderOverworld(OverworldDocument world)
    {
        if (_rom is null) return;
        var rendered = Smb3OverworldRenderer.Render(_rom, world, _overworldAnimationFrame, EffectiveOverworldPalettes());
        AddDiagnostics(rendered.Diagnostics);
        if (rendered.IsSuccess)
        {
            OverworldCanvas.World = world;
            OverworldCanvas.Snapshot = rendered.Value!;
        }
    }

    private void OpenOverworldPaletteEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (_rom is null || _project is null || OverworldCanvas.World is not { } world) return;
        if (_overworldPaletteEditor is null)
        {
            var tilePalettes = _overworlds.Select(static item => item.TilePalette).Append(world.TilePalette).Distinct().Order().ToArray();
            var spritePalettes = _overworlds.Select(static item => item.SpritePalette).Append(world.SpritePalette).Distinct().Order().ToArray();
            _overworldPaletteEditor = new OverworldPaletteEditorWindow(
                tilePalettes, spritePalettes, world.TilePalette, world.SpritePalette,
                GetOverworldPalette,
                (palette, sprites, colors) => PreviewOverworldPalette(world, palette, sprites, colors),
                CommitOverworldPalettes,
                () => { _overworldPaletteDrafts.Clear(); RenderOverworld(world); BuildOverworldTilePicker(world); });
            _overworldPaletteEditor.Closed += (_, _) => _overworldPaletteEditor = null;
            _overworldPaletteEditor.Show(this);
        }
        else _overworldPaletteEditor.Activate();
    }

    private IReadOnlyList<byte> GetOverworldPalette(int index, bool sprites)
    {
        if (_rom is null) return new byte[16];
        if (_overworldPaletteDrafts.TryGetValue((index, sprites), out var draft)) return draft.ToArray();
        var changed = _project?.OverworldPalettes?.LastOrDefault(item => item.Sprites == sprites && item.Palette == index);
        if (changed?.Colors.Count == 16) return changed.Colors.ToArray();
        var bank = _rom.Prg.Slice(27 * 0x2000, 0x2000);
        var pointer = bank[0x17D2] | (bank[0x17D3] << 8);
        var offset = pointer - 0xA000 + (index * 16);
        return offset >= 0 && offset <= bank.Length - 16 ? bank.Slice(offset, 16).ToArray() : new byte[16];
    }

    private void PreviewOverworldPalette(OverworldDocument world, int index, bool sprites, IReadOnlyList<byte> colors)
    {
        _overworldPaletteDrafts[(index, sprites)] = colors.Take(16).Concat(Enumerable.Repeat((byte)0, 16)).Take(16).ToArray();
        if (!sprites && index == world.TilePalette)
        {
            RenderOverworld(world);
            BuildOverworldTilePicker(world);
        }
    }

    private void CommitOverworldPalettes(IReadOnlyList<OverworldPaletteDraft> drafts)
    {
        if (_project is null) return;
        foreach (var draft in drafts) _project = _project.WithOverworldPalette(draft.Palette, draft.Sprites, draft.Colors);
        _overworldPaletteDrafts.Clear();
        MarkProjectChanged();
        UpdateSaveState();
        RenderSelectedOverworld();
        if (OverworldCanvas.World is { } current) BuildOverworldTilePicker(current);
    }

    private IReadOnlyList<OverworldPaletteOverride> EffectiveOverworldPalettes()
    {
        var committed = (_project?.OverworldPalettes ?? []).Where(item => !_overworldPaletteDrafts.ContainsKey((item.Palette, item.Sprites)));
        return committed.Concat(_overworldPaletteDrafts.Select(item => new OverworldPaletteOverride(item.Key.Palette, item.Key.Sprites, item.Value))).ToArray();
    }

    private void OverworldCanvas_LevelPointerSelected(object? sender, OverworldLevelPointer pointer)
    {
        _selectedOverworldNode = pointer;
        OverworldNodeTitle.Text = $"Map node {pointer.Index + 1}";
        OverworldNodeEditor.IsVisible = true;
        _refreshingOverworldNode = true;
        try
        {
            var choices = _rom?.Profile.Levels.Values.OrderBy(static level => level.DisplayName, StringComparer.Ordinal).ToArray() ?? [];
            OverworldNodeLevelList.ItemsSource = choices;
            OverworldNodeLevelList.SelectedItem = choices.FirstOrDefault(level => MatchesNodeDestination(pointer, level));
        }
        finally
        {
            _refreshingOverworldNode = false;
        }
        RomStatusText.Text = $"Map node {pointer.Index + 1}: choose a destination level.";
    }

    private void OverworldCanvas_LockBridgeSelected(object? sender, OverworldLockBridge item)
    {
        _selectedOverworldLock = item;
        OverworldLockTitle.Text = $"Lock / bridge ${item.Slot:X2}";
        OverworldLockEditor.IsVisible = true;
        RomStatusText.Text = $"Lock / bridge ${item.Slot:X2}: select a map tile, then apply its post-fortress replacement.";
    }

    private void ApplyLockReplacement_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _selectedOverworldLock is null || OverworldCanvas.World is not { } world) return;
        var current = world.LocksAndBridges.FirstOrDefault(item => item.Slot == _selectedOverworldLock.Slot);
        if (current is null) return;

        var nextLock = current with { ReplacementTile = OverworldCanvas.PaintTile };
        var next = world.WithLockBridge(nextLock);
        _overworlds = _overworlds.Select(item => item.World == next.World ? next : item).ToArray();
        _project = _project.WithOverworldLockBridge(nextLock);
        _selectedOverworldLock = nextLock;
        _isProjectDirty = true;
        UpdateSaveState();
        RenderOverworld(next);
        RomStatusText.Text = $"Lock / bridge ${nextLock.Slot:X2} will replace with tile ${nextLock.ReplacementTile:X2}.";
    }

    private void OverworldNodeLevelList_SelectionChanged(object? sender, EventArgs e)
    {
        if (_refreshingOverworldNode || _rom is null || _project is null || _selectedOverworldNode is null ||
            OverworldNodeLevelList.SelectedItem is not LevelLocation target || OverworldCanvas.World is not { } world ||
            !TryGetMapPointers(target, out var layoutPointer, out var enemyPointer)) return;

        var current = world.LevelPointers.ElementAtOrDefault(_selectedOverworldNode.Index);
        if (current is null) return;
        var nextPointer = current with { ObjectSet = target.Tileset, LevelOffset = layoutPointer, EnemyOffset = enemyPointer };
        var next = world.WithLevelPointer(nextPointer);
        _overworlds = _overworlds.Select(item => item.World == next.World ? next : item).ToArray();
        _project = _project.WithOverworldLevelPointer(next.World, nextPointer);
        _selectedOverworldNode = nextPointer;
        _isProjectDirty = true;
        UpdateSaveState();
        RenderOverworld(next);
        RomStatusText.Text = $"Map node {nextPointer.Index + 1} now enters {target.DisplayName}.";
    }

    private bool MatchesNodeDestination(OverworldLevelPointer pointer, LevelLocation level) =>
        pointer.ObjectSet == level.Tileset && TryGetMapPointers(level, out var layoutPointer, out var enemyPointer) &&
        pointer.LevelOffset == layoutPointer && pointer.EnemyOffset == enemyPointer;

    private bool TryGetMapPointers(LevelLocation level, out ushort layoutPointer, out ushort enemyPointer)
    {
        layoutPointer = enemyPointer = 0;
        if (_rom is null) return false;
        var layoutRelative = level.LayoutOffset - _rom.PrgOffset;
        var enemyRelative = level.EnemyOffset - _rom.PrgOffset;
        if (layoutRelative < 0 || enemyRelative < 0) return false;
        layoutPointer = (ushort)(0xA000 + (layoutRelative & 0x1FFF));
        enemyPointer = (ushort)(0xC000 + (enemyRelative & 0x1FFF));
        return true;
    }

    private void OverworldCanvas_ZoomRequested(object? sender, OverworldZoomRequestedEventArgs e)
    {
        var oldOffset = OverworldScrollViewer.Offset;
        var viewportPoint = new Avalonia.Point(e.Pointer.X - oldOffset.X, e.Pointer.Y - oldOffset.Y);
        OverworldCanvas.Zoom = e.NewZoom;
        OverworldScrollViewer.UpdateLayout();
        var targetX = (e.LogicalPoint.X * e.NewZoom) - viewportPoint.X;
        var targetY = (e.LogicalPoint.Y * e.NewZoom) - viewportPoint.Y;
        SetOverworldScrollOffset(targetX, targetY);
        var sequence = ++_zoomSequence;
        Dispatcher.UIThread.Post(() =>
        {
            if (sequence != _zoomSequence) return;
            OverworldScrollViewer.UpdateLayout();
            SetOverworldScrollOffset(targetX, targetY);
        }, DispatcherPriority.Render);
    }

    private void OverworldCanvas_PanRequested(object? sender, OverworldPanRequestedEventArgs e)
    {
        var offset = OverworldScrollViewer.Offset;
        SetOverworldScrollOffset(offset.X - e.Delta.X, offset.Y - e.Delta.Y);
    }

    private void OverworldCanvas_NodeMoveStarted(object? sender, EventArgs e) =>
        _overworldNodeMoveStart = OverworldCanvas.World;

    private void OverworldCanvas_NodeMoveRequested(object? sender, OverworldNodeMoveRequestEventArgs e)
    {
        if (_project is null || OverworldCanvas.World is not { } world || e.Node.Index < 0 || e.Node.Index >= world.LevelPointers.Count) return;
        var current = world.LevelPointers[e.Node.Index];
        if (current.Screen == e.Screen && current.Column == e.Column && current.Row == e.Row)
        {
            e.Accepted = true;
            return;
        }
        if (world.LevelPointers.Any(node => node.Index != current.Index && node.Screen == e.Screen && node.Column == e.Column && node.Row == e.Row))
        {
            RomStatusText.Text = "That map tile already has a node. Move it to an empty tile.";
            return;
        }

        var nextPointer = current with { Screen = e.Screen, Column = e.Column, Row = e.Row };
        var next = world.WithLevelPointer(nextPointer);
        _overworlds = _overworlds.Select(item => item.World == next.World ? next : item).ToArray();
        _project = _project.WithOverworldLevelPointer(next.World, nextPointer);
        _selectedOverworldNode = nextPointer;
        _isProjectDirty = true;
        UpdateSaveState();
        RenderOverworld(next);
        e.Accepted = true;
    }

    private void OverworldCanvas_NodeMoveCompleted(object? sender, EventArgs e)
    {
        if (_overworldNodeMoveStart is { } before && OverworldCanvas.World is { } after &&
            before.World == after.World && !before.LevelPointers.SequenceEqual(after.LevelPointers))
        {
            _overworldHistory.Record(before);
        }
        _overworldNodeMoveStart = null;
    }

    private void BuildOverworldTilePicker(OverworldDocument world)
    {
        if (_rom is null) return;
        var entries = new List<OverworldTileEntry>(256);
        for (var index = 0; index < 256; index++)
        {
            var preview = Smb3OverworldRenderer.RenderTilePreview(_rom, world, (byte)index, EffectiveOverworldPalettes());
            if (preview.IsSuccess)
            {
                var pixels = preview.Value!.ArgbPixels;
                entries.Add(new OverworldTileEntry((byte)index, new CatalogPreviewData(16, 16, pixels), pixels.Distinct().Take(2).Count() == 1));
            }
        }
        _overworldTileEntries = entries;
        ApplyOverworldTileFilter();
    }

    private void OverworldTileFilter_Changed(object? sender, RoutedEventArgs e) => ApplyOverworldTileFilter();

    private void ApplyOverworldTileFilter()
    {
        // SelectionChanged can fire while XAML is still constructing the
        // neighbouring controls. Defer filtering until the picker exists.
        if (OverworldTileGroupBox is null || OverworldShowBlankTilesToggle is null || OverworldTileGrid is null) return;
        var group = OverworldTileGroupBox.SelectedIndex;
        var showBlanks = OverworldShowBlankTilesToggle.IsChecked == true || group == 5;
        var entries = _overworldTileEntries.Where(item =>
            (showBlanks || !item.IsBlank) &&
            (group is 0 or 5 || item.PaletteGroup == group)).ToArray();
        OverworldTileGrid.ItemsSource = entries;
        OverworldTileGrid.SelectedItem = entries.FirstOrDefault(item => item.Value == _selectedOverworldTile) ?? entries.FirstOrDefault();
    }

    private void OverworldTileGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OverworldTileGrid.SelectedItem is not OverworldTileEntry tile) return;
        _selectedOverworldTile = tile.Value;
        OverworldCanvas.PaintTile = tile.Value;
    }

    private void OverworldCanvas_TilePaintRequested(object? sender, IReadOnlyList<(int X, int Y)> points)
    {
        if (OverworldCanvas.World is not { } world || _project is null || points.Count == 0) return;
        var next = world;
        foreach (var point in points) next = next.WithTile(point.X, point.Y, OverworldCanvas.PaintTile);
        if (next.Tiles.SequenceEqual(world.Tiles)) return;
        _overworlds = _overworlds.Select(item => item.World == world.World ? next : item).ToArray();
        _project = _project.WithOverworld(next);
        _isProjectDirty = true;
        UpdateSaveState();
        var rendered = _rom is null ? null : Smb3OverworldRenderer.Render(_rom, next, _overworldAnimationFrame);
        if (rendered is { IsSuccess: true })
        {
            OverworldCanvas.World = next;
            OverworldCanvas.Snapshot = rendered.Value!;
        }
    }

    private void OverworldCanvas_PaintStarted(object? sender, EventArgs e) =>
        _overworldPaintStart = OverworldCanvas.World;

    private void OverworldCanvas_PaintCompleted(object? sender, EventArgs e)
    {
        if (_overworldPaintStart is { } before && OverworldCanvas.World is { } after &&
            before.World == after.World && !before.Tiles.SequenceEqual(after.Tiles))
        {
            _overworldHistory.Record(before);
        }
        _overworldPaintStart = null;
    }

    private void OverworldAnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (OverworldToggle.IsChecked != true || OverworldWorldBox.SelectedItem is not OverworldDocument world || world.AnimationSpeed <= 0) return;
        if (--_overworldAnimationTicks >= 0) return;
        _overworldAnimationFrame = world.World == 2
            ? (_overworldAnimationFrame + 1) & 1
            : (_overworldAnimationFrame + 1) & 3;
        _overworldAnimationTicks = world.AnimationSpeed;
        RenderSelectedOverworld();
    }

    private void SetActiveRenderDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        _activeRenderDiagnostics = diagnostics;
        RefreshDiagnosticsList();
        var error = diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        SafetyBanner.IsVisible = error is not null;
        FixUnsafeButton.IsVisible = error is not null && EditorCanvas.CanFixActiveIssue;
        if (error is not null)
        {
            // A safety state is the primary chrome message; do not show a second
            // warning beside it for the same condition.
            DesignerNotice.IsVisible = false;
            string name = "Level state";
            if (TryReadElementIndex(error.Code, out var index) && EditorCanvas.Document?.Elements.FirstOrDefault(item => item.Index == index) is { } element)
            {
                name = ObjectCatalogNames.Describe(EditorCanvas.Document.Tileset, element).Split('\n')[0];
            }
            SafetyBannerText.Text = $"Unsafe: {name}";
        }
    }

    private void FixUnsafe_Click(object? sender, RoutedEventArgs e)
    {
        if (EditorCanvas.TryFixActiveIssue())
        {
            FixUnsafeButton.IsVisible = false;
        }
    }

    private static bool TryReadElementIndex(string code, out int index)
    {
        const string marker = ":ELEMENT:";
        var position = code.IndexOf(marker, StringComparison.Ordinal);
        return int.TryParse(position >= 0 ? code[(position + marker.Length)..] : null, out index);
    }

    private void SafetyBanner_Click(object? sender, RoutedEventArgs e)
    {
        DiagnosticsPanel.IsExpanded = true;
    }

    private void GridToggle_Click(object? sender, RoutedEventArgs e)
    {
        EditorCanvas.ShowGrid = GridToggle.IsChecked == true;
        EditorCanvas.InvalidateVisual();
    }

    private void EditorCanvas_ZoomRequested(object? sender, CanvasZoomRequestedEventArgs e)
    {
        var oldOffset = LevelScrollViewer.Offset;
        var viewportPoint = new Avalonia.Point(e.Pointer.X - oldOffset.X, e.Pointer.Y - oldOffset.Y);
        EditorCanvas.Zoom = e.NewZoom;
        LevelScrollViewer.UpdateLayout();
        var targetX = (e.LogicalPoint.X * e.NewZoom) - viewportPoint.X;
        var targetY = (e.LogicalPoint.Y * e.NewZoom) - viewportPoint.Y;
        SetScrollOffset(targetX, targetY);
        var sequence = ++_zoomSequence;
        Dispatcher.UIThread.Post(() =>
        {
            if (sequence != _zoomSequence) return;
            LevelScrollViewer.UpdateLayout();
            SetScrollOffset(targetX, targetY);
        }, DispatcherPriority.Render);
    }

    private void EditorCanvas_PanRequested(object? sender, CanvasPanRequestedEventArgs e)
    {
        var offset = LevelScrollViewer.Offset;
        SetScrollOffset(offset.X - e.Delta.X, offset.Y - e.Delta.Y);
    }

    private void EditorCanvas_EdgeScrollRequested(object? sender, CanvasEdgeScrollRequestedEventArgs e)
    {
        var point = e.CanvasPoint - new Avalonia.Vector(LevelScrollViewer.Offset.X, LevelScrollViewer.Offset.Y);
        const double edge = 28;
        const double step = 18;
        var offset = LevelScrollViewer.Offset;
        var x = offset.X;
        var y = offset.Y;
        if (point.X < edge) x = Math.Max(0, x - step);
        else if (point.X > LevelScrollViewer.Bounds.Width - edge) x += step;
        if (point.Y < edge) y = Math.Max(0, y - step);
        else if (point.Y > LevelScrollViewer.Bounds.Height - edge) y += step;
        offset = new Avalonia.Vector(x, y);
        LevelScrollViewer.Offset = offset;
    }

    private void SetScrollOffset(double x, double y) =>
        LevelScrollViewer.Offset = new Avalonia.Vector(
            Math.Clamp(x, 0, Math.Max(0, LevelScrollViewer.Extent.Width - LevelScrollViewer.Viewport.Width)),
            Math.Clamp(y, 0, Math.Max(0, LevelScrollViewer.Extent.Height - LevelScrollViewer.Viewport.Height)));

    private void SetOverworldScrollOffset(double x, double y) =>
        OverworldScrollViewer.Offset = new Avalonia.Vector(
            Math.Clamp(x, 0, Math.Max(0, OverworldScrollViewer.Extent.Width - OverworldScrollViewer.Viewport.Width)),
            Math.Clamp(y, 0, Math.Max(0, OverworldScrollViewer.Extent.Height - OverworldScrollViewer.Viewport.Height)));

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
        _activeAreaId = null;
        _catalogPreviewCache.Clear();
        EditorCanvas.SourceRom = _rom;
        _appSettings = _appSettings with { LastRomPath = _rom.SourcePath };
        AddDiagnostics(AppSettingsStore.Save(_appSettings).Diagnostics);
        if (createProject)
        {
            _project = ProjectDocumentV2.Create(_rom);
            _projectPath = null;
            _savedProject = _project;
            _isProjectDirty = false;
            UpdateSaveState();
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

    private async Task<bool> SaveProjectAsync(bool forcePicker)
    {
        if (_project is null)
        {
            AddDiagnostics([Diagnostics.Error("PROJECT_NONE", "Open a verified ROM before saving a project.")]);
            return false;
        }

        UpdateProjectEditorSettings();
        if (forcePicker || _projectPath is null)
        {
            Directory.CreateDirectory(WorkspacePaths.ProjectsDirectory);
            var projectsFolder = await StorageProvider.TryGetFolderFromPathAsync(WorkspacePaths.ProjectsDirectory);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Warp Whistle project",
                SuggestedFileName = "My-SMB3-Hack.wwproj",
                DefaultExtension = "wwproj",
                SuggestedStartLocation = projectsFolder,
                FileTypeChoices = [new FilePickerFileType("Warp Whistle project") { Patterns = ["*.wwproj"] }]
            });
            if (file is null)
            {
                return false;
            }

            _projectPath = file.Path.LocalPath;
        }

        var saved = ProjectStore.Save(_project, _projectPath);
        AddDiagnostics(saved.Diagnostics);
        if (saved.IsSuccess)
        {
            _savedProject = _project;
            _isProjectDirty = false;
            UpdateSaveState();
        }
        return saved.IsSuccess;
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
        if (OverworldToggle.IsChecked == true && OverworldWorldBox.SelectedItem is OverworldDocument world && _overworldHistory.CanUndo)
        {
            ApplyOverworldHistory(_overworldHistory.Undo(world));
            return;
        }
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
        if (OverworldToggle.IsChecked == true && OverworldWorldBox.SelectedItem is OverworldDocument world && _overworldHistory.CanRedo)
        {
            ApplyOverworldHistory(_overworldHistory.Redo(world));
            return;
        }
        if (_document is null || !_history.CanRedo)
        {
            return;
        }

        _document = _history.Redo(_document);
        SetDocument(_document, clearHistory: false);
        MarkProjectChanged();
    }

    private void ApplyOverworldHistory(OverworldDocument world)
    {
        if (_project is null) return;
        _overworlds = _overworlds.Select(item => item.World == world.World ? world : item).ToArray();
        _project = _project.WithOverworld(world);
        _isProjectDirty = true;
        UpdateSaveState();
        var rendered = _rom is null ? null : Smb3OverworldRenderer.Render(_rom, world, _overworldAnimationFrame);
        if (rendered is { IsSuccess: true })
        {
            OverworldCanvas.World = world;
            OverworldCanvas.Snapshot = rendered.Value!;
        }
    }

    private void SetDocument(LevelDocument document, bool clearHistory)
    {
        _document = document;
        _activeAreaId = document.AreaId;
        EditorCanvas.FourByteGeneratorIds = _rom?.Profile.Levels.TryGetValue(document.AreaId, out var location) == true
            ? location.FourByteGeneratorIds
            : new HashSet<int>();
        EditorCanvas.Document = document;
        EditorCanvas.PaletteOverrides = _project?.PaletteOverrides ?? [];
        RefreshCatalog(document.Tileset);
        if (clearHistory)
        {
            _history.Clear();
        }

        RefreshInspector();
        RefreshPatches();
        EditorCanvas.RefreshEnemyValidation();
    }

    private void RefreshPatches()
    {
        _refreshingPatches = true;
        try
        {
            var settings = _project?.Patches ?? PatchSettings.None;
            var catalog = PatchCatalog.Discover();
            var definitions = catalog.IsSuccess ? catalog.Value!.SelectMany(static package => package.Features).ToArray() : [];
            var included = definitions.Where(definition => settings.Get(definition.Id) is not null).ToArray();
            NoPatchesText.IsVisible = included.Length == 0;
            PatchOverrideList.Children.Clear();
            _patchOverrideIds.Clear();
            var choices = new[]
            {
                new PatchOverrideOption("Inherit", null),
                new PatchOverrideOption("Enabled", true),
                new PatchOverrideOption("Disabled", false)
            };
            var areaId = _document?.AreaId;
            var perLevel = included.Where(static definition => definition.SupportsLevelOverrides).ToArray();
            PatchLevelText.Text = areaId is null ? "Select a level to set an override." : perLevel.Length == 0 ? "Included patches are global-only." : "Level override";
            foreach (var definition in perLevel)
            {
                var setting = settings.Get(definition.Id)!;
                bool? selected = areaId is not null && setting.LevelOverrides is not null && setting.LevelOverrides.TryGetValue(areaId, out var value) ? value : null;
                var box = new ComboBox { Width = 120, ItemsSource = choices, SelectedItem = choices.First(item => item.Value == selected) };
                box.IsEnabled = areaId is not null && definition.SupportedProfiles.Contains(_rom?.Profile.Id ?? "", StringComparer.Ordinal);
                box.SelectionChanged += PatchOverrideChanged;
                _patchOverrideIds[box] = definition.Id;
                PatchOverrideList.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#182534")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#3A506B")),
                    BorderThickness = new Avalonia.Thickness(1),
                    Padding = new Avalonia.Thickness(6),
                    Child = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        ColumnSpacing = 8,
                        Children =
                        {
                            new TextBlock { Text = definition.DisplayName, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                            box
                        }
                    }
                });
                Grid.SetColumn(box, 1);
            }
            PatchControls.IsEnabled = _project is not null;
        }
        finally
        {
            _refreshingPatches = false;
        }
    }

    private async void OpenPatchManager_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        var catalog = PatchCatalog.Discover();
        if (!catalog.IsSuccess)
        {
            AddDiagnostics(catalog.Diagnostics);
            return;
        }

        var manager = new PatchManagerWindow(_project.Patches ?? PatchSettings.None, _project.ExternalPatches ?? [], (settings, externalPatches) =>
        {
            _project = _project with { Patches = settings, ExternalPatches = externalPatches };
            MarkPatchesChanged();
        });
        await manager.ShowDialog(this);
    }

    private void PatchOverrideChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPatches || _project is null || _document is null || sender is not ComboBox box || box.SelectedItem is not PatchOverrideOption option) return;
        var settings = _project.Patches ?? PatchSettings.None;
        // Keep patches that have not been included in the project absent. A
        // level override applies only to the visible, included patch.
        if (!_patchOverrideIds.TryGetValue(box, out var id) || settings.Get(id) is not { } setting) return;
        _project = _project with { Patches = settings.With(id, WithLevelOverride(setting, _document.AreaId, option.Value)) };
        MarkPatchesChanged();
    }

    private static PatchSetting WithLevelOverride(PatchSetting setting, string areaId, bool? value)
    {
        var overrides = new Dictionary<string, bool>(setting.LevelOverrides ?? new Dictionary<string, bool>(), StringComparer.Ordinal);
        if (value is bool enabled) overrides[areaId] = enabled;
        else overrides.Remove(areaId);
        return setting with { LevelOverrides = overrides };
    }

    private void MarkPatchesChanged()
    {
        UpdateProjectEditorSettings();
        _isProjectDirty = true;
        UpdateSaveState();
        RefreshPatches();
    }

    private void MarkProjectChanged()
    {
        if (_project is null || _document is null)
        {
            return;
        }

        _project = _project.WithArea(_document);
        UpdateProjectEditorSettings();
        _isProjectDirty = true;
        UpdateSaveState();
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

    private async Task<bool> ResolveUnsavedChangesAsync(string action)
    {
        if (!_isProjectDirty) return true;
        var choice = await new UnsavedChangesDialog(action).ShowDialog<UnsavedChangesChoice>(this);
        return choice switch
        {
            UnsavedChangesChoice.Save => await SaveProjectAsync(forcePicker: _projectPath is null),
            UnsavedChangesChoice.Discard => DiscardUnsavedChanges(),
            _ => false
        };
    }

    private bool DiscardUnsavedChanges()
    {
        if (_savedProject is not null) _project = _savedProject;
        _isProjectDirty = false;
        UpdateSaveState();
        return true;
    }

    private void UpdateSaveState()
    {
        if (SaveButton is null || SaveStateText is null) return;
        SaveButton.IsEnabled = _project is not null;
        if (UndoButton is not null) UndoButton.IsEnabled = _history.CanUndo;
        if (RedoButton is not null) RedoButton.IsEnabled = _history.CanRedo;
        SaveStateText.Text = _isProjectDirty ? "Unsaved changes" : "Saved";
        SaveStateText.Foreground = _isProjectDirty ? Avalonia.Media.Brushes.Gold : Avalonia.Media.Brushes.MediumSeaGreen;
        Title = _isProjectDirty ? "Warp Whistle *" : "Warp Whistle";
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_closingApproved || !_isProjectDirty)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Dispatcher.UIThread.Post(async () =>
        {
            if (!await ResolveUnsavedChangesAsync("closing Warp Whistle")) return;
            _closingApproved = true;
            Close();
        });
        base.OnClosing(e);
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
        if (_document is null || _rom is null)
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
        var palette = Smb3LevelRenderer.ReadPaletteIndices(_rom, _document, false, _document.Header.BackgroundPalette);
        _editingPaletteColors = palette.IsSuccess ? palette.Value!.ToArray() : new byte[16];
        RefreshPaletteSwatches();
        MusicBox.SelectedItem = MusicChoices.First(item => item.Value == _document.Header.Music);
        TimeBox.SelectedItem = TimeChoices.First(item => item.Value == _document.Header.TimeSetting);
        _refreshingInspector = false;
        var layout = Smb3LevelCodec.EncodeLayout(_document);
        var enemies = Smb3LevelCodec.EncodeEnemies(_document);
        UpdateByteBudget(layout.Value?.Length ?? 0, _document.OriginalLayoutLength, enemies.Value?.Length ?? 0, _document.OriginalEnemyLength);
        SpaceText.Text = $"Layout: {layout.Value?.Length ?? 0} / {_document.OriginalLayoutLength} bytes\n" +
                         $"Sprites: {enemies.Value?.Length ?? 0} / {_document.OriginalEnemyLength} bytes\n" +
                         $"Used by: {string.Join(", ", _document.UsedBy)}";
    }

    private void UpdateByteBudget(int layoutUsed, int layoutCapacity, int enemyUsed, int enemyCapacity)
    {
        var layoutLeft = layoutCapacity - layoutUsed;
        var enemyLeft = enemyCapacity - enemyUsed;
        ByteBudgetText.Text = $"Level {layoutUsed}/{layoutCapacity} · Sprites {enemyUsed}/{enemyCapacity}";
        ByteBudgetText.Foreground = layoutLeft < 0 || enemyLeft < 0
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF6B6B"))
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9FB0C4"));
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
        var all = _diagnostics
            .Concat(_activeRenderDiagnostics)
            .Concat(_activePersistentFeedback is null
                ? []
                : [new Diagnostic(
                    _activePersistentFeedback.Severity,
                    "EDITOR_ACTION",
                    _activePersistentFeedback.Details)])
            .AsEnumerable()
            .Reverse()
            .ToArray();
        DiagnosticsList.ItemsSource = all.Select(static diagnostic => diagnostic.ToString()).ToArray();
        var warnings = all.Count(item => item.Severity == DiagnosticSeverity.Warning);
        var errors = all.Count(item => item.Severity == DiagnosticSeverity.Error);
        DiagnosticsCountText.Text = errors > 0 ? $"({errors} error{(errors == 1 ? string.Empty : "s")})" :
            warnings > 0 ? $"({warnings} warning{(warnings == 1 ? string.Empty : "s")})" : string.Empty;
    }

    private void RefreshCatalog(int tileset)
    {
        _catalogPreviewCancellation?.Cancel();
        _catalog.Clear();
        _catalog.AddRange(BuildCatalog(tileset));
        var missing = new List<CatalogEntry>();
        for (var index = 0; index < _catalog.Count; index++)
        {
            var entry = _catalog[index];
            if (_rom is not null && _document is not null && TryGetCatalogPreview(entry, tileset, _rom, _document, out var preview))
                _catalog[index] = entry with { Preview = preview };
            else
                missing.Add(entry);
        }
        ApplyCatalogFilter();
        QueueCatalogPreviews(missing, tileset);
    }

    private void ApplyCatalogFilter()
    {
        if (CatalogGrid is null || CatalogListView is null) return;
        var query = CatalogSearchBox?.Text?.Trim() ?? string.Empty;
        var type = _catalogFilter;
        var source = type switch
        {
            3 => _recentCatalog.AsEnumerable(),
            4 => UsedCatalogEntries(),
            _ => _catalog
        };
        var scoped = source.Where(item => type is 0 or 3 or 4 ||
                                                  type == 1 && !item.IsEnemy ||
                                                  type == 2 && item.IsEnemy);
        var displayed = _groupCatalogVariants
            ? GroupCatalogEntries(scoped, expandFamilies: type is 3 or 4)
            : OrderUnfurledCatalogEntries(scoped);
        var filtered = displayed
            .Where(item => string.IsNullOrWhiteSpace(query) ||
                           item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           $"${item.Id:X2}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _refreshingCatalog = true;
        CatalogGrid.ItemsSource = filtered;
        CatalogListView.ItemsSource = filtered;
        // A canvas selection deliberately clears the catalog paste source so
        // right-click duplicates the selected level item. Do not let an async
        // preview/filter refresh silently make the first visible catalog card
        // active again.
        _activeCatalogEntry = _activeCatalogEntry is null
            ? null
            : filtered.FirstOrDefault(item => item.SameIdentity(_activeCatalogEntry));
        CatalogGrid.SelectedItem = _activeCatalogEntry;
        CatalogListView.SelectedItem = _activeCatalogEntry;
        _refreshingCatalog = false;
    }

    private IEnumerable<CatalogEntry> GroupCatalogEntries(IEnumerable<CatalogEntry> source, bool expandFamilies)
    {
        var entries = source.ToArray();
        var firstIndexByFamily = entries
            .Select((entry, index) => (Entry: entry, Index: index))
            .Where(item => item.Entry.FamilySortId is not null)
            .GroupBy(item => item.Entry.FamilySortId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Index), StringComparer.Ordinal);
        var firstIndexByNeighborhood = entries
            .Select((entry, index) => (Entry: entry, Index: index))
            .Where(item => item.Entry.NeighborhoodSortId is not null)
            .GroupBy(item => item.Entry.NeighborhoodSortId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Index), StringComparer.Ordinal);
        var groups = entries
            .GroupBy(item => item.FamilyId ?? $"single:{item.IsEnemy}:{item.IsVariable}:{item.Id}")
            .Select((group, index) => (Group: group, Index: index))
            .OrderBy(item => item.Group.First().NeighborhoodSortId is { } neighborhood && firstIndexByNeighborhood.TryGetValue(neighborhood, out var first)
                ? first
                : item.Index)
            .ThenBy(item => item.Group.First().FamilySortId is { } family && firstIndexByFamily.TryGetValue(family, out var first)
                ? first
                : item.Index)
            .ThenBy(item => item.Index);
        foreach (var grouped in groups)
        {
            var group = grouped.Group;
            var present = group.Select(item => item.AsVariant()).ToArray();
            var firstPresent = present[0];
            var variants = expandFamilies && firstPresent.FamilyId is not null
                ? _catalog.Where(item => item.FamilyId == firstPresent.FamilyId).Select(item => item.AsVariant()).ToArray()
                : present;
            if (variants.Length < 2 || firstPresent.FamilyId is null)
            {
                yield return firstPresent;
                continue;
            }

            var selected = _catalogVariantSelections.TryGetValue(firstPresent.FamilyId, out var identity)
                ? variants.FirstOrDefault(item => item.Identity == identity) ?? firstPresent
                : variants.FirstOrDefault(item => _activeCatalogEntry is not null && item.SameIdentity(_activeCatalogEntry)) ?? firstPresent;
            yield return selected with { Variants = variants };
        }
    }

    private static IEnumerable<CatalogEntry> OrderUnfurledCatalogEntries(IEnumerable<CatalogEntry> source)
    {
        // Preserve the catalog's natural order, but keep configured families
        // adjacent even when they are displayed individually.
        var entries = source.Select((entry, index) => (Entry: entry.AsVariant(), Index: index)).ToArray();
        var firstIndexByFamily = entries
            .Where(item => item.Entry.FamilySortId is not null)
            .GroupBy(item => item.Entry.FamilySortId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Index), StringComparer.Ordinal);
        var firstIndexByNeighborhood = entries
            .Where(item => item.Entry.NeighborhoodSortId is not null)
            .GroupBy(item => item.Entry.NeighborhoodSortId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Index), StringComparer.Ordinal);
        return entries
            .OrderBy(item => item.Entry.NeighborhoodSortId is { } neighborhood && firstIndexByNeighborhood.TryGetValue(neighborhood, out var first) ? first : item.Index)
            .ThenBy(item => item.Entry.FamilySortId is { } family && firstIndexByFamily.TryGetValue(family, out var first) ? first : item.Index)
            .ThenBy(item => item.Index)
            .Select(item => item.Entry);
    }

    private IEnumerable<CatalogEntry> UsedCatalogEntries()
    {
        if (_document is null) return [];

        var used = new HashSet<(bool IsEnemy, bool IsVariable, int Id)>(
            _document.Elements
                .Where(element => element.Kind is LevelElementKind.FixedGenerator or LevelElementKind.VariableGenerator)
                .Select(element => (false, element.Kind == LevelElementKind.VariableGenerator, element.GeneratorId))
                .Concat(_document.Enemies.Select(enemy => (true, false, (int)enemy.Id))));
        return _catalog.Where(entry => used.Contains((entry.IsEnemy, entry.IsVariable, entry.Id)));
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

    private List<CatalogEntry> BuildCatalog(int tileset)
    {
        // Keep variable generators together and first. This makes shared
        // structures such as ceiling/wall pipes discoverable instead of
        // burying them after the entire fixed-object catalog.
        var items = ObjectCatalogNames.VariableForTileset(tileset)
            .Select(item => CreateCatalogEntry(tileset, false, true, item.Id, item.Name));
        var fixedItems = ObjectCatalogNames.ForTileset(tileset)
            .Select(item => CreateCatalogEntry(tileset, false, false, item.Id, item.Name));
        var allItems = items.Concat(fixedItems).ToList();
        allItems.AddRange(Smb3LevelRenderer.EnemyCatalog
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(item => CreateCatalogEntry(tileset, true, false, item.Key, item.Value)));
        return allItems;
    }

    private CatalogEntry CreateCatalogEntry(int tileset, bool isEnemy, bool isVariable, int id, string name)
    {
        var family = _catalogVariantFamilies.Find(tileset, isEnemy, isVariable, name);
        return new CatalogEntry(isEnemy, isVariable, id, name, $"{name} (${id:X2}, {id})",
            FamilyId: family?.Id, FamilySortId: family?.FamilySortId, NeighborhoodSortId: family?.NeighborhoodSortId,
            FamilyName: family?.Name, SuppressPreview: family?.HidePreview ?? false);
    }

    private void QueueCatalogPreviews(IReadOnlyList<CatalogEntry> entries, int tileset)
    {
        if (_rom is null || _document is null) return;
        if (entries.Count == 0)
        {
            return;
        }
        var cancellation = _catalogPreviewCancellation = new CancellationTokenSource();
        var rom = _rom;
        var document = _document;
        _ = Task.Run(async () =>
        {
            foreach (var batch in entries.Chunk(12))
            {
                if (cancellation.IsCancellationRequested) return;
                var rendered = batch.Select(entry => (entry, Preview: GetCatalogPreview(entry, tileset, rom, document))).ToArray();
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellation.IsCancellationRequested) return;
                    foreach (var (entry, preview) in rendered)
                    {
                        var index = _catalog.FindIndex(item => item.IsEnemy == entry.IsEnemy && item.IsVariable == entry.IsVariable && item.Id == entry.Id);
                        if (index >= 0) _catalog[index] = entry with { Preview = preview };
                    }
                    ApplyCatalogFilter();
                });
                await Task.Delay(25, cancellation.Token).ConfigureAwait(false);
            }
        }, cancellation.Token);
    }

    private CatalogPreviewData? GetCatalogPreview(CatalogEntry entry, int tileset, RomImage rom, LevelDocument document)
    {
        if (TryGetCatalogPreview(entry, tileset, rom, document, out var cached)) return cached;
        var preview = BuildCatalogPreview(entry, tileset, rom, document, _project?.PaletteOverrides);
        _catalogPreviewCache.Add(CatalogPreviewKey(entry, tileset, rom, document), preview);
        return preview;
    }

    private bool TryGetCatalogPreview(CatalogEntry entry, int tileset, RomImage rom, LevelDocument document, out CatalogPreviewData? preview) =>
        _catalogPreviewCache.TryGet(CatalogPreviewKey(entry, tileset, rom, document), out preview);

    private string CatalogPreviewKey(CatalogEntry entry, int tileset, RomImage rom, LevelDocument document)
    {
        var overrides = string.Join(",", (_project?.PaletteOverrides ?? [])
            .Where(item => item.Tileset == tileset)
            .OrderBy(item => item.Objects).ThenBy(item => item.Slot)
            .Select(item => $"{item.Objects}:{item.Slot}:{Convert.ToHexString(item.Colors.ToArray())}"));
        return $"{rom.Sha1}:{tileset}:{document.Header.BackgroundPalette}:{document.Header.ObjectPalette}:{overrides}:{entry.IsEnemy}:{entry.IsVariable}:{entry.Id}";
    }

    private static int CachePreviewId(CatalogEntry entry) => entry.Id + (entry.IsVariable ? 0x1000 : 0);

    private static CatalogPreviewData? BuildCatalogPreview(CatalogEntry entry, int tileset, RomImage rom, LevelDocument document, IReadOnlyList<PaletteOverride>? paletteOverrides)
    {
        if (entry.SuppressPreview) return null;
        try
        {
            var sample = document with
            {
                Tileset = tileset,
                Elements = entry.IsEnemy ? [] : CreatePreviewElements(entry, document, rom),
                Enemies = entry.IsEnemy ? [new EnemyElement(0, (byte)entry.Id, 1, 1)] : []
            };
            var rendered = new Smb3LevelRenderer().Render(rom, sample, paletteOverrides: paletteOverrides);
            if (!rendered.IsSuccess) return null;
            var snapshot = rendered.Value!;
            if (entry.IsEnemy)
            {
                if (snapshot.EnemySprites.TryGetValue((byte)entry.Id, out var sprite))
                    return ToThumbnail(sprite.PixelWidth, sprite.PixelHeight, sprite.ArgbPixels);
                if (FoundryEnemyPreviewCatalog.TryGet(entry.Id, out var foundryPreview))
                {
                    var fallback = new Smb3LevelRenderer().RenderMetatilePreview(
                        rom, document, foundryPreview.Blocks, foundryPreview.Width, foundryPreview.Height, paletteOverrides);
                    if (fallback.IsSuccess)
                        return ToThumbnail(fallback.Value!.PixelWidth, fallback.Value.PixelHeight, fallback.Value.ArgbPixels);
                }
                return null;
            }
            if (!snapshot.ElementBounds.TryGetValue(0, out var bounds)) return null;
            var left = Math.Max(0, bounds.Left * 16);
            var top = Math.Max(0, bounds.Top * 16);
            var width = Math.Min(snapshot.PixelWidth - left, Math.Max(1, bounds.Width * 16));
            var height = Math.Min(snapshot.PixelHeight - top, Math.Max(1, bounds.Height * 16));
            if (width <= 0 || height <= 0) return null;
            var pixels = new uint[width * height];
            for (var y = 0; y < height; y++)
                snapshot.ArgbPixels.Skip((top + y) * snapshot.PixelWidth + left).Take(width).ToArray().CopyTo(pixels, y * width);
            return ToThumbnail(width, height, pixels);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static LevelElement CreatePreviewElement(CatalogEntry entry, LevelDocument document, RomImage rom)
    {
        if (!entry.IsVariable)
            return new LevelElement(0, LevelElementKind.FixedGenerator, entry.Id, 1, 1,
                (byte)(entry.Id & 0x0F), null, (byte)((entry.Id & 0x70) << 1), 1, 1, 1);
        var encoded = entry.Id + 1;
        var firstHigh = (encoded / 15) << 5;
        var first = document.Header.IsVertical ? (byte)((firstHigh & 0xF0) | 1) : (byte)((firstHigh & 0xE0) | 1);
        var shape = (byte)(((encoded % 15) << 4) | GeneratorDefaults.Parameter(document.Tileset, entry.Id));
        var fourByte = rom.Profile.Levels.TryGetValue(document.AreaId, out var location)
            ? location.FourByteGeneratorIds
            : new HashSet<int>();
        return new LevelElement(0, LevelElementKind.VariableGenerator, entry.Id, 1, 1, shape,
            GeneratorDefaults.ExtraParameter(document.Tileset, fourByte, entry.Id), first, 1, 1, 1);
    }

    private static IReadOnlyList<LevelElement> CreatePreviewElements(CatalogEntry entry, LevelDocument document, RomImage rom)
    {
        var target = CreatePreviewElement(entry, document, rom);
        if (document.Tileset != 1 ||
            !(entry.IsVariable && entry.Id is >= 0 and <= 3 || !entry.IsVariable && entry.Id == 0x06))
        {
            return [target];
        }

        // Big blocks and vines seek an already-generated ground tile. This is
        // preview-only support, matching the stock generator's dependency;
        // it is never added to the user's level.
        const int groundY = 4;
        var groundFirst = document.Header.IsVertical ? (byte)(groundY % 15) : (byte)groundY;
        var groundSecond = document.Header.IsVertical ? (byte)((groundY / 15 << 4) | 1) : (byte)1;
        var ground = new LevelElement(1, LevelElementKind.VariableGenerator, 11, 1, groundY,
            0xC0, 3, groundFirst, groundSecond, 1, groundY);
        return [ground, target];
    }

    private static CatalogPreviewData ToThumbnail(int width, int height, IReadOnlyList<uint> pixels)
    {
        const int maximumSide = 48;
        if (width <= maximumSide && height <= maximumSide) return new CatalogPreviewData(width, height, pixels.ToArray());
        var scale = Math.Min((double)maximumSide / width, (double)maximumSide / height);
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        var thumbnail = new uint[targetWidth * targetHeight];
        for (var y = 0; y < targetHeight; y++)
        for (var x = 0; x < targetWidth; x++)
            thumbnail[(y * targetWidth) + x] = pixels[(Math.Min(height - 1, (int)(y / scale)) * width) + Math.Min(width - 1, (int)(x / scale))];
        return new CatalogPreviewData(targetWidth, targetHeight, thumbnail);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record CatalogEntry(
        bool IsEnemy,
        bool IsVariable,
        int Id,
        string ShortName,
        string Display,
        CatalogPreviewData? Preview = null,
        string? FamilyId = null,
        string? FamilySortId = null,
        string? NeighborhoodSortId = null,
        string? FamilyName = null,
        bool SuppressPreview = false,
        IReadOnlyList<CatalogEntry>? Variants = null)
    {
        public bool HasNoPreview => Preview is null;
        public bool HasVariants => Variants is { Count: > 1 };
        // Text is more useful than a family label when a family deliberately
        // has no preview, such as the Z-event control entries.
        public string CardTitle => HasNoPreview ? ShortName : FamilyName ?? ShortName;
        public string CatalogDisplay => HasVariants ? $"{FamilyName} — {ShortName}" : Display;
        public string CatalogToolTip => HasVariants
            ? $"{CatalogDisplay}\nRight-click to choose from {Variants!.Count} variants"
            : Display;
        public string SearchText => HasVariants
            ? string.Join(' ', Variants!.Select(item => $"{item.Display} {item.Id} ${item.Id:X2}"))
            : Display;
        public (bool IsEnemy, bool IsVariable, int Id) Identity => (IsEnemy, IsVariable, Id);

        public CatalogEntry AsVariant() => Variants is null ? this : this with { Variants = null };
        public bool SameIdentity(CatalogEntry other) => Identity == other.Identity;
        public string PreviewGlyph => IsEnemy ? "●" : "■";
        public override string ToString() => Display;
    }

    private sealed record NamedValue(int Value, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record PaletteChoice(int Value, string Name, IReadOnlyList<Avalonia.Media.IBrush> Colors);
    private sealed record PaletteSwatch(int Index, byte Value, Avalonia.Media.IBrush Brush);

    private void PaletteSwatch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index } || index < 0 || index >= _editingPaletteColors.Length) return;
        _editingPaletteColors[index] = (byte)((_editingPaletteColors[index] + 1) & 0x3F);
        RefreshPaletteSwatches();
    }

    private void RefreshPaletteSwatches()
    {
        PaletteSwatches.Children.Clear();
        for (var index = 0; index < _editingPaletteColors.Length; index++)
        {
            PaletteSwatches.Children.Add(new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Avalonia.Thickness(1),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(Smb3PaletteColor(_editingPaletteColors[index]))),
                Tag = index
            });
            var swatch = (Button)PaletteSwatches.Children[^1];
            ToolTip.SetTip(swatch, $"Color ${_editingPaletteColors[index]:X2}");
            swatch.Click += PaletteSwatch_Click;
        }
    }

    private static uint Smb3PaletteColor(byte value)
    {
        var colors = new uint[] { 0xFF666666, 0xFF002A88, 0xFF1412A7, 0xFF3B00A4, 0xFF5C007E, 0xFF6E0040, 0xFF6C0700, 0xFF561D00,
            0xFF3C2400, 0xFF0B3C00, 0xFF004B00, 0xFF00412B, 0xFF003E5C, 0xFF000000, 0xFF000000, 0xFF000000,
            0xFFADADAD, 0xFF155FD9, 0xFF4240FF, 0xFF7527FE, 0xFFA01ACC, 0xFFB71E7B, 0xFFB53120, 0xFF994E00,
            0xFF6B4700, 0xFF1E5E00, 0xFF008000, 0xFF00785A, 0xFF007399, 0xFF000000, 0xFF000000, 0xFF000000,
            0xFFFFFEFF, 0xFF64B0FF, 0xFF9390FF, 0xFFB36BFF, 0xFFF05CFF, 0xFFFF5AA8, 0xFFFF7A59, 0xFFFFA139,
            0xFFFFC739, 0xFF9BEF45, 0xFF4FEF6F, 0xFF4FEFD0, 0xFF3FD9FF, 0xFF000000, 0xFF000000, 0xFF000000,
            0xFFFFFEFF, 0xFFB8E8FF, 0xFFD0D0FF, 0xFFE0BFFF, 0xFFFFBFF3, 0xFFFFBFDB, 0xFFFFC7B3, 0xFFFFD59B,
            0xFFFFE79B, 0xFFD7F7A5, 0xFFBFFFBF, 0xFFBFFFE8, 0xFFBFEFFF, 0xFF000000, 0xFF000000, 0xFF000000 };
        return colors[value & 0x3F];
    }

    private PaletteChoice BuildPaletteChoice(int index, bool objects)
    {
        var slot = GetPaletteSlot(objects, index);
        if (slot is null) return new(index, index.ToString(), []);
        var colors = PalettePreview.FromNesColors(slot.Colors);
        var tileset = TilesetDisplayName(_document?.Tileset ?? 0);
        var label = string.IsNullOrWhiteSpace(slot.Name)
            ? $"{tileset} · {index + 1}"
            : $"{tileset} · {index + 1} - {slot.Name}";
        return new PaletteChoice(index, label, colors);
    }

    private static string TilesetDisplayName(int tileset) => tileset switch
    {
        1 => "Plains",
        2 => "Fortress",
        3 or 14 => "Hills",
        4 or 12 => "High-Up",
        5 or 11 or 13 => "Plant",
        6 or 7 or 8 => "Pipe Maze",
        9 => "Desert",
        10 => "Airship",
        _ => $"Tileset {tileset}"
    };

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

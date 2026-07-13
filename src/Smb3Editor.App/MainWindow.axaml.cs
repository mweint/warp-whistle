using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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

public sealed partial class MainWindow : Window
{
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
    private readonly List<Diagnostic> _diagnostics = [];
    private IReadOnlyList<Diagnostic> _activeRenderDiagnostics = [];
    private EditorActionFeedback? _activePersistentFeedback;
    private readonly List<CatalogEntry> _catalog = [];
    private readonly ObservableCollection<CatalogEntry> _recentCatalog = [];
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
    private byte[] _editingPaletteColors = new byte[16];
    private PaletteEditorWindow? _paletteEditor;
    private string _playMode = "rom";
    private int _zoomSequence;
    private string? _activeAreaId;
    private bool _isProjectDirty;
    private bool _suppressLevelSelection;
    private bool _closingApproved;
    private bool _refreshingPatches;

    private static string LegacyPreviewCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpWhistle", "preview-cache");

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? startupRomPath)
    {
        InitializeComponent();
        TracePlayRomButton.IsVisible = TraceToolsEnabled;
        TracePlayRomMenuItem.IsVisible = TraceToolsEnabled;
        OpenTraceLogsMenuItem.IsVisible = TraceToolsEnabled;
        WorkspacePaths.Configure(AppContext.BaseDirectory);
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
        EditorCanvas.CatalogPlacementRequested += PlaceCatalogAt;
        EditorCanvas.SelectionDescriptionChanged += text => SelectionText.Text = text;
        EditorCanvas.CanvasItemSelected += ClearCatalogPasteSource;
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
        var configuredEmulator = !string.IsNullOrWhiteSpace(_appSettings.EmulatorPath) && File.Exists(_appSettings.EmulatorPath)
            ? _appSettings.EmulatorPath
            : FindExternalMesen();
        if (!string.IsNullOrWhiteSpace(configuredEmulator) && !string.Equals(configuredEmulator, _appSettings.EmulatorPath, StringComparison.OrdinalIgnoreCase))
        {
            _appSettings = _appSettings with { EmulatorPath = configuredEmulator };
            AddDiagnostics([Diagnostics.Info("EMULATOR_EXTERNAL_FOUND", "Using Mesen from externals/emulators.")]);
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
            WorkspacePaths.RomsDirectory,
            Path.Combine(AppContext.BaseDirectory, "externals", "roms")
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
            WorkspacePaths.EmulatorsDirectory,
            Path.Combine(AppContext.BaseDirectory, "externals", "emulators")
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
        RomStatusText.Text = "Preparing Play ROM...";
        if (!await EnsureProjectSavedForPlayAsync()) return;
        SaveGlobalEmulatorSettings();
        if (!await EnsureEmulatorConfiguredAsync())
        {
            ShowPlayFailure("Play ROM needs a configured emulator.");
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

        var arguments = (EmulatorArgumentsBox.Text ?? "{rom}")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var launched = _emulatorLauncher.Launch(new EmulatorConfiguration(emulatorPath, arguments), romPath);
        AddDiagnostics(launched.Diagnostics);
        if (!launched.IsSuccess) ShowPlayFailure("Could not start the emulator.");
        else RomStatusText.Text = "Launching Play ROM...";
    }

    private async void TracePlayRom_Click(object? sender, RoutedEventArgs e)
    {
        if (!TraceToolsEnabled) return;
        RomStatusText.Text = "Preparing Play ROM trace...";
        if (!await EnsureProjectSavedForPlayAsync()) return;
        SaveGlobalEmulatorSettings();
        if (!await EnsureEmulatorConfiguredAsync())
        {
            ShowPlayFailure("Trace Play ROM needs a configured emulator.");
            return;
        }

        var emulatorPath = EmulatorPathBox.Text!.Trim();
        if (!Path.GetFileNameWithoutExtension(emulatorPath).Contains("mesen", StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostics([Diagnostics.Error("TRACE_EMULATOR", "Trace Play ROM requires Mesen or Mesen 2 because it loads the bundled Lua trace.")]);
            ShowPlayFailure("Trace Play ROM requires Mesen.");
            return;
        }

        var artifact = CompileCurrentProject();
        if (!artifact.IsSuccess)
        {
            ShowPlayFailure("Trace Play ROM is blocked — open Diagnostics for the exact reason.");
            return;
        }

        try
        {
            var directory = WorkspacePaths.PlaytestDirectory;
            Directory.CreateDirectory(directory);
            var romPath = Path.Combine(directory, "trace-playtest.nes");
            var logPath = Path.Combine(directory, "retry-trace.log");
            var scriptPath = Path.Combine(directory, "trace-retry.lua");
            var metadata = $"TRACE_META UTC={DateTimeOffset.UtcNow:O} PROFILE={_rom?.Profile.Id ?? "unknown"} SHA1={_rom?.Sha1 ?? "unknown"}\n";
            AddDiagnostics(AtomicFile.Write(logPath, Encoding.UTF8.GetBytes(metadata), maintainBackup: false).Diagnostics);

            var bundledScript = Path.Combine(AppContext.BaseDirectory, "trace-retry.lua");
            if (!File.Exists(bundledScript))
                bundledScript = Path.Combine(AppContext.BaseDirectory, "tools", "retry-trace.lua");
            if (!File.Exists(bundledScript))
            {
                AddDiagnostics([Diagnostics.Error("TRACE_SCRIPT", "The bundled Mesen trace script is missing from this build.")]);
                ShowPlayFailure("The bundled trace script is missing.");
                return;
            }

            var script = File.ReadAllText(bundledScript).Replace(
                "@@TRACE_LOG_PATH@@", logPath.Replace('\\', '/'), StringComparison.Ordinal);
            var scriptWrite = AtomicFile.Write(scriptPath, Encoding.UTF8.GetBytes(script), maintainBackup: false);
            AddDiagnostics(scriptWrite.Diagnostics);
            if (!scriptWrite.IsSuccess)
            {
                ShowPlayFailure("Could not prepare the trace script.");
                return;
            }

            var romWrite = AtomicFile.Write(romPath, artifact.Value!.RomBytes, maintainBackup: false);
            AddDiagnostics(romWrite.Diagnostics);
            if (!romWrite.IsSuccess)
            {
                ShowPlayFailure("Could not write the trace Play ROM.");
                return;
            }

            var launched = _emulatorLauncher.Launch(
                new EmulatorConfiguration(emulatorPath, ["{rom}", scriptPath]), romPath);
            AddDiagnostics(launched.Diagnostics);
            if (!launched.IsSuccess) ShowPlayFailure("Could not start Mesen with the trace.");
            else RomStatusText.Text = "Tracing Play ROM — navigate and test normally";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddDiagnostics([Diagnostics.Error("TRACE_PREPARE", $"Could not prepare the trace files: {ex.Message}")]);
            ShowPlayFailure("Could not prepare the Play ROM trace.");
        }
    }

    private async void PlayCurrentLevel_Click(object? sender, RoutedEventArgs e)
    {
        RomStatusText.Text = "Preparing Play Level...";
        if (!await EnsureProjectSavedForPlayAsync()) return;
        SaveGlobalEmulatorSettings();
        if (!await EnsureEmulatorConfiguredAsync())
        {
            ShowPlayFailure("Play Level needs a configured emulator.");
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
            ShowPlayFailure("Play Level could not prepare its temporary test ROM.");
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

        RomStatusText.Text = $"Launching {selectedLevel.DisplayName} as Small Mario";
        var arguments = (EmulatorArgumentsBox.Text ?? "{rom}")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var launched = _emulatorLauncher.Launch(new EmulatorConfiguration(EmulatorPathBox.Text!.Trim(), arguments), romPath);
        AddDiagnostics(launched.Diagnostics);
        if (!launched.IsSuccess) ShowPlayFailure("Could not start the emulator.");
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

    private void CatalogFilterTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } || !int.TryParse(tag, out var selected)) return;
        _catalogFilter = selected;
        CatalogAllFilter.IsChecked = selected == 0;
        CatalogObjectsFilter.IsChecked = selected == 1;
        CatalogEnemiesFilter.IsChecked = selected == 2;
        CatalogRecentFilter.IsChecked = selected == 3;
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
        foreach (var existing in _recentCatalog.Where(item => item.IsEnemy == entry.IsEnemy && item.IsVariable == entry.IsVariable && item.Id == entry.Id).ToArray())
            _recentCatalog.Remove(existing);
        _recentCatalog.Insert(0, entry);
        while (_recentCatalog.Count > 24) _recentCatalog.RemoveAt(_recentCatalog.Count - 1);
    }

    private void CatalogViewChanged(object? sender, RoutedEventArgs e)
    {
        var grid = ReferenceEquals(sender, CatalogGridToggle) ? CatalogGridToggle.IsChecked != false : CatalogListToggle.IsChecked == false;
        CatalogGridToggle.IsChecked = grid;
        CatalogListToggle.IsChecked = !grid;
        CatalogGrid.IsVisible = grid;
        CatalogListView.IsVisible = !grid;
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

    private void SetScrollOffset(double x, double y) =>
        LevelScrollViewer.Offset = new Avalonia.Vector(
            Math.Clamp(x, 0, Math.Max(0, LevelScrollViewer.Extent.Width - LevelScrollViewer.Viewport.Width)),
            Math.Clamp(y, 0, Math.Max(0, LevelScrollViewer.Extent.Height - LevelScrollViewer.Viewport.Height)));

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
            var supported = _project is not null && string.Equals(_rom?.Profile.Id, "us-prg1", StringComparison.Ordinal);
            PatchControls.IsEnabled = supported;

            var settings = _project?.Patches ?? PatchSettings.None;
            var retryIncluded = settings.QuickRetry is not null;
            var quitIncluded = settings.StartSelectReturnToMap is not null;
            QuickRetryCard.IsVisible = retryIncluded;
            QuitToMapCard.IsVisible = quitIncluded;
            NoPatchesText.IsVisible = !retryIncluded && !quitIncluded;

            var choices = new[]
            {
                new PatchOverrideOption("Inherit", null),
                new PatchOverrideOption("Enabled", true),
                new PatchOverrideOption("Disabled", false)
            };
            QuickRetryOverride.ItemsSource = choices;
            QuitToMapOverride.ItemsSource = choices;
            var areaId = _document?.AreaId;
            var retry = settings.QuickRetry ?? new();
            var quit = settings.StartSelectReturnToMap ?? new();
            PatchLevelText.Text = areaId is null ? "Select a level to set an override." : "Level override";
            bool? retryOverride = areaId is not null && retry.LevelOverrides is not null && retry.LevelOverrides.TryGetValue(areaId, out var retryValue) ? retryValue : null;
            bool? quitOverride = areaId is not null && quit.LevelOverrides is not null && quit.LevelOverrides.TryGetValue(areaId, out var quitValue) ? quitValue : null;
            QuickRetryOverride.SelectedItem = choices.First(item => item.Value == retryOverride);
            QuitToMapOverride.SelectedItem = choices.First(item => item.Value == quitOverride);
            QuickRetryOverride.IsEnabled = supported && areaId is not null;
            QuitToMapOverride.IsEnabled = supported && areaId is not null;
        }
        finally
        {
            _refreshingPatches = false;
        }
    }

    private async void OpenPatchManager_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        var supported = string.Equals(_rom?.Profile.Id, "us-prg1", StringComparison.Ordinal);
        if (!supported)
        {
            AddDiagnostics([Diagnostics.Warning("PATCH_PROFILE", "Patches currently require Super Mario Bros. 3 (USA, PRG1 / Rev A).")]);
            return;
        }

        var manager = new PatchManagerWindow(_project.Patches ?? PatchSettings.None, settings =>
        {
            _project = _project with { Patches = settings };
            MarkPatchesChanged();
        });
        await manager.ShowDialog(this);
    }

    private void PatchOverrideChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPatches || _project is null || _document is null || sender is not ComboBox box || box.SelectedItem is not PatchOverrideOption option) return;
        var settings = _project.Patches ?? PatchSettings.None;
        var retry = settings.QuickRetry ?? new();
        var quit = settings.StartSelectReturnToMap ?? new();
        if (ReferenceEquals(box, QuickRetryOverride)) retry = WithLevelOverride(retry, _document.AreaId, option.Value);
        if (ReferenceEquals(box, QuitToMapOverride)) quit = WithLevelOverride(quit, _document.AreaId, option.Value);
        _project = _project with { Patches = settings with { QuickRetry = retry, StartSelectReturnToMap = quit } };
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
                         $"Enemies: {enemies.Value?.Length ?? 0} / {_document.OriginalEnemyLength} bytes\n" +
                         $"Used by: {string.Join(", ", _document.UsedBy)}";
    }

    private void UpdateByteBudget(int layoutUsed, int layoutCapacity, int enemyUsed, int enemyCapacity)
    {
        var layoutLeft = layoutCapacity - layoutUsed;
        var enemyLeft = enemyCapacity - enemyUsed;
        ByteBudgetText.Text = $"Level {layoutUsed}/{layoutCapacity} · Enemies {enemyUsed}/{enemyCapacity}";
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
        var source = type == 3 ? _recentCatalog.AsEnumerable() : _catalog;
        var filtered = source
            .Where(item => (type is 0 or 3 || (type == 1 && !item.IsEnemy) || (type == 2 && item.IsEnemy)) &&
                           (string.IsNullOrWhiteSpace(query) || item.Display.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            item.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            $"${item.Id:X2}".Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        _refreshingCatalog = true;
        CatalogGrid.ItemsSource = filtered;
        CatalogListView.ItemsSource = filtered;
        if (_activeCatalogEntry is null || !filtered.Contains(_activeCatalogEntry)) _activeCatalogEntry = filtered.FirstOrDefault();
        CatalogGrid.SelectedItem = _activeCatalogEntry;
        CatalogListView.SelectedItem = _activeCatalogEntry;
        _refreshingCatalog = false;
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
        var items = ObjectCatalogNames.ForTileset(tileset)
            .Select(item => new CatalogEntry(false, false, item.Id, item.Name, $"{item.Name} (${item.Id:X2}, {item.Id})"))
            .ToList();
        items.AddRange(ObjectCatalogNames.VariableForTileset(tileset)
            .Select(item => new CatalogEntry(false, true, item.Id, item.Name, $"{item.Name} (${item.Id:X2}, {item.Id})")));
        items.AddRange(Smb3LevelRenderer.EnemyCatalog
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(item => new CatalogEntry(true, false, item.Key, item.Value,
                $"{item.Value} (${item.Key:X2}, {item.Key})")));
        return items;
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
            if (entry.IsEnemy && snapshot.EnemySprites.TryGetValue((byte)entry.Id, out var sprite))
                return ToThumbnail(sprite.PixelWidth, sprite.PixelHeight, sprite.ArgbPixels);
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

    private sealed record CatalogEntry(bool IsEnemy, bool IsVariable, int Id, string ShortName, string Display, CatalogPreviewData? Preview = null)
    {
        public bool HasNoPreview => Preview is null;
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
        var state = slot.IsModified ? "Modified" : "Stock";
        var label = string.IsNullOrWhiteSpace(slot.Name) ? $"{index + 1} - {state}" : $"{index + 1} - {slot.Name} ({state})";
        return new PaletteChoice(index, label, colors);
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

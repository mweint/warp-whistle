using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Smb3Editor.Core;

namespace Smb3Editor.App;

public sealed partial class SetupWizardWindow : Window
{
    private readonly AppSettingsV1 _settings;
    private string? _emulatorPath;

    public SetupWizardWindow() : this(new AppSettingsV1())
    {
    }

    public SetupWizardWindow(AppSettingsV1 settings)
    {
        InitializeComponent();
        _settings = settings;
        RomPathBox.Text = settings.LastRomPath;
        RomUrlBox.Text = settings.RomUrl;
        UrlRomOption.IsChecked = !string.IsNullOrWhiteSpace(settings.RomUrl);
        LocalRomOption.IsChecked = UrlRomOption.IsChecked != true;
        UpdateRomSourceUi();
        _emulatorPath = settings.EmulatorPath;
        UpdateEmulatorStatus();
    }

    public AppSettingsV1? Result { get; private set; }

    private void RomSourceChanged(object? sender, RoutedEventArgs e) => UpdateRomSourceUi();

    private void UpdateRomSourceUi()
    {
        var useUrl = UrlRomOption.IsChecked == true;
        LocalRomPanel.IsVisible = !useUrl;
        UrlRomPanel.IsVisible = useUrl;
        StatusText.Text = string.Empty;
    }

    private async void ChooseRom_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a legally obtained SMB3 ROM",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("NES ROM") { Patterns = ["*.nes", "*.rom"] }]
        });
        if (files.Count > 0) RomPathBox.Text = files[0].Path.LocalPath;
    }

    private async void ChooseEmulator_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose emulator executable",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Executable") { Patterns = ["*.exe"] }]
        });
        if (files.Count == 0) return;
        _emulatorPath = files[0].Path.LocalPath;
        UpdateEmulatorStatus();
    }

    private async void DownloadMesen_Click(object? sender, RoutedEventArgs e)
    {
        await SetBusyAsync(async () =>
        {
            StatusText.Text = "Downloading and unpacking MesenCE...";
            var installed = await SetupDownloads.DownloadMesenAsync();
            if (!installed.IsSuccess) { StatusText.Text = installed.Diagnostics.First().Message; return; }
            _emulatorPath = installed.Value;
            UpdateEmulatorStatus();
            StatusText.Text = string.Empty;
        });
    }

    private async void Finish_Click(object? sender, RoutedEventArgs e)
    {
        var useUrl = UrlRomOption.IsChecked == true;
        var source = useUrl ? RomUrlBox.Text?.Trim() : RomPathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            StatusText.Text = useUrl ? "Enter your authorized HTTPS URL." : "Choose your local ROM file.";
            return;
        }

        await SetBusyAsync(async () =>
        {
            StatusText.Text = "Checking and importing ROM...";
            var imported = await SetupDownloads.ImportRomAsync(source);
            if (!imported.IsSuccess) { StatusText.Text = imported.Diagnostics.First().Message; return; }
            Result = _settings with
            {
                LastRomPath = imported.Value,
                RomUrl = useUrl ? source : null,
                EmulatorPath = _emulatorPath,
                EmulatorArguments = ["{rom}"],
                SetupCompleted = true
            };
            Close();
        });
    }

    private void Skip_Click(object? sender, RoutedEventArgs e)
    {
        Result = _settings with { SetupCompleted = true, EmulatorPath = _emulatorPath, EmulatorArguments = ["{rom}"] };
        Close();
    }

    private void UpdateEmulatorStatus()
    {
        EmulatorStatusText.Text = !string.IsNullOrWhiteSpace(_emulatorPath) && File.Exists(_emulatorPath)
            ? $"Current emulator: {Path.GetFileNameWithoutExtension(_emulatorPath)}"
            : "No emulator selected. You can add one later.";
    }

    private async Task SetBusyAsync(Func<Task> action)
    {
        IsEnabled = false;
        try { await action(); }
        finally { IsEnabled = true; }
    }
}

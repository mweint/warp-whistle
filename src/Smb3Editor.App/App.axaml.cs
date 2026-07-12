using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Smb3Editor.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startupRom = desktop.Args?.FirstOrDefault(static argument =>
                argument.EndsWith(".nes", StringComparison.OrdinalIgnoreCase) ||
                argument.EndsWith(".rom", StringComparison.OrdinalIgnoreCase));
            desktop.MainWindow = new MainWindow(startupRom);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

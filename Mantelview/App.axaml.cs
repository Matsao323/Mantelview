using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Mantelview;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var isScreensaverMode = desktop.Args?.Contains("--slideshow", StringComparer.OrdinalIgnoreCase) == true;
            desktop.MainWindow = new MainWindow(isScreensaverMode);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

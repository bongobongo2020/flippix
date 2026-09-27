using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FlipPix.Mobile.ViewModels;
using FlipPix.Mobile.Views;

namespace FlipPix.Mobile;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var vm = new MainViewModel();
        // Back from the background: catch up with the computer now rather than after a back-off.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, _) => FlipPix.Mobile.Services.AppServices.Jobs.Nudge();
        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single: // Android, iOS
                single.MainView = new AppShell { DataContext = vm };
                break;
            case IClassicDesktopStyleApplicationLifetime desktop: // a phone- or iPad-sized window for iteration
                var ipad = Environment.GetEnvironmentVariable("FLIPPIX_MOBILE_IPAD") == "1";
                desktop.MainWindow = new Window
                {
                    Title = "FlipPix Mobile", Width = ipad ? 1180 : 412, Height = ipad ? 820 : 900,
                    Content = new AppShell { DataContext = vm },
                };
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }
}

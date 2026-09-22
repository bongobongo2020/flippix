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
        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single: // Android
                single.MainView = new MainView { DataContext = vm };
                break;
            case IClassicDesktopStyleApplicationLifetime desktop: // phone-sized window for iteration
                desktop.MainWindow = new Window
                {
                    Title = "FlipPix Mobile", Width = 412, Height = 900,
                    Content = new MainView { DataContext = vm },
                };
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }
}

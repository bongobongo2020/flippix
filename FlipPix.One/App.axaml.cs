using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FlipPix.One.ViewModels;
using FlipPix.One.Views;

namespace FlipPix.One;

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
                    Title = "FlipPix-One", Width = 412, Height = 900,
                    Content = new MainView { DataContext = vm },
                };
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }
}

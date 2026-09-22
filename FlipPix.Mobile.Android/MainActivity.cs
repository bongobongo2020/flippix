using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using FlipPix.Mobile.Controls;

namespace FlipPix.Mobile.Android;

[Activity(
    Label = "FlipPix",
    Theme = "@style/FlipPixTheme",
    Icon = "@drawable/icon",
    MainLauncher = true,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        VideoSurface.Factory = new AndroidVideoSurface(this);
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}

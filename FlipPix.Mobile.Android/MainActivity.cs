using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using FlipPix.Mobile.Controls;
using FlipPix.Mobile.Services;

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
        DeviceInfo.Name = PhoneName(); // DeviceInfo, never AppServices: see DeviceInfo
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }

    /// <summary>"Google Pixel 8": what the computer lists this phone as once it is paired.</summary>
    private static string PhoneName()
    {
        var maker = global::Android.OS.Build.Manufacturer ?? "";
        var model = global::Android.OS.Build.Model ?? "Android phone";
        if (maker.Length > 0) maker = char.ToUpperInvariant(maker[0]) + maker[1..];
        return model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker.Length == 0 ? model : $"{maker} {model}";
    }
}

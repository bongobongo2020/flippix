using Avalonia;
using Avalonia.iOS;
using FlipPix.Mobile.Controls;
using FlipPix.Mobile.Services;
using Foundation;
using UIKit;

namespace FlipPix.Mobile.iOS;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        VideoSurface.Factory = new IosVideoSurface();
        // DeviceInfo, never AppServices: see DeviceInfo.
        var ipad = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad;
        DeviceInfo.Noun = ipad ? "iPad" : "iPhone";
        DeviceInfo.Name = DeviceName(ipad);
        // Documents, which Info.plist shows in Files as On My iPad › FlipPix: on this device only, not iCloud.
        DeviceInfo.SaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }

    /// <summary>
    /// What the computer lists this device as. Since iOS 16 the user-assigned name ("Zee's iPad") needs
    /// an entitlement, and without it UIDevice.Name is just the model, so the model is used either way.
    /// </summary>
    private static string DeviceName(bool ipad)
    {
        var name = UIDevice.CurrentDevice.Name;
        return string.IsNullOrWhiteSpace(name) ? (ipad ? "iPad" : "iPhone") : name;
    }
}

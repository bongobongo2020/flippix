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
        MobileSettings.Folder = SettingsFolder(DeviceInfo.SaveFolder);
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }

    /// <summary>
    /// Library/Application Support/FlipPixMobile, which Files never shows. Earlier builds kept the
    /// pairing in Documents/FlipPixMobile; it moves here once, so the iPad stays paired.
    /// </summary>
    private static string SettingsFolder(string documents)
    {
        var support = NSFileManager.DefaultManager
            .GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path!;
        var folder = Path.Combine(support, "FlipPixMobile");
        var old = Path.Combine(documents, "FlipPixMobile");
        try
        {
            if (Directory.Exists(old) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(support);
                Directory.Move(old, folder);
            }
        }
        catch { /* left where it was: the iPad pairs again */ }
        return folder;
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

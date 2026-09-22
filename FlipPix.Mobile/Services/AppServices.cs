namespace FlipPix.Mobile.Services;

/// <summary>The app's two long-lived objects. A phone app has one window, so one instance.</summary>
public static class AppServices
{
    public static MobileSettings Settings { get; } = MobileSettings.Load();
    public static ComfyGateway Comfy { get; } = new();

    static AppServices() => Comfy.Configure(Settings.ComfyUrl);
}

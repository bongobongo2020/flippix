namespace FlipPix.Mobile.Services;

/// <summary>The app's long-lived objects. A phone app has one window, so one instance of each.</summary>
public static class AppServices
{
    public static MobileSettings Settings { get; } = MobileSettings.Load();
    public static RemoteClient Remote { get; } = new();
    public static JobsHub Jobs { get; } = new(Remote);

    static AppServices()
    {
        if (Settings.IsPaired) Remote.Configure(Settings.ServerUrl, Settings.Token, Settings.ServerName);
    }
}

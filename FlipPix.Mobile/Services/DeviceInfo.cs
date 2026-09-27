namespace FlipPix.Mobile.Services;

/// <summary>
/// What the computer shows in its list of paired phones. The Android head sets it while building the
/// app, before Avalonia has started, so this class must stay free of anything that touches the UI:
/// setting AppServices.DeviceName there once ran AppServices' static constructor, whose DispatcherTimer
/// bound Avalonia's dispatcher before the Android one existed, and no posted job ever ran after that.
/// </summary>
public static class DeviceInfo
{
    public static string Name { get; set; } = Environment.MachineName;

    /// <summary>What this device is called in running text: "Disconnect this iPad".</summary>
    public static string Noun { get; set; } = "phone";
}

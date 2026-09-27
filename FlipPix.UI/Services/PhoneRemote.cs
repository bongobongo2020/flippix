extern alias remote;

using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using RemoteHost = remote::FlipPix.Remote.Host.RemoteHost;

namespace FlipPix.UI.Services;

/// <summary>
/// The desktop's one phone remote: the LAN server FlipPix Mobile pairs with. Started at launch
/// (it listens only if it was left on), shown in <see cref="PhoneRemoteWindow"/>, stopped at exit.
/// This is the only file that names FlipPix.Remote's types (see the aliased reference in the csproj).
/// </summary>
public static class PhoneRemote
{
    private static RemoteHost? _host;

    /// <summary>The window's DataContext. Null until <see cref="Start"/> ran.</summary>
    public static object? Host => _host;

    public static void Start(SettingsService settings, IAppLogger logger)
    {
        if (_host != null) return;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        _host = new RemoteHost(() => settings.Settings, logger, action => dispatcher.BeginInvoke(action));
        // Reads its config and saved jobs off the UI thread; never delays the first window.
        _ = _host.InitializeAsync().ContinueWith(
            t => logger.LogError(t.Exception!, "Phone remote failed to start"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public static void Stop()
    {
        var host = _host;
        _host = null;
        if (host == null) return;
        // On the thread pool: blocking the UI thread on a task that resumes on it would deadlock.
        try { Task.Run(() => host.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(4)); }
        catch (Exception) { /* exiting anyway */ }
    }
}

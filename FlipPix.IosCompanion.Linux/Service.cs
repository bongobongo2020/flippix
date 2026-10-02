using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FlipPix.Core.Services;
using FlipPix.Remote.Engine;
using FlipPix.Remote.Host;

namespace FlipPix.IosCompanion;

/// <summary>
/// The companion without a window: what CompanionForm does on Windows, with status.json in place of
/// the window and a request file in place of the "New code" button. Runs until SIGTERM (systemctl stop).
/// </summary>
internal static class Service
{
    private static readonly string[] Credits = { "Pictures by Krea 2", "Video by MiniMax H3", "Writing by Qwen2.5-VL" };

    public static async Task<int> RunAsync()
    {
        Directory.CreateDirectory(CompanionConfig.LocalDir);
        // Before SettingsService, which resolves ~/.config without creating it.
        Directory.CreateDirectory(CompanionConfig.AppDataDir);

        // One companion per PC: a second copy would fight the first for the phone's port.
        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(Path.Combine(CompanionConfig.LocalDir, "companion.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("FlipPix iOS Companion is already running. See: flippix-companion status");
            return 1;
        }

        using var _ = instanceLock;
        using var stop = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
        using var ui = new SerialDispatcher();

        var log = new CompanionLogger();
        var settings = new SettingsService();
        settings.SetLogger(log);
        var config = CompanionConfig.Load(settings.Settings);
        log.LogInfo("Companion starting (ComfyUI at {0})", config.PortableRoot);

        var status = new CompanionStatus { Host = Environment.MachineName, Pid = Environment.ProcessId, LogFolder = log.FolderPath };
        var lastCode = "";

        // No filter, no phone link: the licenses of both models require the screening.
        NsfwFilter? filter = null;
        if (File.Exists(config.FilterModel))
        {
            try { filter = new NsfwFilter(config.FilterModel); }
            catch (Exception ex) { log.LogError(ex, "The content filter didn't load"); }
        }
        else
        {
            log.LogWarning("Content filter model not found at {0}", config.FilterModel);
        }

        var host = new RemoteHost(() => settings.Settings, log, ui.Post,
            configPath: Path.Combine(CompanionConfig.AppDataDir, "companion-remote.json"),
            dataDir: Path.Combine(CompanionConfig.LocalDir, "remote"),
            options: new RemoteOptions
            {
                Looks = new HashSet<string> { "photo" },
                Filter = filter,
                LibraryMadeOnly = true,
                Credits = Credits,
            });
        var supervisor = new Supervisor(config, () => settings.Settings, log);

        void Refresh()
        {
            var running = host.IsRunning;
            var code = running ? host.PairingCode : "";
            if (code != lastCode)
            {
                lastCode = code;
                status.CodeIssuedUtc = DateTime.UtcNow;
                if (code.Length > 0) Console.WriteLine($"Pairing code: {code}");
            }
            status.PairingCode = code;
            status.Address = running ? host.AddressText : "";
            status.PairedDevices = host.Devices.Count;
            status.Activity = host.ActivityText ?? "";
            status.Phone = filter == null
                ? new() { Ok = false, Text = "Off until the content filter is installed" }
                : new() { Ok = running ? true : host.StatusText.StartsWith("Couldn't") ? false : null, Text = running ? "On" : host.StatusText };
            status.Comfy = new() { Ok = supervisor.ComfyReady ? true : Pending(supervisor.ComfyStatus) ? null : false, Text = supervisor.ComfyStatus };
            status.Llm = new() { Ok = supervisor.LlmReady ? true : Pending(supervisor.LlmStatus) ? null : false, Text = supervisor.LlmStatus };
            status.Filter = new() { Ok = filter != null, Text = filter != null ? "On: every picture and video is checked" : "Missing. Run the installer again." };
            status.UpdatedUtc = DateTime.UtcNow;
            try { status.Save(); }
            catch (Exception ex) { log.LogWarning("Couldn't write {0}: {1}", CompanionStatus.FilePath, ex.Message); }
        }

        try
        {
            host.PropertyChanged += (_, _) => ui.Post(Refresh);
            host.Devices.CollectionChanged += (_, _) => ui.Post(Refresh);
            await host.InitializeAsync();
            if (filter != null) ui.Post(() => host.IsEnabled = true);

            supervisor.Changed += () => ui.Post(Refresh);
            supervisor.Start();

            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(CompanionStatus.NewCodeRequestPath))
                {
                    try { File.Delete(CompanionStatus.NewCodeRequestPath); } catch (IOException) { }
                    ui.Post(() => host.NewCodeCommand.Execute(null));
                }
                ui.Post(Refresh);
                try { await Task.Delay(TimeSpan.FromSeconds(1), stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The companion failed");
            Console.Error.WriteLine("The companion failed: " + ex.Message);
            return 1;
        }
        finally
        {
            log.LogInfo("Companion stopping");
            supervisor.Dispose();
            try { await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (Exception) { /* exiting anyway */ }
            filter?.Dispose();
            try { File.Delete(CompanionStatus.FilePath); } catch (IOException) { }
        }
        return 0;
    }

    private static bool Pending(string status) => status.StartsWith("Starting") || status.StartsWith("Checking");

    /// <summary>
    /// Stands in for the UI thread: RemoteHost changes its properties only through the dispatcher it's
    /// given, so running every such action on one thread keeps them in order and never concurrent.
    /// </summary>
    private sealed class SerialDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public SerialDispatcher()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    try { action(); }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                }
            }) { IsBackground = true, Name = "companion-dispatch" };
            _thread.Start();
        }

        public void Post(Action action)
        {
            try { _queue.Add(action); }
            catch (InvalidOperationException) { /* shutting down */ }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
        }
    }
}

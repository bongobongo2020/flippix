using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.Remote.Contracts;
using FlipPix.Remote.Engine;
using FlipPix.Remote.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlipPix.Remote.Host;

/// <summary>
/// The phone remote as the desktop sees it: on or off, the pairing code to type on the phone, the
/// addresses it listens on, and the phones allowed in. Both desktop builds bind their "Phone remote"
/// window to this. Properties change on the UI thread only (through the dispatcher the desktop hands
/// in), because Avalonia throws on a binding update from any other thread.
/// </summary>
public sealed partial class RemoteHost : ObservableObject, IAsyncDisposable
{
    private const int MaxWrongCodes = 5;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    private readonly Action<Action> _dispatch;
    private readonly string _configPath;
    private readonly object _configLock = new();
    private readonly object _saveLock = new();
    private readonly SemaphoreSlim _switch = new(1, 1);
    private RemoteConfig _config = new();
    private WebApplication? _web;
    private DiscoveryResponder? _discovery;
    private string _code = "";
    private DateTime _codeIssued;
    private int _wrongCodes;
    private bool _loaded;
    private bool _applying;

    /// <param name="settings">The desktop's live settings; read at the moment each job needs them.</param>
    /// <param name="dispatch">Runs an action on the desktop's UI thread.</param>
    /// <param name="options">What this host exposes and screens; null for everything, unscreened (the desktop).</param>
    public RemoteHost(Func<ComfyUISettings> settings, IAppLogger? logger, Action<Action> dispatch,
        string? configPath = null, string? dataDir = null, RemoteOptions? options = null)
    {
        _dispatch = dispatch;
        _configPath = configPath ?? RemoteConfig.DefaultPath;
        Engine = new RemoteEngine(settings, logger, dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipPix", "remote"), options);
        Jobs = new JobManager(Engine);
        if (Engine.Options.LibraryMadeOnly)
            Engine.Library.Visible = entry => Jobs.MadeFor(entry.RelativePath) != null;
        Jobs.Changed += () => _dispatch(RefreshActivity);
        Name = Environment.MachineName;
    }

    public RemoteEngine Engine { get; }
    public JobManager Jobs { get; }
    public string Name { get; }
    public int Port => _config.Port;

    public ObservableCollection<PairedDevice> Devices { get; } = new();

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "Off";
    /// <summary>The code as it's read out: "482 913".</summary>
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private string _addressText = "";
    [ObservableProperty] private string _activityText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoDevices))]
    private bool _hasDevices;

    public bool NoDevices => !HasDevices;

    // ── Lifetime ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the config off the UI thread and starts listening if the remote was left on.</summary>
    /// <param name="alwaysOn">
    /// Listen whatever the config says (the iOS companion). Setting IsEnabled after this returns isn't
    /// the same: the load is applied through the dispatcher, so it can land later and switch it back off.
    /// </param>
    public async Task InitializeAsync(bool alwaysOn = false)
    {
        var config = await Task.Run(() => RemoteConfig.Load(_configPath));
        if (alwaysOn) config.Enabled = true;
        _ = Task.Run(Engine.Uploads.Prune);
        await Jobs.StartAsync();
        lock (_configLock) _config = config;
        _dispatch(() =>
        {
            Devices.Clear();
            foreach (var d in config.Devices) Devices.Add(d);
            HasDevices = Devices.Count > 0;
            _loaded = true;
            _applying = true;
            IsEnabled = config.Enabled;
            _applying = false;
            RefreshActivity();
        });
        if (config.Enabled) await StartAsync();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loaded || _applying) return;
        lock (_configLock) _config.Enabled = value;
        _ = SaveConfigAsync();
        _ = value ? StartAsync() : StopAsync();
    }

    // Both run on the thread pool: building the web host and listing network adapters take long
    // enough to freeze a window, and the switch is flipped from the UI thread.
    private Task StartAsync() => Task.Run(async () =>
    {
        await _switch.WaitAsync();
        try { await StartCoreAsync(); }
        finally { _switch.Release(); }
    });

    private Task StopAsync() => Task.Run(async () =>
    {
        await _switch.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _switch.Release(); }
    });

    private async Task StartCoreAsync()
    {
        await StopCoreAsync();
        var port = Port;
        WebApplication? web = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = typeof(RemoteHost).Assembly.GetName().Name,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.ListenAnyIP(port);
                k.Limits.MaxRequestBodySize = UploadStore.MaxBytes + 1024 * 1024;
                k.AddServerHeader = false;
            });
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
            web = builder.Build();
            RemoteEndpoints.Map(web, this);
            await web.StartAsync();
            _web = web;

            _discovery = new DiscoveryResponder(Hello, m => Engine.Logger.LogWarning(m));
            _discovery.Start();

            IssueCode();
            var addresses = LanAddresses();
            _dispatch(() =>
            {
                IsRunning = true;
                StatusText = "On. Your phone can connect over Wi-Fi.";
                AddressText = addresses.Count == 0
                    ? $"This computer, port {port}"
                    : string.Join("   ", addresses.Select(a => $"{a}:{port}"));
            });
            Engine.Logger.LogInfo($"Phone remote listening on port {port}");
        }
        catch (Exception ex)
        {
            var reason = ex is IOException { InnerException: SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } }
                         || ex.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                         || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                ? $"Port {port} is already used by another program."
                : ex.Message;
            Engine.Logger.LogWarning("Phone remote couldn't start: " + reason);
            if (web != null && _web == null)
            {
                try { await web.DisposeAsync(); } catch (Exception) { /* it never started */ }
            }
            _dispatch(() =>
            {
                IsRunning = false;
                StatusText = "Couldn't start: " + reason;
            });
        }
    }

    private async Task StopCoreAsync()
    {
        _discovery?.Dispose();
        _discovery = null;
        var web = _web;
        _web = null;
        if (web != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await web.StopAsync(cts.Token);
            }
            catch (Exception) { /* stopping is best effort */ }
            await web.DisposeAsync();
        }
        _dispatch(() =>
        {
            IsRunning = false;
            StatusText = "Off";
            PairingCode = "";
            AddressText = "";
        });
    }

    /// <summary>
    /// Stops listening and cancels the running job. Safe to block on from the UI thread: nothing in
    /// here needs that thread (property updates are posted, not awaited).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Jobs.Dispose();
        await StopAsync().ConfigureAwait(false);
        Engine.Dispose();
    }

    // ── Pairing ────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void NewCode() => IssueCode();

    private void IssueCode()
    {
        string code;
        lock (_configLock)
        {
            _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");
            _codeIssued = DateTime.UtcNow;
            _wrongCodes = 0;
            code = _code;
        }
        _dispatch(() => PairingCode = code[..3] + " " + code[3..]);
    }

    /// <summary>
    /// Trades the code on screen for a token. Five wrong guesses replace the code, so it can't be
    /// worked through; a used code is replaced too, so each one pairs a single phone.
    /// </summary>
    internal PairResponse? TryPair(string code, string deviceName)
    {
        code = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        string? token = null;
        PairedDevice? device = null;
        var reissue = false;
        lock (_configLock)
        {
            var fresh = DateTime.UtcNow - _codeIssued < CodeLifetime;
            if (_code.Length == 6 && fresh && CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(code), System.Text.Encoding.ASCII.GetBytes(_code)))
            {
                token = RemoteConfig.NewToken();
                var name = string.IsNullOrWhiteSpace(deviceName) ? "Phone" : deviceName.Trim();
                device = new PairedDevice { Name = name.Length > 60 ? name[..60] : name, TokenHash = RemoteConfig.Hash(token), LastSeen = DateTimeOffset.Now };
                _config.Devices.Add(device);
                reissue = true;
            }
            else if (++_wrongCodes >= MaxWrongCodes || !fresh)
            {
                reissue = true;
            }
        }
        if (reissue) IssueCode();
        if (device == null) return null;

        _ = SaveConfigAsync();
        _dispatch(() =>
        {
            Devices.Add(device);
            HasDevices = true;
        });
        return new PairResponse { Token = token!, Name = Name };
    }

    internal PairedDevice? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        PairedDevice? device;
        var save = false;
        lock (_configLock)
        {
            device = _config.Match(token);
            // Last seen is for the desktop window ("used today"); saved at most hourly per phone.
            if (device != null && (device.LastSeen == null || DateTimeOffset.Now - device.LastSeen > TimeSpan.FromHours(1)))
            {
                device.LastSeen = DateTimeOffset.Now;
                save = true;
            }
        }
        if (save) _ = SaveConfigAsync();
        return device;
    }

    [RelayCommand]
    private void RemoveDevice(PairedDevice? device)
    {
        if (device == null) return;
        lock (_configLock) _config.Devices.RemoveAll(d => d.Id == device.Id);
        Devices.Remove(device);
        HasDevices = Devices.Count > 0;
        _ = SaveConfigAsync();
    }

    private async Task SaveConfigAsync()
    {
        string snapshot;
        lock (_configLock) snapshot = System.Text.Json.JsonSerializer.Serialize(_config);
        try
        {
            await Task.Run(() =>
            {
                lock (_saveLock) // one writer at a time
                    System.Text.Json.JsonSerializer.Deserialize<RemoteConfig>(snapshot)!.Save(_configPath);
            });
        }
        catch (Exception ex)
        {
            Engine.Logger.LogWarning("Couldn't save the phone remote settings: " + ex.Message);
        }
    }

    // ── What the phone and the window read ─────────────────────────────────────────────────────

    internal HelloDto Hello() => new() { Name = Name, Port = Port, Version = "1" };

    private void RefreshActivity()
    {
        var (queued, running) = Jobs.Counts();
        ActivityText = (running, queued) switch
        {
            (0, 0) => "No phone jobs right now.",
            (_, 0) => "Making something for your phone.",
            (0, _) => $"{queued} phone job{(queued == 1 ? "" : "s")} waiting.",
            _ => $"Making something for your phone; {queued} more waiting.",
        };
    }

    /// <summary>
    /// The IPv4 addresses a phone on the same network can use, real adapters first. Virtual switches
    /// (Hyper-V, WSL, VirtualBox, VMware) are left out: a phone can't reach them.
    /// </summary>
    public static IReadOnlyList<string> LanAddresses()
    {
        var skip = new[] { "vEthernet", "VirtualBox", "VMware", "Hyper-V", "WSL", "Loopback", "Bluetooth", "docker", "br-", "virbr", "tailscale", "ZeroTier" };
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            && !skip.Any(s => n.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                                              || n.Description.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .Select(n => (Nic: n, Props: n.GetIPProperties()))
                .OrderByDescending(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                        && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(x => x.Props.UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                            && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.Address.ToString())
                .Distinct()
                .Take(3)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<string>();
        }
    }
}

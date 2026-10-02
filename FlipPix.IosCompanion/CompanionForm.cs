using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using FlipPix.Core.Services;
using FlipPix.Remote.Engine;
using FlipPix.Remote.Host;

namespace FlipPix.IosCompanion;

/// <summary>
/// The companion's one window: the pairing code, whether everything the iPad needs is running, and
/// the model credits. Closing it keeps the companion running in the tray; Quit stops it.
/// </summary>
public sealed class CompanionForm : Form
{
    private static readonly string[] Credits = { "Pictures by Krea 2", "Video by MiniMax H3", "Writing by Qwen2.5-VL" };

    private static readonly Color Silver = Color.FromArgb(192, 192, 192);
    private static readonly Color Ok = Color.FromArgb(0, 128, 0);
    private static readonly Color Bad = Color.FromArgb(192, 0, 0);
    private static readonly Color Busy = Color.FromArgb(128, 128, 0);
    private static readonly Font UiFont = new("MS Sans Serif", 8.25f);
    private static readonly Font BoldFont = new("MS Sans Serif", 8.25f, FontStyle.Bold);
    private static readonly Font CodeFont = new("MS Sans Serif", 26f, FontStyle.Bold);
    private static readonly Font MarkFont = new("Marlett", 11f);

    private readonly CompanionLogger _log = new();
    private readonly SettingsService _settings = new();
    private readonly NotifyIcon _tray = new();
    private readonly Label _code, _how, _address, _devices, _activity, _credits;
    private readonly (Label Icon, Label Text) _rowPhone, _rowComfy, _rowLlm, _rowFilter;

    private CompanionConfig? _config;
    private NsfwFilter? _filter;
    private RemoteHost? _host;
    private Supervisor? _supervisor;
    private bool _quitting;
    private bool _toldAboutTray;

    public CompanionForm()
    {
        // Every size below is in 96-DPI pixels; the fonts are in points and grow with the display's
        // scale on their own. Dpi autoscaling grows the layout to match, or the text overflows its boxes.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "FlipPix iOS Companion";
        ClientSize = new Size(460, 410);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Silver;
        Font = UiFont;
        // The exe carries flippix.ico (ApplicationIcon); a single-file publish has no loose .ico beside it.
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { /* default icon */ }

        var banner = new Panel { Location = new Point(0, 0), Size = new Size(460, 56) };
        banner.Paint += (_, e) =>
        {
            // Painted by hand, so it scales its own coordinates (autoscaling only moves controls).
            var k = banner.DeviceDpi / 96f;
            int Px(float v) => (int)Math.Round(v * k);
            using var brush = new LinearGradientBrush(banner.ClientRectangle, Color.FromArgb(0, 0, 128), Color.FromArgb(16, 132, 208), 0f);
            e.Graphics.FillRectangle(brush, banner.ClientRectangle);
            if (Icon != null)
            {
                using var icon = new Icon(Icon, Px(32), Px(32));
                e.Graphics.DrawIcon(icon, new Rectangle(Px(14), Px(12), Px(32), Px(32)));
            }
            using var title = new Font("MS Sans Serif", 12f, FontStyle.Bold);
            e.Graphics.DrawString("FlipPix iOS Companion", title, Brushes.White, Px(54), Px(10));
            e.Graphics.DrawString("Keeps this PC ready for FlipPix on your iPad.", UiFont, Brushes.Gainsboro, Px(56), Px(32));
        };
        Controls.Add(banner);

        var pair = new GroupBox { Text = "Connect your iPad", Location = new Point(10, 64), Size = new Size(440, 134) };
        _code = new Label { Text = "--- ---", Font = CodeFont, Location = new Point(12, 20), Size = new Size(250, 44) };
        var newCode = new Button { Text = "New code", Location = new Point(344, 30), Size = new Size(80, 24) };
        newCode.Click += (_, _) => _host?.NewCodeCommand.Execute(null);
        _how = new Label { Location = new Point(14, 68), Size = new Size(414, 28) };
        _address = new Label { Location = new Point(14, 98), Size = new Size(300, 16) };
        _devices = new Label { Location = new Point(14, 114), Size = new Size(300, 16) };
        pair.Controls.AddRange(new Control[] { _code, newCode, _how, _address, _devices });
        Controls.Add(pair);

        var status = new GroupBox { Text = "Status", Location = new Point(10, 204), Size = new Size(440, 128) };
        _rowPhone = Row(status, 20, "Phone link");
        _rowComfy = Row(status, 44, "Pictures and video (ComfyUI)");
        _rowLlm = Row(status, 68, "Writing assistant");
        _rowFilter = Row(status, 92, "Content filter");
        Controls.Add(status);

        _activity = new Label { Location = new Point(14, 338), Size = new Size(350, 16) };
        // The credits get a line of their own: beside the links they ran into "Licenses".
        _credits = new Label { Text = string.Join("  ·  ", Credits), Location = new Point(14, 364), Size = new Size(432, 16) };
        var licenses = new LinkLabel { Text = "Licenses", Location = new Point(14, 386), AutoSize = true };
        licenses.LinkClicked += (_, _) => Open(Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_LICENSES.md"));
        var logs = new LinkLabel { Text = "Logs", Location = new Point(74, 386), AutoSize = true };
        logs.LinkClicked += (_, _) => Open(_log.FolderPath);
        var hide = new Button { Text = "Hide", Location = new Point(370, 336), Size = new Size(80, 22) };
        hide.Click += (_, _) => HideToTray();
        Controls.AddRange(new Control[] { _activity, _credits, licenses, logs, hide });
        ResumeLayout(false);

        _tray.Icon = Icon ?? SystemIcons.Application;
        _tray.Text = "FlipPix iOS Companion";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Quit", null, async (_, _) => await QuitAsync());
        _tray.ContextMenuStrip = menu;

        RefreshUi();
    }

    private static (Label Icon, Label Text) Row(Control parent, int y, string name)
    {
        var icon = new Label { Location = new Point(12, y), Size = new Size(18, 18) };
        var label = new Label { Text = name, Location = new Point(34, y + 2), Size = new Size(170, 16) };
        var text = new Label { Text = "Checking...", Location = new Point(206, y + 2), Size = new Size(228, 16) };
        parent.Controls.AddRange(new Control[] { icon, label, text });
        return (icon, text);
    }

    private static void SetRow((Label Icon, Label Text) row, bool? ok, string text)
    {
        if (ok == true) { row.Icon.Font = MarkFont; row.Icon.Text = "a"; row.Icon.ForeColor = Ok; }
        else if (ok == false) { row.Icon.Font = MarkFont; row.Icon.Text = "r"; row.Icon.ForeColor = Bad; }
        else { row.Icon.Font = BoldFont; row.Icon.Text = " …"; row.Icon.ForeColor = Busy; }
        row.Text.Text = text;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (Environment.GetCommandLineArgs().Contains("--tray")) BeginInvoke(() => HideToTray(quiet: true));
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The companion failed to start");
            SetRow(_rowPhone, false, "Couldn't start: " + ex.Message);
        }
    }

    private async Task StartAsync()
    {
        _settings.SetLogger(_log);
        _config = CompanionConfig.Load(_settings.Settings);
        Ffmpeg.Ensure(_config.PortableRoot, _log);

        // No filter, no phone link: the licenses of both models require the screening.
        if (File.Exists(_config.FilterModel))
        {
            try { _filter = new NsfwFilter(_config.FilterModel); }
            catch (Exception ex) { _log.LogError(ex, "The content filter didn't load"); }
        }

        var options = new RemoteOptions
        {
            Looks = new HashSet<string> { "photo" },
            Filter = _filter,
            LibraryMadeOnly = true,
            Credits = Credits,
        };
        _host = new RemoteHost(() => _settings.Settings, _log, Dispatch,
            configPath: Path.Combine(CompanionConfig.AppDataDir, "companion-remote.json"),
            dataDir: Path.Combine(CompanionConfig.LocalDir, "remote"),
            options: options);
        _host.PropertyChanged += (_, _) => Dispatch(RefreshUi);
        _host.Devices.CollectionChanged += (_, _) => Dispatch(RefreshUi);
        await _host.InitializeAsync(alwaysOn: _filter != null);

        _supervisor = new Supervisor(_config, () => _settings.Settings, _log);
        _supervisor.Changed += () => Dispatch(RefreshUi);
        _supervisor.Start();
        RefreshUi();
    }

    private void Dispatch(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* closing */ }
    }

    private void RefreshUi()
    {
        var host = _host;
        var running = host?.IsRunning == true;
        _code.Text = running && !string.IsNullOrEmpty(host!.PairingCode) ? host.PairingCode : "--- ---";
        _how.Text = $"On the iPad (same Wi-Fi as this PC), open FlipPix, tap {Environment.MachineName} and type this code.";
        _address.Text = running ? "This PC: " + host!.AddressText : "";
        var paired = host?.Devices.Count ?? 0;
        _devices.Text = paired == 0 ? "No iPad paired yet." : $"Paired: {paired} device{(paired == 1 ? "" : "s")}.";
        _activity.Text = host?.ActivityText ?? "";

        if (_filter == null)
            SetRow(_rowPhone, false, "Off until the content filter is installed");
        else if (host == null)
            SetRow(_rowPhone, null, "Starting...");
        else
            SetRow(_rowPhone, running ? true : host.StatusText.StartsWith("Couldn't") ? false : null, running ? "On" : host.StatusText);

        if (_supervisor is { } s)
        {
            SetRow(_rowComfy, s.ComfyReady ? true : Pending(s.ComfyStatus) ? null : false, s.ComfyStatus);
            SetRow(_rowLlm, s.LlmReady ? true : Pending(s.LlmStatus) ? null : false, s.LlmStatus);
        }
        else
        {
            SetRow(_rowComfy, null, "Checking...");
            SetRow(_rowLlm, null, "Checking...");
        }
        SetRow(_rowFilter, _filter != null, _filter != null ? "On: every picture and video is checked" : "Missing. Run Setup again.");
        _tray.Text = running ? "FlipPix iOS Companion: ready" : "FlipPix iOS Companion";
    }

    private static bool Pending(string status) => status.StartsWith("Starting") || status.StartsWith("Checking");

    private void HideToTray(bool quiet = false)
    {
        Hide();
        if (quiet || _toldAboutTray) return;
        _toldAboutTray = true;
        _tray.ShowBalloonTip(4000, "FlipPix iOS Companion", "Still running, so your iPad can connect. Right-click this icon to quit.", ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The window's X hides it; the companion keeps serving the iPad until Quit or Windows shuts down.
        if (!_quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Shutdown();
        _tray.Visible = false;
        base.OnFormClosed(e);
    }

    private async Task QuitAsync()
    {
        _quitting = true;
        await Task.Run(Shutdown);
        Close();
    }

    private void Shutdown()
    {
        var host = _host;
        _host = null;
        _supervisor?.Dispose();
        _supervisor = null;
        if (host != null)
        {
            try { host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4)); }
            catch (Exception) { /* exiting anyway */ }
        }
        _filter?.Dispose();
        _filter = null;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception) { /* nothing registered to open it */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }
}

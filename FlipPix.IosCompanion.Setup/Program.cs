using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace FlipPix.IosCompanion.Setup;

/// <summary>
/// The one-file installer. Unpacks the embedded ios-companion package (once per package version,
/// under %LocalAppData%\FlipPix\ios-companion-setup\) and runs the setup wizard from it, then
/// removes the unpacked copy once the install has finished.
/// </summary>
internal static class Program
{
    private const string Title = "FlipPix iOS Companion Setup";

    [STAThread]
    private static int Main(string[] args)
    {
        // No visual styles: the classic look matches the wizard that follows.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.SetCompatibleTextRenderingDefault(false);

        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
        if (payload == null)
        {
            MessageBox.Show("This setup file is damaged (it carries no installer). Download it again.",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        (string Dir, string Wizard)? unpacked = null;
        Exception? failure = null;
        using (var splash = new SplashForm())
        {
            splash.Shown += async (_, _) =>
            {
                try { unpacked = await Task.Run(() => Unpack(payload, splash.Report)); }
                catch (Exception ex) { failure = ex; }
                splash.Close();
            };
            Application.Run(splash);
        }
        if (unpacked is not { } package)
        {
            MessageBox.Show("Setup couldn't unpack its files: " + (failure?.Message ?? "unknown error") +
                            "\n\nCheck there is at least 1 GB free on drive C: and try again.",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var started = DateTime.UtcNow;
        var code = RunWizard(package.Wizard, args);
        if (code != 0)
        {
            MessageBox.Show($"Setup stopped with an error (code {code}).\n\nThe logs are in:\n" +
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipPix", "setup-logs"),
                Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return code;
        }
        // Finished: the installed app no longer needs the unpacked package. Cancelled: keep it, so
        // running the setup file again goes straight back to the wizard.
        if (Installed(started)) TryDelete(package.Dir);
        return 0;
    }

    /// <summary>Unpacks the package and returns its folder and the wizard script inside it.</summary>
    private static (string Dir, string Wizard) Unpack(Stream payload, Action<double> report)
    {
        // The folder is named after the package's hash, so a new download never mixes with an old one
        // and running the same file again skips straight to the wizard.
        var hash = Convert.ToHexString(SHA256.HashData(payload))[..16];
        payload.Position = 0;
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlipPix", "ios-companion-setup");
        var dir = Path.Combine(cache, hash);
        var done = Path.Combine(dir, ".unpacked");

        if (!File.Exists(done))
        {
            // Older unpacked versions are no longer needed.
            if (Directory.Exists(cache))
                foreach (var old in Directory.EnumerateDirectories(cache))
                    if (!old.EndsWith(hash, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            using var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
            var total = Math.Max(1L, zip.Entries.Sum(e => e.Length));
            long written = 0;
            var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                // Windows PowerShell 5.1's Compress-Archive writes '\' separators; accept both.
                var name = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(dir, name));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never outside dir
                if (name.EndsWith(Path.DirectorySeparatorChar))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                written += entry.Length;
                report((double)written / total);
            }
            File.WriteAllText(done, DateTime.UtcNow.ToString("O"));
        }

        var wizard = Directory.EnumerateFiles(dir, "flippix-installer.ps1", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("the package has no setup wizard.");
        return (dir, wizard);
    }

    private static int RunWizard(string wizard, string[] args)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";
        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(wizard)!)!,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-File", wizard, "-Companion" })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p == null) return 1;
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>The wizard writes companion.json at the end of a finished install.</summary>
    private static bool Installed(DateTime sinceUtc)
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "companion.json");
        return File.Exists(file) && File.GetLastWriteTimeUtc(file) >= sinceUtc;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* in use or locked: harmless, it's only a cache */ }
    }
}

/// <summary>"Preparing Setup" with a segmented bar, while the package unpacks.</summary>
internal sealed class SplashForm : Form
{
    private readonly ProgressBar _bar;

    public SplashForm()
    {
        Text = "FlipPix iOS Companion Setup";
        ClientSize = new Size(380, 96);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(192, 192, 192);
        Font = new Font("MS Sans Serif", 8.25f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        var label = new Label
        {
            Text = "Preparing Setup, please wait...",
            Location = new Point(16, 18),
            Size = new Size(348, 16),
        };
        _bar = new ProgressBar { Location = new Point(16, 44), Size = new Size(348, 20), Minimum = 0, Maximum = 100 };
        Controls.AddRange(new Control[] { label, _bar });
    }

    /// <summary>Thread-safe progress, 0..1.</summary>
    public void Report(double fraction)
    {
        if (IsDisposed || !IsHandleCreated) return;
        var value = (int)Math.Clamp(fraction * 100, 0, 100);
        try { BeginInvoke(() => { if (_bar.Value != value) _bar.Value = value; }); }
        catch (InvalidOperationException) { /* closing */ }
    }
}

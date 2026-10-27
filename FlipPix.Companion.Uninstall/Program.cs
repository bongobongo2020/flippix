using System.Diagnostics;
using System.Text.Json;

namespace FlipPix.Companion.Uninstall;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new UninstallForm());
    }
}

internal sealed class UninstallForm : Form
{
    private static readonly Color Silver = Color.FromArgb(192, 192, 192);
    private static readonly Font UiFont = new("MS Sans Serif", 8.25f);

    // Protected folders that should NEVER be deleted
    private static readonly HashSet<string> ProtectedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"C:\",
        @"C:\Windows",
        @"C:\Users",
    };

    private readonly ListBox _log;
    private readonly Button _uninstallBtn;
    private readonly Button _cancelBtn;
    private readonly CheckBox _deleteFolder;
    private readonly TextBox _folderPath;
    private bool _running;

    public UninstallForm()
    {
        Text = "Uninstall FlipPix Companion";
        ClientSize = new Size(480, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Silver;
        Font = UiFont;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var label = new Label
        {
            Text = "This will remove FlipPix Companion from your computer:\n\n" +
                   "  - Stop the companion if running\n" +
                   "  - Remove Desktop, Start Menu, and Startup shortcuts\n" +
                   "  - Remove configuration and log files\n" +
                   "  - Remove firewall rules",
            Location = new Point(16, 16),
            Size = new Size(448, 80),
        };

        _deleteFolder = new CheckBox
        {
            Text = "Also delete this folder:",
            Location = new Point(16, 100),
            Size = new Size(150, 20),
            Checked = false,
        };

        _folderPath = new TextBox
        {
            Text = FindCompanionFolder(),
            Location = new Point(170, 98),
            Size = new Size(290, 22),
            Enabled = false,
        };
        _deleteFolder.CheckedChanged += (_, _) => _folderPath.Enabled = _deleteFolder.Checked;

        _log = new ListBox
        {
            Location = new Point(16, 130),
            Size = new Size(448, 200),
            Font = new Font("Consolas", 9f),
        };

        _uninstallBtn = new Button { Text = "Uninstall", Location = new Point(290, 340), Size = new Size(90, 26) };
        _uninstallBtn.Click += async (_, _) => await UninstallAsync();

        _cancelBtn = new Button { Text = "Cancel", Location = new Point(386, 340), Size = new Size(78, 26) };
        _cancelBtn.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { label, _deleteFolder, _folderPath, _log, _uninstallBtn, _cancelBtn });
    }

    /// <summary>Try to find where FlipPix Companion is installed by checking config or common locations.</summary>
    private static string FindCompanionFolder()
    {
        // Check companion.json for PortableRoot
        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "companion.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("PortableRoot", out var prop))
                {
                    var root = prop.GetString();
                    if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                        return root;
                }
            }
            catch { }
        }

        // Check common installation locations
        var commonPaths = new[]
        {
            @"C:\FlipPix",
            @"C:\FlipPix-Companion",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlipPix"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipPix"),
        };

        foreach (var path in commonPaths)
        {
            if (Directory.Exists(path) &&
                (File.Exists(Path.Combine(path, "FlipPix.IosCompanion.exe")) ||
                 Directory.Exists(Path.Combine(path, "ComfyUI"))))
                return path;
        }

        return @"C:\FlipPix";
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }
        _log.Items.Add(message);
        _log.TopIndex = _log.Items.Count - 1;
    }

    private static bool IsSafeToDelete(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var normalized = Path.GetFullPath(folder).TrimEnd('\\', '/');

        // Never delete protected system folders
        if (ProtectedFolders.Contains(normalized)) return false;

        // Never delete if it's a root drive
        if (normalized.Length <= 3) return false;

        // Must contain "flippix" in the path name
        if (!normalized.Contains("flippix", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("FlipPix", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private async Task UninstallAsync()
    {
        if (_running) return;
        _running = true;
        _uninstallBtn.Enabled = false;
        _deleteFolder.Enabled = false;
        _folderPath.Enabled = false;

        var deleteFolder = _deleteFolder.Checked;
        var folderToDelete = _folderPath.Text.Trim();

        await Task.Run(() =>
        {
            // 1. Stop companion process
            Log("Stopping FlipPix Companion...");
            try
            {
                foreach (var p in Process.GetProcessesByName("FlipPix.IosCompanion"))
                {
                    try { p.Kill(); p.WaitForExit(5000); Log($"  Stopped process {p.Id}"); }
                    catch { Log($"  Could not stop process {p.Id}"); }
                }
            }
            catch (Exception ex) { Log($"  Error: {ex.Message}"); }

            // 2. Remove shortcuts
            Log("Removing shortcuts...");
            RemoveShortcuts();

            // 3. Remove config and data
            Log("Removing configuration and data...");
            RemoveConfigAndData();

            // 4. Remove firewall rules
            Log("Removing firewall rules...");
            RemoveFirewallRules();

            // 5. Optionally delete folder (with safety checks!)
            if (deleteFolder && !string.IsNullOrEmpty(folderToDelete))
            {
                if (IsSafeToDelete(folderToDelete))
                {
                    Log($"Deleting folder: {folderToDelete}");
                    try
                    {
                        if (Directory.Exists(folderToDelete))
                        {
                            Directory.Delete(folderToDelete, recursive: true);
                            Log("  Folder deleted.");
                        }
                        else
                        {
                            Log("  Folder does not exist.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"  Could not delete folder: {ex.Message}");
                        Log("  You may need to delete it manually.");
                    }
                }
                else
                {
                    Log($"SAFETY: Refusing to delete '{folderToDelete}'");
                    Log("  This folder is protected or doesn't look like a FlipPix folder.");
                }
            }

            Log("");
            Log("Uninstall complete!");
        });

        _cancelBtn.Text = "Close";
    }

    private void RemoveShortcuts()
    {
        var shortcuts = new[]
        {
            // Desktop
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FlipPix Companion.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FlipPix iOS Companion.lnk"),
            // Start Menu
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FlipPix", "FlipPix Companion.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FlipPix", "FlipPix iOS Companion.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FlipPix", "Uninstall FlipPix Companion.lnk"),
            // Startup
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "FlipPix Companion.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "FlipPix iOS Companion.lnk"),
        };

        foreach (var shortcut in shortcuts)
        {
            try
            {
                if (File.Exists(shortcut))
                {
                    File.Delete(shortcut);
                    Log($"  Removed: {Path.GetFileName(shortcut)}");
                }
            }
            catch (Exception ex) { Log($"  Could not remove {Path.GetFileName(shortcut)}: {ex.Message}"); }
        }

        // Remove Start Menu folder if empty
        var startMenuFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FlipPix");
        try
        {
            if (Directory.Exists(startMenuFolder) && !Directory.EnumerateFileSystemEntries(startMenuFolder).Any())
            {
                Directory.Delete(startMenuFolder);
                Log("  Removed empty Start Menu folder");
            }
        }
        catch { }
    }

    private void RemoveConfigAndData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var filesToDelete = new[]
        {
            Path.Combine(appData, "FlipPix", "companion.json"),
            Path.Combine(appData, "FlipPix", "companion-remote.json"),
        };

        var foldersToDelete = new[]
        {
            Path.Combine(localAppData, "FlipPix", "companion"),
            Path.Combine(localAppData, "FlipPix", "ios-companion-setup"),
        };

        foreach (var file in filesToDelete)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    Log($"  Removed: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex) { Log($"  Could not remove {Path.GetFileName(file)}: {ex.Message}"); }
        }

        foreach (var folder in foldersToDelete)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                    Log($"  Removed: {Path.GetFileName(folder)}");
                }
            }
            catch (Exception ex) { Log($"  Could not remove {Path.GetFileName(folder)}: {ex.Message}"); }
        }
    }

    private void RemoveFirewallRules()
    {
        var ruleNames = new[] { "FlipPix phone remote", "FlipPix Phone Remote" };

        foreach (var ruleName in ruleNames)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall delete rule name=\"{ruleName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(10000);
                if (p?.ExitCode == 0)
                    Log($"  Removed firewall rule: {ruleName}");
            }
            catch (Exception ex) { Log($"  Could not remove firewall rule: {ex.Message}"); }
        }
    }

    private void ScheduleFolderDeletion(string folder)
    {
        // Use cmd to delete folder after this process exits
        var batchFile = Path.Combine(Path.GetTempPath(), "uninstall-flippix-companion.bat");
        var batch = $"""
            @echo off
            timeout /t 2 /nobreak >nul
            rd /s /q "{folder}"
            del "%~f0"
            """;
        try
        {
            File.WriteAllText(batchFile, batch);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Log("  Folder will be deleted after closing this window");
        }
        catch (Exception ex) { Log($"  Could not schedule folder deletion: {ex.Message}"); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running && _cancelBtn.Text != "Close")
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }
}

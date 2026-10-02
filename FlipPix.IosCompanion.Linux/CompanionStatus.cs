using System.Diagnostics;
using System.Text.Json;

namespace FlipPix.IosCompanion;

/// <summary>
/// What the running companion shows in place of a window, written to <c>status.json</c> beside its
/// logs (<c>~/.local/share/FlipPix/companion</c>) and read back by <c>flippix-companion status</c>.
/// </summary>
public sealed class CompanionStatus
{
    public string Host { get; set; } = "";
    public int Pid { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string PairingCode { get; set; } = "";
    public DateTime CodeIssuedUtc { get; set; }
    public string Address { get; set; } = "";
    public int PairedDevices { get; set; }
    public string Activity { get; set; } = "";
    public Row Phone { get; set; } = new();
    public Row Comfy { get; set; } = new();
    public Row Llm { get; set; } = new();
    public Row Filter { get; set; } = new();
    public string LogFolder { get; set; } = "";

    /// <summary>One status line: Ok true / false, or null while it's starting.</summary>
    public sealed class Row
    {
        public bool? Ok { get; set; }
        public string Text { get; set; } = "Checking...";
    }

    public static string FilePath => Path.Combine(CompanionConfig.LocalDir, "status.json");

    /// <summary>Dropped by <c>flippix-companion code</c>; the service answers with a fresh code.</summary>
    public static string NewCodeRequestPath => Path.Combine(CompanionConfig.LocalDir, "new-code.request");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static CompanionStatus? Load()
    {
        try { return JsonSerializer.Deserialize<CompanionStatus>(File.ReadAllText(FilePath)); }
        catch (Exception) { return null; }
    }

    /// <summary>True when the service that wrote this is still running and writing.</summary>
    public bool IsLive()
    {
        if (DateTime.UtcNow - UpdatedUtc > TimeSpan.FromSeconds(30)) return false;
        try { using var p = Process.GetProcessById(Pid); return !p.HasExited; }
        catch (Exception) { return false; }
    }
}

internal static class StatusCommand
{
    /// <summary>A code is good for 15 minutes (RemoteHost.CodeLifetime); stop showing it a little before.</summary>
    private static readonly TimeSpan CodeShownFor = TimeSpan.FromMinutes(14);

    public static int Print()
    {
        var s = CompanionStatus.Load();
        if (s == null || !s.IsLive())
        {
            Console.WriteLine("FlipPix iOS Companion isn't running.");
            Console.WriteLine("  Start it:   systemctl --user start flippix-companion");
            Console.WriteLine("  Why not:    journalctl --user -u flippix-companion -n 50");
            return 1;
        }

        var color = !Console.IsOutputRedirected;
        string Bold(string t) => color ? $"\e[1m{t}\e[0m" : t;
        string Paint(bool? ok, string t) => !color ? t : ok switch { true => $"\e[32m{t}\e[0m", false => $"\e[31m{t}\e[0m", _ => $"\e[33m{t}\e[0m" };

        Console.WriteLine();
        Console.WriteLine(Bold($"FlipPix iOS Companion on {s.Host}"));
        Console.WriteLine();
        var codeFresh = s.PairingCode.Length > 0 && DateTime.UtcNow - s.CodeIssuedUtc < CodeShownFor;
        if (codeFresh)
        {
            Console.WriteLine($"  Pairing code:  {Bold(s.PairingCode)}");
            Console.WriteLine($"  On the iPad (same Wi-Fi as this PC), open FlipPix, tap {s.Host} and type this code.");
        }
        else if (s.Phone.Ok == true)
        {
            Console.WriteLine("  Pairing code:  expired. Run  flippix-companion code  for a new one.");
        }
        if (s.Address.Length > 0) Console.WriteLine($"  This PC:       {s.Address}");
        Console.WriteLine(s.PairedDevices == 0 ? "  No iPad paired yet." : $"  Paired:        {s.PairedDevices} device{(s.PairedDevices == 1 ? "" : "s")}");
        Console.WriteLine();
        foreach (var (name, row) in new[] { ("Phone link", s.Phone), ("Pictures and video (ComfyUI)", s.Comfy),
                                            ("Writing assistant", s.Llm), ("Content filter", s.Filter) })
        {
            var mark = row.Ok switch { true => "[ok]", false => "[x] ", _ => "[..]" };
            Console.WriteLine($"  {Paint(row.Ok, mark)} {name,-30} {row.Text}");
        }
        if (s.Activity.Length > 0) Console.WriteLine($"\n  {s.Activity}");
        Console.WriteLine();
        Console.WriteLine($"  Logs: {s.LogFolder}");
        return 0;
    }

    public static async Task<int> NewCodeAsync()
    {
        var before = CompanionStatus.Load();
        if (before == null || !before.IsLive()) return Print();
        Directory.CreateDirectory(CompanionConfig.LocalDir);
        await File.WriteAllTextAsync(CompanionStatus.NewCodeRequestPath, DateTime.UtcNow.ToString("O"));
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            var now = CompanionStatus.Load();
            if (now != null && now.CodeIssuedUtc > before.CodeIssuedUtc && now.PairingCode.Length > 0) break;
        }
        return Print();
    }
}

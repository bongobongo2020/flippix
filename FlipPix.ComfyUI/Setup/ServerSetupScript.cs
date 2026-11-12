using System.Text;

namespace FlipPix.ComfyUI.Setup;

/// <summary>A model to fetch on the server itself: where it goes there, and where from.</summary>
public sealed record ScriptModel(string ServerFolder, string RelativeName, string Url);

/// <summary>
/// Builds a script the user can paste into a terminal on the ComfyUI server, for whatever FlipPix can't
/// put there itself (no reachable model share, or Manager refuses a pack). Bash for Linux/macOS,
/// PowerShell for Windows. Downloads resume, so the script can be re-run after an interruption.
/// </summary>
public static class ServerSetupScript
{
    public static string Build(bool windows, string serverUrl, string? customNodesDir,
        IReadOnlyList<string> repoUrls, IReadOnlyList<ScriptModel> models)
    {
        return windows
            ? BuildPowerShell(serverUrl, customNodesDir, repoUrls, models)
            : BuildBash(serverUrl, customNodesDir, repoUrls, models);
    }

    private static string BuildBash(string serverUrl, string? customNodesDir, IReadOnlyList<string> repos, IReadOnlyList<ScriptModel> models)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine($"# FlipPix setup for the ComfyUI at {serverUrl}. Run it on that server.");
        sb.AppendLine("# Safe to re-run: downloads resume and existing node packs are skipped.");
        sb.AppendLine("set -u");
        if (repos.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# --- custom node packs ---");
            sb.AppendLine($"cd {Q(customNodesDir ?? "ComfyUI/custom_nodes")} || exit 1");
            foreach (var r in repos)
            {
                var name = FlipPix.Core.Services.NodeCatalog.PackNameFromRepo(r);
                sb.AppendLine($"[ -d {Q(name)} ] || git clone --depth 1 {Q(r)} {Q(name)}");
            }
            sb.AppendLine("# Packs with a requirements.txt need it installed with ComfyUI's own Python, e.g.:");
            sb.AppendLine("#   /path/to/comfyui/python -m pip install -r <pack>/requirements.txt");
            sb.AppendLine("# (or use \"Try fix\" on the pack in ComfyUI-Manager after the restart).");
        }
        if (models.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# --- models ---");
            foreach (var m in models)
            {
                var target = m.ServerFolder.TrimEnd('/') + "/" + m.RelativeName.Replace('\\', '/');
                var dir = target[..target.LastIndexOf('/')];
                sb.AppendLine($"mkdir -p {Q(dir)} && curl -L --fail -C - -o {Q(target)} {Q(m.Url)}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("echo \"Done. Restart ComfyUI, then click Rescan in FlipPix.\"");
        return sb.ToString();

        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
    }

    private static string BuildPowerShell(string serverUrl, string? customNodesDir, IReadOnlyList<string> repos, IReadOnlyList<ScriptModel> models)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# FlipPix setup for the ComfyUI at {serverUrl}. Run it in PowerShell on that server.");
        sb.AppendLine("# Safe to re-run: downloads resume and existing node packs are skipped.");
        if (repos.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# --- custom node packs ---");
            sb.AppendLine($"Set-Location {Q(customNodesDir ?? "ComfyUI\\custom_nodes")}");
            foreach (var r in repos)
            {
                var name = FlipPix.Core.Services.NodeCatalog.PackNameFromRepo(r);
                sb.AppendLine($"if (-not (Test-Path {Q(name)})) {{ git clone --depth 1 {Q(r)} {Q(name)} }}");
            }
            sb.AppendLine("# Packs with a requirements.txt need it installed with ComfyUI's own Python, e.g.:");
            sb.AppendLine("#   python_embeded\\python.exe -m pip install -r <pack>\\requirements.txt");
        }
        if (models.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# --- models ---");
            foreach (var m in models)
            {
                var target = m.ServerFolder.TrimEnd('\\', '/') + "\\" + m.RelativeName.Replace('/', '\\');
                var dir = target[..target.LastIndexOf('\\')];
                sb.AppendLine($"New-Item -ItemType Directory -Force {Q(dir)} | Out-Null; curl.exe -L --fail -C - -o {Q(target)} {Q(m.Url)}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Write-Host 'Done. Restart ComfyUI, then click Rescan in FlipPix.'");
        return sb.ToString();

        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
    }
}

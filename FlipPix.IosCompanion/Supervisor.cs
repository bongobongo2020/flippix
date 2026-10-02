using System.Diagnostics;
using System.Runtime.InteropServices;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;

namespace FlipPix.IosCompanion;

/// <summary>
/// Keeps the two servers the iPad needs running on this PC: ComfyUI and the writing assistant
/// (llama-server). Each is started only when it isn't already answering, so a ComfyUI the user runs
/// themselves is left alone, and whatever this started is stopped when the companion quits.
/// </summary>
public sealed class Supervisor : IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(15); // first ComfyUI start installs things
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly CompanionConfig _config;
    private readonly Func<ComfyUISettings> _settings;
    private readonly IAppLogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _stop = new();
    private readonly Managed _comfy = new("ComfyUI", "comfyui.log");
    private readonly Managed _llm = new("Writing assistant", "writing-assistant.log");

    public Supervisor(CompanionConfig config, Func<ComfyUISettings> settings, IAppLogger log)
    {
        _config = config;
        _settings = settings;
        _log = log;
    }

    public string ComfyStatus => _comfy.Status;
    public string LlmStatus => _llm.Status;
    public bool ComfyReady => _comfy.Ready;
    public bool LlmReady => _llm.Ready;

    /// <summary>Raised (on a worker thread) after each round of checks.</summary>
    public event Action? Changed;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var s = _settings();
            await KeepAsync(_comfy, Normalize(s.BaseUrl, "http://127.0.0.1:8188") + "/system_stats", StartComfy);
            await KeepAsync(_llm, Normalize(s.LMStudioSettings?.BaseUrl, "http://127.0.0.1:8080") + "/health", StartLlm);
            Changed?.Invoke();
            try { await Task.Delay(CheckEvery, _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task KeepAsync(Managed m, string probeUrl, Func<Process?> start)
    {
        if (await AnswersAsync(probeUrl))
        {
            m.Set(true, "Running");
            return;
        }
        if (!IsLocal(probeUrl))
        {
            m.Set(false, "Not reachable at " + new Uri(probeUrl).GetLeftPart(UriPartial.Authority));
            return;
        }
        var alive = m.Process is { HasExited: false };
        if (alive && DateTime.UtcNow - m.StartedUtc < StartGrace)
        {
            m.Set(false, "Starting (the first start takes a few minutes)");
            return;
        }
        if (!alive && DateTime.UtcNow - m.StartedUtc < RetryAfter)
        {
            m.Set(false, "Stopped; trying again shortly. See the logs if this keeps happening.");
            return;
        }
        // Quitting: a start now would outlive the companion.
        if (_stop.IsCancellationRequested) return;
        try
        {
            m.Stop();
            m.Process = start();
            m.StartedUtc = DateTime.UtcNow;
            m.Set(false, m.Process == null ? "Not installed. Run Setup again." : "Starting");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Couldn't start {0}", m.Name);
            m.StartedUtc = DateTime.UtcNow;
            m.Set(false, "Couldn't start: " + ex.Message);
        }
    }

    private Process? StartComfy()
    {
        // Windows: the portable build's python_embeded. Linux: the venv the Ubuntu installer made
        // beside ComfyUI (scripts/install-ios-companion-linux.sh).
        var root = _config.PortableRoot;
        var windows = OperatingSystem.IsWindows();
        var python = windows
            ? Path.Combine(root, "python_embeded", "python.exe")
            : Path.Combine(root, "venv", "bin", "python");
        if (!File.Exists(python)) return null;
        var psi = Hidden(python, root);
        var args = windows
            ? new[] { "-s", Path.Combine("ComfyUI", "main.py"), "--windows-standalone-build", "--disable-auto-launch" }
            : new[] { "-s", Path.Combine("ComfyUI", "main.py"), "--listen", "127.0.0.1", "--port", "8188", "--disable-auto-launch" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return _comfy.Launch(psi, _log);
    }

    private Process? StartLlm()
    {
        // start-llm.bat on Windows, start-llm.sh on Linux.
        var script = _config.LlmStartScript;
        if (!File.Exists(script)) return null;
        var psi = Hidden(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash", Path.GetDirectoryName(script) ?? "");
        if (OperatingSystem.IsWindows()) psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(script);
        return _llm.Launch(psi, _log);
    }

    private static ProcessStartInfo Hidden(string file, string workDir) => new(file)
    {
        WorkingDirectory = workDir,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private async Task<bool> AnswersAsync(string url)
    {
        try
        {
            using var r = await _http.GetAsync(url, _stop.Token);
            return r.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    private static string Normalize(string? url, string fallback)
    {
        var u = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim().TrimEnd('/');
        return u.Contains("://") ? u : "http://" + u;
    }

    private static bool IsLocal(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return host is "127.0.0.1" or "localhost" or "::1"
                   || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException) { return false; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _comfy.Stop();
        _llm.Stop();
        _http.Dispose();
    }

    /// <summary>One server this may have started, with its log.</summary>
    private sealed class Managed
    {
        private readonly object _logLock = new();
        private StreamWriter? _logFile;

        public Managed(string name, string logName)
        {
            Name = name;
            LogPath = Path.Combine(CompanionConfig.LocalDir, "logs", logName);
        }

        public string Name { get; }
        public string LogPath { get; }
        public Process? Process { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.MinValue;
        public bool Ready { get; private set; }
        public string Status { get; private set; } = "Checking...";

        public void Set(bool ready, string status) => (Ready, Status) = (ready, status);

        public Process? Launch(ProcessStartInfo psi, IAppLogger log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            lock (_logLock)
            {
                _logFile?.Dispose();
                _logFile = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            }
            var p = Process.Start(psi);
            if (p == null) return null;
            KillWithCompanion.Add(p, log);
            p.OutputDataReceived += (_, e) => Write(e.Data);
            p.ErrorDataReceived += (_, e) => Write(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            log.LogInfo("Started {0} (pid {1})", Name, p.Id);
            return p;
        }

        private void Write(string? line)
        {
            if (line == null) return;
            lock (_logLock) _logFile?.WriteLine(line);
        }

        public void Stop()
        {
            var p = Process;
            Process = null;
            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch (Exception) { /* already gone */ }
                p.Dispose();
            }
            lock (_logLock)
            {
                _logFile?.Dispose();
                _logFile = null;
            }
        }
    }

    /// <summary>
    /// Windows: what the companion starts dies with it, however it ends. Dispose kills them on Quit,
    /// but ending the companion from Task Manager skips that, and the orphaned ComfyUI then kept
    /// answering with whatever nodes it loaded at its start: the next companion and Setup's node
    /// updates found it running and left it alone. A job that kills its processes when its last handle
    /// closes (the companion's, at exit) covers every way out. Processes the servers start inherit it.
    /// (Linux needs none: systemd stops the service's whole cgroup.)
    /// </summary>
    private static class KillWithCompanion
    {
        private static readonly Lazy<IntPtr> Job = new(Create);

        public static void Add(Process p, IAppLogger log)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                if (Job.Value == IntPtr.Zero || !AssignProcessToJobObject(Job.Value, p.Handle))
                    log.LogWarning("{0} won't stop by itself if the companion is ended from Task Manager (error {1})",
                        p.ProcessName, Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { log.LogWarning("Couldn't tie pid {0} to the companion: {1}", p.Id, ex.Message); }
        }

        private static IntPtr Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job; // never closed: the handle closing at exit is what kills the servers
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// The local llama-server shares one GPU with ComfyUI and Yakkinator. Whatever they keep loaded pushes the
    /// LLM's weights and KV cache out of VRAM into shared system RAM, where it runs many times slower. Just
    /// before FlipPix talks to a llama-server on this PC, this frees what the other two hold — when VRAM is
    /// actually short — so the LLM gets the card:
    /// <list type="bullet">
    /// <item>ComfyUI: <c>POST /free</c> (unload models + free memory). Skipped while a FlipPix render holds the
    /// queue, so a running chain of renders keeps its models warm.</item>
    /// <item>Yakkinator: <c>POST /remote/v1/unload</c>. It answers "busy" while speaking, and its engines load
    /// again by themselves on the next line.</item>
    /// </list>
    /// Best-effort throughout: nothing here ever fails or holds up the LLM call by more than a few seconds.
    /// </summary>
    public sealed class GpuMemoryBroker
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(20);

        private readonly ComfyUIService _comfy;
        private readonly WorkflowQueueCoordinator _queue;
        private readonly SettingsService _settings;
        private readonly IAppLogger _logger;
        private readonly YakkinatorClient _yak = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTime _lastFreed = DateTime.MinValue;

        public GpuMemoryBroker(ComfyUIService comfy, WorkflowQueueCoordinator queue, SettingsService settings, IAppLogger logger)
        {
            _comfy = comfy;
            _queue = queue;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>Makes room on the GPU for the local LLM at <paramref name="llmBaseUrl"/>.
        /// <paramref name="loadingModel"/>: the server is about to load a model, the moment free VRAM matters
        /// most — then the throttle is ignored.</summary>
        public async Task FreeForLlmAsync(string llmBaseUrl, bool loadingModel, CancellationToken ct)
        {
            var llm = _settings.Settings?.LMStudioSettings;
            if (llm == null || !llm.AutoFreeGpuMemory || !IsThisPc(llmBaseUrl)) return;
            if (!loadingModel && DateTime.UtcNow - _lastFreed < MinInterval) return;

            if (!await _gate.WaitAsync(0, ct)) return; // another call is already freeing
            try
            {
                var freeGb = await QueryFreeVramGbAsync(ct);
                var wantGb = Math.Max(0, llm.AutoFreeMinFreeGb);
                if (freeGb is { } gb && gb >= wantGb) return;

                var freed = new System.Collections.Generic.List<string>();
                var comfyLocal = IsThisPc(_settings.Settings?.BaseUrl ?? string.Empty);
                if (comfyLocal && _queue.CurrentWorkflowType == null)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    try { if (await _comfy.FreeMemoryAsync(cancellationToken: cts.Token)) freed.Add("ComfyUI"); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    catch (Exception ex) { _logger.LogWarning($"GPU memory: ComfyUI /free failed: {ex.Message}"); }
                }

                var yak = await _yak.UnloadAsync(ct);
                if (yak == YakUnload.Unloaded) freed.Add("Yakkinator");

                _lastFreed = DateTime.UtcNow;
                if (freed.Count == 0) return;

                // Give the drivers a moment to hand the memory back before the LLM allocates.
                await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
                var after = await QueryFreeVramGbAsync(ct);
                _logger.LogInfo($"GPU memory: {(freeGb is { } b ? $"{b:0.0} GB" : "unknown")} free before the LLM call " +
                                $"(want {wantGb:0.#} GB) — freed {string.Join(" + ", freed)}" +
                                (after is { } a ? $", now {a:0.0} GB free." : "."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning($"GPU memory: freeing for the LLM failed: {ex.Message}");
            }
            finally { _gate.Release(); }
        }

        /// <summary>Free VRAM on the emptiest NVIDIA GPU, in GB; null when nvidia-smi can't tell.</summary>
        public static async Task<double?> QueryFreeVramGbAsync(CancellationToken ct)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p == null) return null;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token);
                var mib = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => double.TryParse(l.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1)
                    .Where(v => v >= 0)
                    .DefaultIfEmpty(-1)
                    .Max();
                return mib < 0 ? null : mib / 1024.0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        /// <summary>Whether <paramref name="baseUrl"/> points at this PC (loopback, its own name or one of its addresses).</summary>
        public static bool IsThisPc(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl.Contains("://") ? baseUrl : "http://" + baseUrl, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host;
            if (uri.IsLoopback || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!IPAddress.TryParse(host, out var ip)) return false;
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Any(a => a.Address.Equals(ip));
            }
            catch { return false; }
        }
    }
}

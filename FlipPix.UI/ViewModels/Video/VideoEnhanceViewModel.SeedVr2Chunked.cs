using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// SeedVR2 on clips too long for one prompt: cut into overlapping pieces sized from the server's free RAM,
    /// upscaled one by one, crossfaded back together with the original soundtrack. Automatic — a clip that fits in
    /// one piece runs the ordinary single-prompt path. See <see cref="SeedVr2Chunks"/>.
    ///
    /// <para>Finished pieces are kept in a work folder named after the source file and scale, so a crash, a ComfyUI
    /// restart or the queue's own retry resumes from the next unfinished piece instead of starting again.</para>
    /// </summary>
    public partial class VideoEnhanceViewModel
    {
        private const string SeedVr2PostNode = "113";        // SeedVR2PostProcessing — colour correction
        private const string SeedVr2SageNode = "128";        // PathchSageAttentionKJ — needs the sageattention module

        /// <summary>Set once a run fails because the server has no <c>sageattention</c> module; every SeedVR2 graph
        /// after that is sent with the patch "disabled" (plain attention: slower, same result).</summary>
        private bool _seedVr2SageUnavailable;

        /// <summary>Work folders older than this are leftovers from runs that were never finished.</summary>
        private static readonly TimeSpan ChunkWorkMaxAge = TimeSpan.FromDays(7);

        private static string ChunkWorkRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipPix", "seedvr2-chunks");

        private sealed record VideoProbe(int Width, int Height, int FpsNum, int FpsDen, int Frames, string? AudioCodec);

        private sealed record ChunkJob(string Key, long Seed, double Scale, int FpsNum, int FpsDen, SeedVr2ChunkPlan Plan);

        /// <summary>
        /// Runs <paramref name="item"/> as a chunked SeedVR2 upscale when it is too long for one prompt, and returns
        /// its final path; null when it fits in one piece (or cannot be measured) and the caller should run it whole.
        /// </summary>
        private async Task<string?> TryRunSeedVr2ChunkedAsync(VideoEnhanceQueueItem item, CancellationToken token)
        {
            var ffmpeg = FindFFmpeg();
            var ffprobe = ffmpeg == null ? null : Path.Combine(Path.GetDirectoryName(ffmpeg)!, Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe"));
            if (ffmpeg == null || ffprobe == null || !File.Exists(ffprobe))
            {
                AddLog("SeedVR2: ffmpeg/ffprobe not found on this PC, so the clip runs as one piece (long clips may run the server out of RAM).");
                return null;
            }

            ProcessingStatus = "Measuring the clip...";
            var probe = await Task.Run(() => ProbeVideo(ffprobe, item.InputVideoPath), token);
            if (probe == null)
            {
                AddLog("SeedVR2: could not measure the clip, so it runs as one piece.");
                return null;
            }

            var scale = item.UpscaleScale <= 0 ? DefaultSeedVr2Scale : item.UpscaleScale;
            var key = ChunkKey(item.InputVideoPath, scale);
            var workDir = Path.Combine(ChunkWorkRoot, key);
            PruneChunkWork(key);

            var job = LoadChunkJob(workDir);
            if (job == null)
            {
                var freeRam = await GetServerFreeRamAsync(token);
                var maxFrames = SeedVr2Chunks.MaxFramesForRam(probe.Width, probe.Height, scale, freeRam);
                var fresh = SeedVr2Chunks.Plan(probe.Frames, maxFrames);
                AddLog($"SeedVR2: {probe.Frames} frames {probe.Width}×{probe.Height} @ {(double)probe.FpsNum / probe.FpsDen:0.###} fps, " +
                       $"server RAM free {(freeRam > 0 ? $"{freeRam / 1073741824.0:0.#} GB" : "unknown")} → up to {maxFrames} frames per piece at ×{scale:0.##}");
                if (!fresh.IsChunked) return null;

                job = new ChunkJob(key, BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0) & long.MaxValue,
                    scale, probe.FpsNum, probe.FpsDen, fresh);
                Directory.CreateDirectory(workDir);
                SaveChunkJob(workDir, job);
            }
            else
            {
                AddLog($"SeedVR2: resuming a chunked upscale of this clip ({workDir}).");
            }

            var plan = job.Plan;
            AddLog($"SeedVR2: {plan.Chunks.Count} pieces of ~{plan.Chunks[0].Length} frames, {plan.OverlapFrames}-frame crossfades, LAB colour match");

            var pieces = new List<string>();
            var serverFiles = new List<string>();
            foreach (var chunk in plan.Chunks)
            {
                token.ThrowIfCancellationRequested();
                var label = $"piece {chunk.Index + 1}/{plan.Chunks.Count}";
                var done = Path.Combine(workDir, $"up_{chunk.Index:000}.mp4");
                if (File.Exists(done) && (ProbeVideo(ffprobe, done)?.Frames ?? 0) >= chunk.Length)
                {
                    AddLog($"SeedVR2: {label} already done");
                    pieces.Add(done);
                    continue;
                }

                SetChunkProgress(chunk.Index, plan.Chunks.Count, 0, $"Cutting {label}...");
                var cut = Path.Combine(workDir, $"svr2_{key[..8]}_c{chunk.Index:000}.mp4");
                await Task.Run(() => RunFFmpeg(ffmpeg, SeedVr2Chunks.CutArguments(item.InputVideoPath, chunk, job.FpsNum, job.FpsDen, cut)), token);

                SetChunkProgress(chunk.Index, plan.Chunks.Count, 0.05, $"Uploading {label}...");
                var uploaded = await _comfyUIService.UploadVideoAsync(cut, token);
                if (string.IsNullOrEmpty(uploaded)) throw new Exception($"Upload of {label} failed.");

                var prefix = $"{UpscaleOutputSubfolder}/SVR2_{key[..8]}_c{chunk.Index:000}";
                var workflow = await BuildSeedVr2ChunkWorkflowAsync(uploaded, job.Scale, job.Seed, prefix);

                SetChunkProgress(chunk.Index, plan.Chunks.Count, 0.1, $"Upscaling {label}...");
                var index = chunk.Index;
                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(p =>
                {
                    if (p.Data?.Value != null && p.Data?.Max != null && p.Data.Max > 0)
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            SetChunkProgress(index, plan.Chunks.Count, 0.1 + 0.85 * p.Data.Value / (double)p.Data.Max,
                                $"Upscaling {label}: {p.Data.Value}/{p.Data.Max}"));
                });
                var promptId = await ExecuteSeedVr2Async(workflow,
                    () => BuildSeedVr2ChunkWorkflowAsync(uploaded, job.Scale, job.Seed, prefix), progress, token);
                AddLog($"SeedVR2: {label} submitted, ID {promptId}");

                var fromServer = await TryGetVideoFromHistoryAsync(promptId);
                if (fromServer == null)
                {
                    var file = await _comfyUIService.HttpClient.FindOutputFileFromHistoryAsync(prefix, token);
                    if (file != null) fromServer = await FetchOutputFileAsync(file, token);
                }
                if (fromServer == null || !File.Exists(fromServer)) throw new Exception($"SeedVR2 {label} produced no video.");

                if (IsDownloadedCopy(fromServer)) File.Move(fromServer, done, true);
                else { File.Copy(fromServer, done, true); serverFiles.Add(fromServer); }

                var got = ProbeVideo(ffprobe, done)?.Frames ?? 0;
                if (got < chunk.Length)
                {
                    File.Delete(done);
                    throw new Exception($"SeedVR2 {label} came back with {got} frames, expected {chunk.Length}.");
                }
                try { File.Delete(cut); } catch { /* temp piece: best effort */ }
                pieces.Add(done);
                AddLog($"SeedVR2: {label} done");
            }

            ProcessingProgress = 95;
            ProcessingStatus = "Joining the pieces...";
            var joined = Path.Combine(workDir, "joined.mp4");
            try
            {
                await Task.Run(() => RunFFmpeg(ffmpeg, SeedVr2Chunks.JoinArguments(pieces, plan, job.FpsNum, job.FpsDen,
                    item.InputVideoPath, copyAudio: true, joined), 3_600_000), token);
            }
            catch (InvalidOperationException) when (probe.AudioCodec != null)
            {
                AddLog($"SeedVR2: the soundtrack ({probe.AudioCodec}) could not be copied as-is; re-encoding it to AAC.");
                await Task.Run(() => RunFFmpeg(ffmpeg, SeedVr2Chunks.JoinArguments(pieces, plan, job.FpsNum, job.FpsDen,
                    item.InputVideoPath, copyAudio: false, joined), 3_600_000), token);
            }

            var final = ProbeVideo(ffprobe, joined);
            if (final == null || final.Frames != plan.TotalFrames)
                throw new Exception($"The joined video has {final?.Frames ?? 0} frames, expected {plan.TotalFrames}; pieces kept in {workDir}.");

            var outputDir = Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "Upscale");
            Directory.CreateDirectory(outputDir);
            var finalPath = Path.Combine(outputDir, $"SeedVR2_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            File.Move(joined, finalPath, true);
            AddLog($"SeedVR2: joined {pieces.Count} pieces → {final.Width}×{final.Height}, {final.Frames} frames");

            // The pieces' own files on the server's output share are this run's scratch, not results.
            foreach (var f in serverFiles) { try { File.Delete(f); } catch { /* not ours to fight over */ } }
            try { Directory.Delete(workDir, true); } catch { /* best effort */ }
            return finalPath;
        }

        private void SetChunkProgress(int index, int count, double fraction, string status)
        {
            ProcessingProgress = 5 + 90 * (index + Math.Clamp(fraction, 0, 1)) / count;
            ProcessingStatus = status;
        }

        /// <summary>The SeedVR2 graph for one piece (<see cref="SeedVr2Chunks.PatchForChunk"/>), PlaySound removed.</summary>
        private async Task<JsonElement> BuildSeedVr2ChunkWorkflowAsync(string uploadedName, double scale, long seed, string prefix)
        {
            var json = await ReadWorkflowAsync(SeedVr2UpscaleWorkflow,
                SeedVr2VideoNode, SeedVr2ResizeNode, SeedVr2SamplerNode, SeedVr2PostNode, SeedVr2CombineNode);
            var graph = JsonNode.Parse(json)!.AsObject();
            SeedVr2Chunks.PatchForChunk(graph, SeedVr2VideoNode, SeedVr2ResizeNode, SeedVr2ResizeMultiplierInput,
                SeedVr2SamplerNode, SeedVr2PostNode, SeedVr2CombineNode, uploadedName, scale, seed, prefix);
            graph.Remove(SeedVr2PlaySoundNode);
            if (_seedVr2SageUnavailable && graph[SeedVr2SageNode]?["inputs"] is JsonObject sage)
                sage["sage_attention"] = "disabled";
            return JsonSerializer.SerializeToElement(graph);
        }

        /// <summary>
        /// Runs a SeedVR2 graph. The export patches in Sage attention, which fails outright on a server without the
        /// <c>sageattention</c> module; that one failure is caught, remembered, and the graph rebuilt without it and
        /// sent again — so the tab works on either kind of server with nothing to set.
        /// </summary>
        private async Task<string> ExecuteSeedVr2Async(JsonElement workflow, Func<Task<JsonElement>> rebuild,
            IProgress<FlipPix.ComfyUI.Models.ProgressMessage> progress, CancellationToken token)
        {
            try
            {
                return await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            }
            catch (FlipPix.ComfyUI.Exceptions.ComfyUIExecutionException ex)
                when (!_seedVr2SageUnavailable && ex.Message.Contains("sageattention", StringComparison.OrdinalIgnoreCase))
            {
                _seedVr2SageUnavailable = true;
                AddLog("SeedVR2: the server has no sageattention module; running again with plain attention.");
                return await _comfyUIService.ExecuteWorkflowAsync(await rebuild(), progress, token);
            }
        }

        /// <summary>The server's free system RAM from /system_stats, or 0 when it does not say.</summary>
        private async Task<long> GetServerFreeRamAsync(CancellationToken token)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var doc = JsonDocument.Parse(await http.GetStringAsync($"{GetComfyUIBaseUrl().TrimEnd('/')}/system_stats", token));
                return doc.RootElement.GetProperty("system").GetProperty("ram_free").GetInt64();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddLog($"SeedVR2: could not read the server's free RAM ({ex.Message}); assuming {SeedVr2Chunks.FallbackFreeRamBytes / 1073741824} GB.");
                return 0;
            }
        }

        /// <summary>Size, exact frame count (decoded, not estimated), frame rate and audio codec, via ffprobe.</summary>
        private static VideoProbe? ProbeVideo(string ffprobe, string path)
        {
            var video = RunProbe(ffprobe, $"-v error -count_frames -select_streams v:0 -show_entries stream=width,height,r_frame_rate,nb_read_frames -of csv=p=0 \"{path}\"");
            var parts = video?.Split(',', StringSplitOptions.TrimEntries);
            if (parts == null || parts.Length < 4) return null;
            var rate = parts[2].Split('/');
            if (!int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) || !int.TryParse(parts[3], out var frames)
                || !int.TryParse(rate[0], out var num)) return null;
            var den = rate.Length > 1 && int.TryParse(rate[1], out var d) && d > 0 ? d : 1;
            if (w <= 0 || h <= 0 || frames <= 0 || num <= 0) return null;
            var audio = RunProbe(ffprobe, $"-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 \"{path}\"");
            return new VideoProbe(w, h, num, den, frames, string.IsNullOrWhiteSpace(audio) ? null : audio);
        }

        private static string? RunProbe(string ffprobe, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(ffprobe, args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                });
                if (p == null) return null;
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } return null; }
                p.WaitForExit();
                _ = stderr.Result;
                return p.ExitCode == 0 ? stdout.Result.Trim().Split('\n')[0].Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Names the work folder: the same file at the same scale resumes; an edited file starts over.</summary>
        private static string ChunkKey(string path, double scale)
        {
            var info = new FileInfo(path);
            var id = $"{Path.GetFullPath(path).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{scale.ToString("0.###", CultureInfo.InvariantCulture)}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant();
        }

        private static void SaveChunkJob(string workDir, ChunkJob job) =>
            File.WriteAllText(Path.Combine(workDir, "plan.json"), JsonSerializer.Serialize(job));

        private static ChunkJob? LoadChunkJob(string workDir)
        {
            try
            {
                var file = Path.Combine(workDir, "plan.json");
                if (!File.Exists(file)) return null;
                var job = JsonSerializer.Deserialize<ChunkJob>(File.ReadAllText(file));
                return job?.Plan?.Chunks is { Count: > 1 } ? job : null;
            }
            catch
            {
                return null;
            }
        }

        private static void PruneChunkWork(string keep)
        {
            try
            {
                if (!Directory.Exists(ChunkWorkRoot)) return;
                foreach (var dir in Directory.EnumerateDirectories(ChunkWorkRoot))
                    if (!string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase)
                        && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > ChunkWorkMaxAge)
                        Directory.Delete(dir, true);
            }
            catch { /* housekeeping only */ }
        }
    }
}

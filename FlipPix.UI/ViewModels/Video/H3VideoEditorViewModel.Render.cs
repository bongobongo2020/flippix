using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    public partial class H3VideoEditorViewModel
    {
        private CancellationTokenSource? _renderCts;

        // ── Generate Clips ───────────────────────────────────────────────────────────────────────

        private async Task GenerateClipsAsync()
        {
            if (!CanGenerate) return;

            // Failed clips are retried along with pending ones.
            var pendingClips = TimelineClips
                .Where(c => c.State == H3ClipState.Pending || c.State == H3ClipState.Failed)
                .ToList();
            IsLogOpen = true;
            if (pendingClips.Count == 0)
            {
                StatusText = "Nothing to generate — all clips are rendered";
                AddLog("All clips are already rendered. Use ↻ on a clip to regenerate it.");
                return;
            }

            IsGenerating = true;
            Progress = 0;
            _renderCts = new CancellationTokenSource();
            int total = pendingClips.Count;
            int completed = 0;
            int failed = 0;
            AddLog($"▶ Generating {total} clip(s)...");

            try
            {
                foreach (var clip in pendingClips)
                    clip.State = H3ClipState.Queued;

                // Acquire workflow lease (waits if another tab is using ComfyUI)
                var busyWith = _workflowCoordinator.CurrentWorkflowType;
                if (busyWith != null)
                {
                    StatusText = $"Waiting for {busyWith} to finish...";
                    AddLog($"Waiting for another job ({busyWith}) to finish before starting...");
                }
                else
                {
                    StatusText = "Starting...";
                }
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", _renderCts.Token);
                await EnsureChainNodesAsync(_renderCts.Token);

                for (int i = 0; i < pendingClips.Count; i++)
                {
                    var clip = pendingClips[i];
                    if (_renderCts.Token.IsCancellationRequested) break;

                    clip.State = H3ClipState.Rendering;
                    StatusText = $"Generating clip {clip.DisplayIndex} ({i + 1}/{total})...";
                    Progress = (double)i / total * 100;

                    try
                    {
                        await GenerateSingleClipAsync(clip, _renderCts.Token, i, total);
                        clip.State = H3ClipState.Rendered;
                        completed++;
                        AddLog($"Clip {clip.DisplayIndex} rendered successfully");
                        AutoSaveTimeline(); // Save progress after each successful clip
                    }
                    catch (OperationCanceledException) when (_renderCts.Token.IsCancellationRequested)
                    {
                        // Only a real user cancel stops the batch. An internal HTTP/WebSocket timeout
                        // also surfaces as OperationCanceledException and falls through to the
                        // failure branch below, so the remaining clips still render.
                        clip.State = H3ClipState.Pending;
                        AddLog($"Clip {clip.DisplayIndex} cancelled");
                        AutoSaveTimeline(); // Save state on cancel
                        break;
                    }
                    catch (Exception ex)
                    {
                        clip.State = H3ClipState.Failed;
                        failed++;
                        AddLog($"Clip {clip.DisplayIndex} failed: {ex.Message}");
                        AutoSaveTimeline(); // Save state on failure
                    }
                }

                Progress = 100;
                StatusText = failed > 0
                    ? $"Completed {completed}/{total} clips ({failed} failed — see log)"
                    : $"Completed {completed}/{total} clips";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
                AddLog("Generation cancelled");
            }
            catch (Exception ex)
            {
                StatusText = "Generation failed — see log";
                AddLog($"ERROR: {ex.Message}");
            }
            finally
            {
                // Anything still queued (cancelled/aborted before it started) goes back to pending.
                foreach (var clip in pendingClips.Where(c => c.State is H3ClipState.Queued or H3ClipState.Rendering))
                    clip.State = H3ClipState.Pending;

                IsGenerating = false;
                _renderCts?.Dispose();
                _renderCts = null;
                GenerateClipsCommand.NotifyCanExecuteChanged();
                JoinClipsCommand.NotifyCanExecuteChanged();
            }
        }

        private async Task GenerateSingleClipAsync(H3TimelineClip clip, CancellationToken ct, int batchIndex = 0, int batchTotal = 1)
        {
            // Upload reference images first
            var uploadedImages = new string?[4];
            for (int i = 0; i < Math.Min(4, ReferenceSlots.Count); i++)
            {
                var slot = ReferenceSlots[i];
                if (!slot.HasImages) continue;

                var imagePath = slot.Images.FirstOrDefault()?.Path;
                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) continue;

                try
                {
                    AddLog($"Uploading Picture {i + 1}: {Path.GetFileName(imagePath)}...");
                    uploadedImages[i] = await _comfyUIService.UploadImageAsync(imagePath, ct);
                    AddLog($"Uploaded Picture {i + 1}: {uploadedImages[i]}");
                }
                catch (Exception ex)
                {
                    AddLog($"WARNING: Failed to upload Picture {i + 1}: {ex.Message}");
                }
            }

            // Build the workflow JSON based on h3_obvpm_timeline workflow
            AddLog($"Building workflow for clip {clip.DisplayIndex}...");
            var (workflow, saveNodeId, thumbnailNodeId, contextFolder) = await BuildClipWorkflowWithSaveNodeAsync(clip, uploadedImages);
            clip.ContextLatentFolder = string.Empty; // the old take's latent no longer belongs to this clip

            // Log workflow summary
            AddLog($"Workflow has {workflow.Count} nodes, {workflow.ToJsonString().Length} chars");

            // Submit to ComfyUI and wait for completion
            AddLog($"Submitting clip {clip.DisplayIndex} to ComfyUI...");
            try
            {
                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        StatusText = batchTotal > 1
                            ? $"Generating clip {clip.DisplayIndex} ({batchIndex + 1}/{batchTotal}): {pct:F0}%"
                            : $"Generating clip {clip.DisplayIndex}: {pct:F0}%";
                        Progress = (batchIndex + pct / 100) / batchTotal * 100;
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, ct);

                if (string.IsNullOrEmpty(promptId))
                {
                    throw new Exception("ComfyUI returned empty prompt ID - workflow may have errors");
                }

                AddLog($"Clip {clip.DisplayIndex} completed with prompt ID: {promptId}");
                clip.ContextLatentFolder = contextFolder ?? string.Empty;

                // Get the output video path from the save node
                var outputPath = await ResolveClipOutputPathAsync(promptId, saveNodeId, clip.DisplayIndex, ct);
                if (!string.IsNullOrEmpty(outputPath))
                {
                    clip.OutputPath = outputPath;
                    AddLog($"Clip {clip.DisplayIndex} output: {Path.GetFileName(outputPath)}");
                    await CopyToLocalVideosAsync(outputPath);
                }

                // Get the thumbnail from the SaveImage node (generated within ComfyUI, very fast)
                var thumbnailPath = await ResolveThumbnailPathAsync(promptId, thumbnailNodeId, clip.DisplayIndex, ct);
                if (!string.IsNullOrEmpty(thumbnailPath))
                {
                    clip.ThumbnailPath = thumbnailPath;
                    AddLog($"Clip {clip.DisplayIndex} thumbnail: {Path.GetFileName(thumbnailPath)}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR generating clip {clip.DisplayIndex}: {ex.Message}");
                if (ex.Message.Contains("context_latent", StringComparison.OrdinalIgnoreCase))
                    AddLog("  The previous clip was rendered at another size, and a chain can't change resolution. " +
                           "Restore the aspect ratio/megapixels or regenerate from the first clip.");
                if (ex.InnerException != null)
                {
                    AddLog($"  Inner error: {ex.InnerException.Message}");
                }
                throw;
            }
        }

        /// <summary>
        /// Copies a finished video to Videos\flippix-vids, prefixed with the project folder so
        /// every project's clip_00001.mp4 doesn't collide.
        /// </summary>
        private async Task CopyToLocalVideosAsync(string videoPath)
        {
            if (!File.Exists(videoPath)) return;
            var name = string.IsNullOrEmpty(ProjectFolder)
                ? Path.GetFileName(videoPath)
                : $"{ProjectFolder}_{Path.GetFileName(videoPath)}";
            await LocalCopyService.CopyVideoAsync(videoPath, name);
            AddLog($"Copied to {Path.Combine(LocalCopyService.VideoDestination, name)}");
        }

        private async Task<string?> ResolveClipOutputPathAsync(string promptId, string saveNodeId, int clipIndex, CancellationToken ct)
        {
            try
            {
                // Try to get outputs by node from ComfyUI
                var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
                if (byNode.TryGetValue(saveNodeId, out var outputs) && outputs.Count > 0)
                {
                    var videoFile = outputs[0];
                    return await ResolveVideoToLocalAsync(videoFile);
                }

                // Fallback: look for video file on disk based on naming pattern
                var expectedPrefix = $"video/{ProjectFolder}/clip_{clipIndex:D5}";
                return FindVideoOnDisk(expectedPrefix);
            }
            catch (Exception ex)
            {
                AddLog($"Warning: Could not resolve output path: {ex.Message}");
                return null;
            }
        }

        private async Task<string?> ResolveVideoToLocalAsync(string videoFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    // Try local output folder first
                    var outputFolder = settings.OutputFolderPath;
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var localPath = Path.Combine(outputFolder, videoFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath))
                        {
                            await WaitForFileStableAsync(localPath);
                            return localPath;
                        }
                    }

                    // Try remote folder as fallback
                    var remoteFolder = settings.RemoteOutputFolderPath;
                    if (!string.IsNullOrEmpty(remoteFolder))
                    {
                        var remotePath = Path.Combine(remoteFolder, videoFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(remotePath))
                        {
                            await WaitForFileStableAsync(remotePath);
                            return remotePath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Warning: ResolveVideoToLocal failed: {ex.Message}");
            }
            return null;
        }

        private string? FindVideoOnDisk(string prefix, bool verbose = false)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;

                var folders = new[] { settings.OutputFolderPath, settings.RemoteOutputFolderPath }
                    .Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f))
                    .ToList();

                if (verbose)
                    AddLog($"Searching for '{prefix}' in {folders.Count} folder(s)...");

                foreach (var folder in folders)
                {
                    var searchPattern = prefix.Replace("/", Path.DirectorySeparatorChar.ToString());
                    var baseDir = Path.Combine(folder, Path.GetDirectoryName(searchPattern) ?? "");

                    if (!Directory.Exists(baseDir))
                    {
                        if (verbose)
                            AddLog($"  Directory not found: {baseDir}");
                        continue;
                    }

                    var fileName = Path.GetFileName(searchPattern);
                    var files = Directory.GetFiles(baseDir, $"{fileName}*")
                        .Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .ToList();

                    if (verbose)
                        AddLog($"  Found {files.Count} matching file(s) in: {baseDir}");

                    if (files.Count > 0)
                        return files[0];
                }

                // Also check the direct ComfyUI output folder without subdirectory
                foreach (var folder in folders)
                {
                    var videoDir = Path.Combine(folder, "video");
                    if (Directory.Exists(videoDir))
                    {
                        // List all subdirectories to help debug
                        if (verbose)
                        {
                            var subdirs = Directory.GetDirectories(videoDir).Select(Path.GetFileName).ToList();
                            AddLog($"  Subdirs in {videoDir}: [{string.Join(", ", subdirs.Take(5))}{(subdirs.Count > 5 ? "..." : "")}]");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Warning: FindVideoOnDisk failed: {ex.Message}");
            }
            return null;
        }

        private async Task WaitForFileStableAsync(string filePath, int maxWaitMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastSize = -1;
            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                try
                {
                    var fi = new FileInfo(filePath);
                    if (fi.Exists && fi.Length > 0 && fi.Length == lastSize)
                        return;
                    lastSize = fi.Length;
                }
                catch { }
                await Task.Delay(200);
            }
        }

        private async Task<string?> ResolveThumbnailPathAsync(string promptId, string thumbnailNodeId, int clipIndex, CancellationToken ct)
        {
            try
            {
                // Try to get thumbnail output from the SaveImage node we added
                var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
                if (byNode.TryGetValue(thumbnailNodeId, out var outputs) && outputs.Count > 0)
                {
                    var imageFile = outputs[0];
                    return await ResolveImageToLocalAsync(imageFile);
                }

                // Fallback: look for thumbnail on disk
                var expectedPrefix = $"video/{ProjectFolder}/clip_{clipIndex:D5}_thumb";
                return FindImageOnDisk(expectedPrefix);
            }
            catch (Exception ex)
            {
                AddLog($"Warning: Could not resolve thumbnail: {ex.Message}");
                return null;
            }
        }

        private string? FindImageOnDisk(string prefix)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;

                var folders = new[] { settings.OutputFolderPath, settings.RemoteOutputFolderPath }
                    .Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f));

                foreach (var folder in folders)
                {
                    var searchPattern = prefix.Replace("/", Path.DirectorySeparatorChar.ToString());
                    var baseDir = Path.Combine(folder, Path.GetDirectoryName(searchPattern) ?? "");
                    if (!Directory.Exists(baseDir)) continue;

                    var fileName = Path.GetFileName(searchPattern);
                    var files = Directory.GetFiles(baseDir, $"{fileName}*")
                        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .ToList();

                    if (files.Count > 0)
                        return files[0];
                }
            }
            catch { }
            return null;
        }

        // ── The clip graph ─────────────────────────────────────────────────────────────────────────
        //
        // One graph for every clip (workflow/video/h3-minimax/h3-editor-chain.json): Ref2VA conditioning →
        // H3 Motion Context → sampler on the sigma-shifted model, with the preset's model and turbo LoRA.
        // Clip 2 onward pins the last 22 frames (and the last 0.75 s of sound) of the clip before it as
        // never-denoised rows, so it continues that take instead of starting a new one; the pinned head is
        // cut afterwards, picture and sound together, so the clips butt-join with no seam. This is the
        // pack's README wiring and the one H3 Express's 🔗 chain already uses.
        //
        // With no reference pictures the same graph runs as text-to-video (every ref input removed).

        private const string ChainWorkflowPath = "workflow/video/h3-minimax/h3-editor-chain.json";
        private const string ChainRoot = "h3_editor_ctx";

        /// <summary>The pack's tested window: 22 frames = 0.92 s, a whole number of latent steps.</summary>
        private const int ChainContextFrames = 22;
        private const int ChainFrameRate = 24;

        private static readonly string[] PictureNodeIds = { "img1", "img2", "img3", "img4" };

        /// <summary>Whether the server has the Motion Context pack; null = not asked (or could not be).</summary>
        private bool? _chainNodesOk;

        private const string ThumbnailExtractNodeId = "h3ve_thumb_extract";
        private const string ThumbnailSaveNodeId = "h3ve_thumb_save";

        /// <summary>Asks the server once per generate run whether chaining can work at all.</summary>
        private async Task EnsureChainNodesAsync(CancellationToken ct)
        {
            _chainNodesOk = await _comfyUIService.HttpClient.NodeClassExistsAsync("MiniMaxH3MotionContext", ct);
            if (_chainNodesOk == false)
                AddLog("⚠ The ComfyUI server has no MiniMaxH3MotionContext node (ComfyUI-H3-Motion-Context). Clips " +
                       "render on their own, so they will not flow into each other until it is installed.");
        }

        private bool ChainAvailable => ChainClips && _chainNodesOk != false;

        /// <summary>The clip this one continues from, or null when it starts a chain.</summary>
        private H3TimelineClip? ChainPredecessor(H3TimelineClip clip)
        {
            if (!ChainAvailable || !clip.UseMotionContext) return null;
            var idx = TimelineClips.IndexOf(clip);
            if (idx <= 0) return null;
            var prev = TimelineClips[idx - 1];
            return prev.IsRendered && !string.IsNullOrEmpty(prev.ContextLatentFolder) ? prev : null;
        }

        /// <summary>The length to render so the clip is still <paramref name="seconds"/> long once the
        /// pinned head is cut — on the model's 17k+5 frame grid, nearest step.</summary>
        private static double ChainedSeconds(double seconds)
        {
            var delivered = (int)Math.Round(seconds * ChainFrameRate);
            var steps = Math.Max(2, (int)Math.Round((delivered + ChainContextFrames - 5) / 17.0));
            return (5 + 17 * steps) / (double)ChainFrameRate;
        }

        private string ChainFolderFor(H3TimelineClip clip) =>
            $"{ChainRoot}/{System.Text.RegularExpressions.Regex.Replace(ProjectFolder, "[^A-Za-z0-9_-]", "_")}/{clip.Id}";

        private async Task<(JsonObject workflow, string saveNodeId, string thumbnailNodeId, string? contextFolder)>
            BuildClipWorkflowWithSaveNodeAsync(H3TimelineClip clip, string?[] uploadedImages)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ChainWorkflowPath);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow file not found: {ChainWorkflowPath}");

            var workflow = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(workflowPath))
                           ?? throw new Exception("Failed to parse workflow JSON");

            ApplyEngine(workflow);
            ApplyClipInputs(workflow, clip);
            ApplyReferences(workflow, uploadedImages);
            var contextFolder = ApplyChain(workflow, clip);
            AddThumbnailNodes(workflow, clip, workflow.ContainsKey("trim") ? "trim" : "decode");

            return (workflow, "save", ThumbnailSaveNodeId, contextFolder);
        }

        /// <summary>Model, turbo LoRA, sigma shift, steps, sampler, scheduler — all from the preset.</summary>
        private void ApplyEngine(JsonObject workflow)
        {
            Input(workflow, "unet")["unet_name"] = SelectedModel;

            var useLora = SelectedTurboLoader != "off" && !string.IsNullOrWhiteSpace(TurboLora);
            if (useLora)
            {
                Input(workflow, "lora")["lora_name"] = TurboLora.Replace('\\', '/');
                Input(workflow, "lora")["strength_model"] = TurboStrength;
            }
            else
            {
                workflow.Remove("lora");
                Input(workflow, "shift")["model"] = new JsonArray("unet", 0);
            }

            Input(workflow, "shift")["shift_video"] = (double)ShiftVideo;
            Input(workflow, "shift")["shift_audio"] = (double)ShiftAudio;
            Input(workflow, "sched")["steps"] = Steps;
            Input(workflow, "sched")["scheduler"] = SelectedScheduler;
            Input(workflow, "sampler")["sampler_name"] = SelectedSampler;

            var minimum = H3EditorPreset.MinimumSteps(SelectedModel, useLora ? SelectedTurboLoader : "off");
            AddLog($"Engine: {Path.GetFileName(SelectedModel)}" +
                   (useLora ? $" + {Path.GetFileName(TurboLora)} @ {TurboStrength:0.##}" : "") +
                   $", {Steps} steps {SelectedSampler}/{SelectedScheduler}, shift {ShiftVideo}/{ShiftAudio}");
            if (Steps < minimum)
                AddLog($"⚠ {Steps} steps is below what this model needs (≥{minimum}) — expect smeared, incoherent " +
                       "motion. Pick a turbo preset for few steps.");
        }

        private void ApplyClipInputs(JsonObject workflow, H3TimelineClip clip)
        {
            Input(workflow, "prompt")["value"] = clip.Prompt;
            Input(workflow, "res")["aspect_ratio"] = SelectedAspectRatio;
            Input(workflow, "res")["megapixels"] = Megapixels;
            Input(workflow, "seconds")["value"] = clip.DurationSeconds;
            Input(workflow, "noise")["noise_seed"] = Random.Shared.NextInt64(0, 999_999_999_999_999L);
            Input(workflow, "save")["filename_prefix"] = $"video/{ProjectFolder}/clip_{clip.DisplayIndex:D5}";
        }

        /// <summary>Up to four reference pictures, in slot order. Unused slots are removed from the graph so
        /// ComfyUI never tries to load the template's placeholder.</summary>
        private void ApplyReferences(JsonObject workflow, string?[] uploadedImages)
        {
            var refInputs = Input(workflow, "ref");
            for (int i = 0; i < PictureNodeIds.Length; i++)
                refInputs.Remove($"ref_images.ref_image_{i}");

            // <Picture N> in the prompt is the Nth image the node receives, so a gap would renumber every
            // picture after it.
            var lastFilled = Array.FindLastIndex(uploadedImages, u => !string.IsNullOrEmpty(u));
            if (lastFilled >= 0 && uploadedImages.Take(lastFilled).Any(string.IsNullOrEmpty))
                AddLog("⚠ A Picture slot before the last filled one is empty — the pictures after it are renumbered " +
                       "and no longer match the <Picture N> tags in the prompt. Fill the slots in order.");

            var images = uploadedImages.Take(PictureNodeIds.Length).Where(u => !string.IsNullOrEmpty(u)).ToList();
            for (int i = 0; i < PictureNodeIds.Length; i++)
            {
                if (i < images.Count)
                {
                    Input(workflow, PictureNodeIds[i])["image"] = images[i];
                    refInputs[$"ref_images.ref_image_{i}"] = new JsonArray(PictureNodeIds[i], 0);
                }
                else
                {
                    workflow.Remove(PictureNodeIds[i]);
                }
            }
            AddLog(images.Count > 0 ? $"References: {images.Count} picture(s)" : "No reference pictures — text-to-video");
        }

        /// <summary>
        /// Wires the motion-context chain. Returns the folder this clip's latent is saved to (so the next clip
        /// can continue from it), or null when nothing is saved.
        /// </summary>
        private string? ApplyChain(JsonObject workflow, H3TimelineClip clip)
        {
            if (!ChainAvailable)
            {
                // No pack (or chaining off): a plain Ref2VA render. Without the pack the trim node is missing
                // too, so the decode feeds the video directly.
                workflow.Remove("ctx_load");
                workflow.Remove("ctx");
                workflow.Remove("ctx_save");
                Input(workflow, "guider")["conditioning"] = new JsonArray("ref", 0);
                if (_chainNodesOk == false)
                {
                    workflow.Remove("trim");
                    Input(workflow, "video")["images"] = new JsonArray("decode", 0);
                    Input(workflow, "video")["audio"] = new JsonArray("decode_audio", 0);
                }
                else
                {
                    Input(workflow, "trim")["trim_frames"] = 0;
                }
                return null;
            }

            var folder = ChainFolderFor(clip);
            Input(workflow, "ctx_save")["filename_prefix"] = $"{folder}/clip";
            Input(workflow, "ctx_save")["clip_index"] = 1;

            var prev = ChainPredecessor(clip);
            if (prev != null)
            {
                Input(workflow, "ctx_load")["latent_path"] = prev.ContextLatentFolder;
                Input(workflow, "ctx_load")["clip_index"] = 1;
                var rendered = ChainedSeconds(clip.DurationSeconds);
                Input(workflow, "seconds")["value"] = rendered;
                AddLog($"🔗 Clip {clip.DisplayIndex} continues clip {prev.DisplayIndex}: {ChainContextFrames} frames pinned, " +
                       $"rendered {rendered:0.##}s → ≈{clip.DurationSeconds:0.#}s delivered after the trim");
            }
            else
            {
                // Nothing to continue from: the clip seeds a chain.
                workflow.Remove("ctx_load");
                workflow.Remove("ctx");
                Input(workflow, "guider")["conditioning"] = new JsonArray("ref", 0);
                Input(workflow, "trim")["trim_frames"] = 0;
                var idx = TimelineClips.IndexOf(clip);
                AddLog(idx > 0 && clip.UseMotionContext
                    ? $"🔗 Clip {clip.DisplayIndex} starts a new chain — clip {idx} has no chained render to continue from."
                    : $"🔗 Clip {clip.DisplayIndex} starts the chain.");
            }

            var next = TimelineClips.IndexOf(clip) + 1;
            if (next > 0 && next < TimelineClips.Count && TimelineClips[next].IsRendered && TimelineClips[next].UseMotionContext)
                AddLog($"⚠ Clip {next + 1} was rendered continuing the take this replaces — regenerate it too or the join won't match.");

            return folder;
        }

        private static JsonObject Input(JsonObject workflow, string nodeId) =>
            workflow[nodeId]?["inputs"] as JsonObject
            ?? throw new Exception($"Workflow node '{nodeId}' is missing — {ChainWorkflowPath} no longer matches the editor.");

        /// <summary>
        /// Adds nodes to extract and save the first frame as a thumbnail.
        /// This happens within ComfyUI so it's fast and doesn't require FFmpeg.
        /// </summary>
        private void AddThumbnailNodes(JsonObject workflow, H3TimelineClip clip, string imagesSourceNode)
        {
            workflow[ThumbnailExtractNodeId] = new JsonObject
            {
                ["inputs"] = new JsonObject
                {
                    ["image"] = new JsonArray { imagesSourceNode, 0 },
                    ["batch_index"] = 0
                },
                ["class_type"] = "ImageFromBatch",
                ["_meta"] = new JsonObject { ["title"] = "Extract Thumbnail Frame" }
            };

            workflow[ThumbnailSaveNodeId] = new JsonObject
            {
                ["inputs"] = new JsonObject
                {
                    ["images"] = new JsonArray { ThumbnailExtractNodeId, 0 },
                    ["filename_prefix"] = $"video/{ProjectFolder}/clip_{clip.DisplayIndex:D5}_thumb"
                },
                ["class_type"] = "SaveImage",
                ["_meta"] = new JsonObject { ["title"] = "Save Thumbnail" }
            };
        }

        private static (int width, int height) GetResolution(string aspectRatio, double megapixels)
        {
            double ratio = aspectRatio switch
            {
                "16:9 (Widescreen)" => 16.0 / 9.0,
                "9:16 (Portrait)" => 9.0 / 16.0,
                "4:3 (Standard)" => 4.0 / 3.0,
                "3:4 (Portrait Standard)" => 3.0 / 4.0,
                "1:1 (Square)" => 1.0,
                "21:9 (Ultrawide)" => 21.0 / 9.0,
                _ => 16.0 / 9.0
            };

            // Calculate dimensions from megapixels
            double totalPixels = megapixels * 1_000_000;
            int height = (int)Math.Sqrt(totalPixels / ratio);
            int width = (int)(height * ratio);

            // Round to nearest 64 for compatibility
            width = (width / 64) * 64;
            height = (height / 64) * 64;

            return (width, height);
        }

        // ── Join Clips ───────────────────────────────────────────────────────────────────────────

        /// <param name="clipPaths">Files to join in order (e.g. the upscaled clips); null joins the
        /// timeline's rendered clips.</param>
        private async Task JoinClipsAsync(IReadOnlyList<string>? clipPaths = null)
        {
            if (!CanJoin) return;

            IsJoining = true;
            StatusText = "Joining clips...";

            try
            {
                var paths = (clipPaths ?? TimelineClips
                        .Where(c => c.IsRendered && !string.IsNullOrEmpty(c.OutputPath))
                        .OrderBy(c => c.Index)
                        .Select(c => c.OutputPath!)
                        .ToList())
                    .Where(File.Exists)
                    .ToList();

                if (paths.Count == 0)
                {
                    AddLog("No rendered clips to join");
                    return;
                }

                AddLog($"Joining {paths.Count} clips with FFmpeg...");

                // Determine output path
                // OutputFolderPath is "" (not null) when ComfyUI is remote, and a ?? fallback would then
                // build a relative "video\<project>" under the app's working directory. Without a local
                // output folder, write the joined video next to the clips (the project folder on the share).
                var settings = _settingsService.Settings;
                var projectDir = !string.IsNullOrEmpty(settings?.OutputFolderPath)
                    ? Path.Combine(settings.OutputFolderPath, "video", ProjectFolder)
                    : TimelineClips.Select(c => c.OutputPath).Where(p => !string.IsNullOrEmpty(p))
                        .Select(Path.GetDirectoryName).FirstOrDefault() ?? Path.GetDirectoryName(paths[0]) ?? ".";
                Directory.CreateDirectory(projectDir);
                var outputPath = Path.Combine(projectDir, $"joined_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                // Use FFmpeg to concatenate the clips
                await Task.Run(() => JoinClipsWithFFmpeg(paths, outputPath));

                if (File.Exists(outputPath))
                {
                    JoinedVideoPath = outputPath;
                    PreviewVideoPath = outputPath;
                    await CopyToLocalVideosAsync(outputPath);
                    AddLog($"Join complete: {Path.GetFileName(outputPath)}");
                    StatusText = "Join complete";
                    UpscaleCommand.NotifyCanExecuteChanged();
                }
                else
                {
                    throw new Exception("FFmpeg did not produce output file");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Join failed: {ex.Message}");
                StatusText = "Join failed";
            }
            finally
            {
                IsJoining = false;
                JoinClipsCommand.NotifyCanExecuteChanged();
                UpscaleCommand.NotifyCanExecuteChanged();
            }
        }

        private void JoinClipsWithFFmpeg(List<string> clipPaths, string outputPath)
        {
            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                throw new InvalidOperationException("FFmpeg not found. Please install FFmpeg to join clips.");
            }

            // Create concat list file
            var listFile = Path.Combine(Path.GetTempPath(), $"h3ve_concat_{Guid.NewGuid():N}.txt");
            try
            {
                using (var writer = new StreamWriter(listFile))
                {
                    foreach (var clipPath in clipPaths)
                    {
                        // FFmpeg concat demuxer requires forward slashes and escaped quotes
                        var escaped = clipPath.Replace("\\", "/").Replace("'", @"'\''");
                        writer.WriteLine($"file '{escaped}'");
                    }
                }

                AddLog($"Concatenating {clipPaths.Count} clips...");

                // Build FFmpeg arguments
                var args = Crossfade && CrossfadeFrames > 0 && !ChainAvailable
                    ? BuildCrossfadeArgs(clipPaths, outputPath, listFile)
                    : $"-y -f concat -safe 0 -i \"{listFile}\" -c:v libx264 -crf {Crf} -c:a aac \"{outputPath}\"";

                RunFFmpeg(ffmpegPath, args);
            }
            finally
            {
                try { File.Delete(listFile); } catch { /* temp file cleanup */ }
            }
        }

        private string BuildCrossfadeArgs(List<string> clipPaths, string outputPath, string listFile)
        {
            // For crossfade, we need a more complex filter
            // Simple stream copy with crossfade is complex; for now use basic concat
            // Advanced crossfade would require xfade filter with re-encoding
            if (clipPaths.Count <= 1)
                return $"-y -f concat -safe 0 -i \"{listFile}\" -c:v libx264 -crf {Crf} -c:a aac \"{outputPath}\"";

            // Basic crossfade using xfade filter (re-encodes but produces smooth transitions)
            var sb = new System.Text.StringBuilder();
            sb.Append("-y ");
            foreach (var path in clipPaths)
                sb.Append($"-i \"{path}\" ");

            // Build xfade filter chain
            var filterParts = new List<string>();
            var lastOutput = "[0:v]";
            for (int i = 1; i < clipPaths.Count; i++)
            {
                var nextInput = $"[{i}:v]";
                var output = i == clipPaths.Count - 1 ? "[outv]" : $"[v{i}]";
                var crossfadeDuration = CrossfadeFrames / 24.0; // Assuming 24fps
                filterParts.Add($"{lastOutput}{nextInput}xfade=transition=fade:duration={crossfadeDuration:F2}:offset={5 - crossfadeDuration:F2}{output}");
                lastOutput = output;
            }

            var filterComplex = string.Join(";", filterParts);
            sb.Append($"-filter_complex \"{filterComplex}\" -map \"[outv]\" ");

            // Audio: use amerge or just take first stream
            sb.Append($"-c:v libx264 -crf {Crf} -c:a aac \"{outputPath}\"");

            return sb.ToString();
        }

        // ── Upscale ──────────────────────────────────────────────────────────────────────────────
        // Uses MinimaxH3LatentUpscaler3D for H3-native latent upscaling of the joined video.
        // This encodes the video to H3 latent space, upscales, then decodes back.

        private async Task UpscaleAsync()
        {
            if (!CanUpscale || string.IsNullOrEmpty(JoinedVideoPath)) return;

            IsUpscaling = true;

            // Ensure project folder exists in output directories before upscaling
            await CreateProjectFolderAsync();
            StatusText = "Upscaling joined video with H3 Latent Upscaler...";

            try
            {
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", CancellationToken.None);

                var outputPath = await UpscaleVideoFileAsync(JoinedVideoPath, null, "joined video", CancellationToken.None);
                if (!string.IsNullOrEmpty(outputPath))
                {
                    PreviewVideoPath = outputPath;
                    AddLog($"✓ H3 Latent Upscale complete!");
                    AddLog($"  Saved to: {outputPath}");
                    await CopyToLocalVideosAsync(outputPath);
                    StatusText = "Upscale complete";
                }
                else
                {
                    // Log expected location to help user find the file
                    var settings = _settingsService.Settings;
                    var expectedPath = !string.IsNullOrEmpty(settings?.OutputFolderPath)
                        ? Path.Combine(settings.OutputFolderPath, "video", ProjectFolder, "upscale_00001.mp4")
                        : $"video/{ProjectFolder}/upscale_00001.mp4";
                    AddLog($"⚠ Upscale completed but could not resolve output path.");
                    AddLog($"  Expected location: {expectedPath}");
                    StatusText = "Upscale complete (path unresolved)";
                }
                Progress = 100;
            }
            catch (Exception ex)
            {
                AddLog($"Upscale failed: {ex.Message}");
                StatusText = "Upscale failed";
            }
            finally
            {
                IsUpscaling = false;
            }
        }

        /// <summary>
        /// Uploads <paramref name="inputPath"/>, runs the H3 latent upscale + refine on it and returns
        /// the local path of the result (null if it ran but the file couldn't be found). Throws on
        /// failure. The caller holds the workflow lease.
        /// </summary>
        /// <param name="filenamePrefix">ComfyUI output prefix; null for the default "video/&lt;project&gt;/upscale".</param>
        private async Task<string?> UpscaleVideoFileAsync(string inputPath, string? filenamePrefix, string label, CancellationToken ct)
        {
            AddLog($"Uploading {label} for H3 latent upscale: {Path.GetFileName(inputPath)}");
            var uploadedName = await _comfyUIService.UploadVideoAsync(inputPath, ct);
            AddLog($"Uploaded as: {uploadedName}");

            var (workflow, saveNodeId) = BuildH3LatentUpscaleWorkflow(uploadedName, filenamePrefix);

            AddLog($"Submitting H3 Latent Upscale workflow ({UpscaleFactor}x) for {label}...");
            var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                {
                    var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                    StatusText = $"Upscaling {label}: {pct:F0}%";
                    Progress = pct;
                }
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, ct);
            if (string.IsNullOrEmpty(promptId))
                throw new Exception("ComfyUI returned empty prompt ID");

            AddLog($"Upscale of {label} complete, resolving output path...");
            return await ResolveUpscaleOutputAsync(promptId, saveNodeId, filenamePrefix);
        }

        /// <summary>
        /// Upscales every rendered clip on its own (memory stays one clip's worth, unlike upscaling the
        /// joined video) and returns the paths to join, in timeline order. A clip whose upscale fails
        /// keeps its original render so the story still joins complete.
        /// </summary>
        private async Task<List<string>> UpscaleClipsAsync()
        {
            var clips = TimelineClips
                .Where(c => c.IsRendered && !string.IsNullOrEmpty(c.OutputPath) && File.Exists(c.OutputPath))
                .OrderBy(c => c.Index)
                .ToList();
            var paths = new List<string>();
            if (clips.Count == 0) return paths;

            IsUpscaling = true;
            int upscaled = 0;
            try
            {
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", CancellationToken.None);

                for (int i = 0; i < clips.Count; i++)
                {
                    var clip = clips[i];
                    if (_storyBatchCancelled)
                    {
                        AddLog("Upscale cancelled — remaining clips keep their original render.");
                        paths.AddRange(clips.Skip(i).Select(c => c.OutputPath!));
                        break;
                    }

                    StatusText = $"Upscaling clip {i + 1}/{clips.Count}...";
                    string? result = null;
                    try
                    {
                        // Own subfolder: the on-disk fallback for an original clip searches "clip_00001*",
                        // which would otherwise also match an upscaled file beside it.
                        result = await UpscaleVideoFileAsync(
                            clip.OutputPath!, $"video/{ProjectFolder}/up/clip_{clip.DisplayIndex:D5}",
                            $"clip {clip.DisplayIndex}", CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        AddLog($"Upscale of clip {clip.DisplayIndex} failed: {ex.Message}");
                    }

                    if (!string.IsNullOrEmpty(result) && File.Exists(result))
                    {
                        paths.Add(result);
                        upscaled++;
                    }
                    else
                    {
                        AddLog($"Clip {clip.DisplayIndex}: using the original render in the join.");
                        paths.Add(clip.OutputPath!);
                    }
                }
            }
            finally
            {
                IsUpscaling = false;
            }

            AddLog($"Upscaled {upscaled}/{clips.Count} clips.");
            return paths;
        }

        /// <summary>
        /// Builds an H3 Latent Upscale + Refine workflow that:
        /// 1. Loads the joined video and encodes to H3 latent space
        /// 2. Upscales using MinimaxH3LatentUpscaler3D
        /// 3. Runs a refine/sampling pass with the H3 model to add detail (using RefineAmount as denoise)
        /// 4. Decodes back to video and saves the result
        /// Based on the h3-seed-upscale.json workflow and OBVPM H3 Upscale / Refine Engine.
        /// </summary>
        private (JsonObject workflow, string saveNodeId) BuildH3LatentUpscaleWorkflow(string uploadedVideoName, string? filenamePrefix = null)
        {
            filenamePrefix ??= $"video/{ProjectFolder}/upscale";
            AddLog($"Building H3 Latent Upscale + Refine workflow for: {uploadedVideoName}");
            AddLog($"Upscale factor: {UpscaleFactor}x, Refine amount: {RefineAmount}");

            // Calculate refine steps based on refine amount (more denoise = more steps needed)
            // With BasicScheduler's denoise parameter, only a portion of steps actually run
            int refineSteps = RefineAmount >= 0.5 ? 10 : (RefineAmount >= 0.3 ? 8 : 6);
            AddLog($"Using {refineSteps} refine steps with denoise={RefineAmount:F2}");

            var workflow = new JsonObject
            {
                // ═══════════════════════════════════════════════════════════════════════════════════
                // Model Stack: UNet + CLIP + VAEs
                // ═══════════════════════════════════════════════════════════════════════════════════

                // Load the H3 UNet model (for refine pass)
                ["unet"] = new JsonObject
                {
                    ["class_type"] = "UNETLoader",
                    ["inputs"] = new JsonObject
                    {
                        ["unet_name"] = "h3-minimax/minimax_h3_fused_refdelta_r1024_turbo8_mystic07_int8_convrot.safetensors",
                        ["weight_dtype"] = "default"
                    },
                    ["_meta"] = new JsonObject { ["title"] = "H3 UNet" }
                },

                // Load the CLIP text encoder
                ["clip"] = new JsonObject
                {
                    ["class_type"] = "CLIPLoader",
                    ["inputs"] = new JsonObject
                    {
                        ["clip_name"] = "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors",
                        ["type"] = "minimax",
                        ["device"] = "default"
                    },
                    ["_meta"] = new JsonObject { ["title"] = "H3 CLIP" }
                },

                // Load the H3 video VAE
                ["vae_video"] = new JsonObject
                {
                    ["class_type"] = "VAELoader",
                    ["inputs"] = new JsonObject
                    {
                        ["vae_name"] = "minimax_h3_video_vae_int8_convrot.safetensors"
                    },
                    ["_meta"] = new JsonObject { ["title"] = "H3 Video VAE" }
                },

                // Load the H3 audio VAE
                ["vae_audio"] = new JsonObject
                {
                    ["class_type"] = "VAELoader",
                    ["inputs"] = new JsonObject
                    {
                        ["vae_name"] = "minimax_h3_audio_vae_fp32.safetensors"
                    },
                    ["_meta"] = new JsonObject { ["title"] = "H3 Audio VAE" }
                },

                // Apply sigma shift for H3 model quality
                ["shift"] = new JsonObject
                {
                    ["class_type"] = "MiniMaxH3SigmaShift",
                    ["inputs"] = new JsonObject
                    {
                        ["model"] = new JsonArray { "unet", 0 },
                        ["shift_video"] = (double)ShiftVideo,
                        ["shift_audio"] = (double)ShiftAudio
                    },
                    ["_meta"] = new JsonObject { ["title"] = "MiniMaxH3SigmaShift" }
                },

                // ═══════════════════════════════════════════════════════════════════════════════════
                // Video Loading and Encoding
                // ═══════════════════════════════════════════════════════════════════════════════════

                // Load the input video (the joined clips)
                ["load_video"] = new JsonObject
                {
                    ["class_type"] = "VHS_LoadVideo",
                    ["inputs"] = new JsonObject
                    {
                        ["video"] = uploadedVideoName,
                        ["force_rate"] = 0, // Keep original frame rate
                        ["custom_width"] = 0,
                        ["custom_height"] = 0,
                        ["frame_load_cap"] = 0,
                        ["skip_first_frames"] = 0,
                        ["select_every_nth"] = 1,
                        ["format"] = "None"
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Load Joined Video" }
                },

                // Encode video frames to H3 video latent space
                ["vae_encode_video"] = new JsonObject
                {
                    ["class_type"] = "VAEEncode",
                    ["inputs"] = new JsonObject
                    {
                        ["pixels"] = new JsonArray { "load_video", 0 },
                        ["vae"] = new JsonArray { "vae_video", 0 }
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Encode Video to Latent" }
                },

                // Encode audio to H3 audio latent space
                ["vae_encode_audio"] = new JsonObject
                {
                    ["class_type"] = "VAEEncodeAudio",
                    ["inputs"] = new JsonObject
                    {
                        ["audio"] = new JsonArray { "load_video", 2 },
                        ["vae"] = new JsonArray { "vae_audio", 0 }
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Encode Audio to Latent" }
                },

                // ═══════════════════════════════════════════════════════════════════════════════════
                // Latent Upscale
                // ═══════════════════════════════════════════════════════════════════════════════════

                // Upscale the video latent (not audio)
                ["latent_upscale"] = new JsonObject
                {
                    ["class_type"] = "MinimaxH3LatentUpscaler3D",
                    ["inputs"] = new JsonObject
                    {
                        ["latent"] = new JsonArray { "vae_encode_video", 0 },
                        ["model_name"] = "minimax_h3_latent_upscaler_3d_bf16.safetensors",
                        ["mode"] = "scale by multiplier",
                        ["mode.scale"] = UpscaleFactor,
                        ["align"] = 32,
                        ["enable_temporal_chunking"] = true,
                        ["force_unload"] = true,
                        ["device"] = "cuda",
                        ["precision"] = "fp16"
                    },
                    ["_meta"] = new JsonObject { ["title"] = $"H3 Latent Upscale {UpscaleFactor}x" }
                },

                // Combine upscaled video latent with original audio latent
                ["concat_av"] = new JsonObject
                {
                    ["class_type"] = "LTXVConcatAVLatent",
                    ["inputs"] = new JsonObject
                    {
                        ["video_latent"] = new JsonArray { "latent_upscale", 0 },
                        ["audio_latent"] = new JsonArray { "vae_encode_audio", 0 }
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Concat AV Latent" }
                },

                // ═══════════════════════════════════════════════════════════════════════════════════
                // Refine Pass - This is what adds quality/detail to the upscaled latent
                // ═══════════════════════════════════════════════════════════════════════════════════

                // Create text conditioning for the refine pass
                // Using CLIPTextEncode for simpler, dimension-independent conditioning
                ["conditioning"] = new JsonObject
                {
                    ["class_type"] = "CLIPTextEncode",
                    ["inputs"] = new JsonObject
                    {
                        ["clip"] = new JsonArray { "clip", 0 },
                        // Generic refinement prompt - focuses on preserving existing content while adding detail
                        ["text"] = "Maintain original content. Enhance fine details, textures, and sharpness. Preserve motion, faces, and composition. High quality video upscale."
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Refine Conditioning" }
                },

                // Create guider for the refine sampler
                ["guider"] = new JsonObject
                {
                    ["class_type"] = "BasicGuider",
                    ["inputs"] = new JsonObject
                    {
                        ["model"] = new JsonArray { "shift", 0 },
                        ["conditioning"] = new JsonArray { "conditioning", 0 }
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Refine Guider" }
                },

                // BasicScheduler with denoise parameter for proper RefineAmount control
                // The denoise value determines what portion of the sigma schedule to use
                ["sigmas"] = new JsonObject
                {
                    ["class_type"] = "BasicScheduler",
                    ["inputs"] = new JsonObject
                    {
                        ["model"] = new JsonArray { "shift", 0 },
                        ["scheduler"] = "simple",
                        ["steps"] = refineSteps,
                        ["denoise"] = RefineAmount
                    },
                    ["_meta"] = new JsonObject { ["title"] = $"Refine Scheduler (denoise={RefineAmount:F2})" }
                },

                // Select the sampler for refine pass
                ["sampler_select"] = new JsonObject
                {
                    ["class_type"] = "KSamplerSelect",
                    ["inputs"] = new JsonObject
                    {
                        ["sampler_name"] = "res_multistep"
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Refine Sampler" }
                },

                // Random noise for the refine pass
                ["noise"] = new JsonObject
                {
                    ["class_type"] = "RandomNoise",
                    ["inputs"] = new JsonObject
                    {
                        ["noise_seed"] = Random.Shared.NextInt64(0, 999_999_999_999_999L)
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Refine Noise" }
                },

                // The refine sampler - this adds detail to the upscaled latent
                ["refine_sampler"] = new JsonObject
                {
                    ["class_type"] = "SamplerCustomAdvanced",
                    ["inputs"] = new JsonObject
                    {
                        ["noise"] = new JsonArray { "noise", 0 },
                        ["guider"] = new JsonArray { "guider", 0 },
                        ["sampler"] = new JsonArray { "sampler_select", 0 },
                        ["sigmas"] = new JsonArray { "sigmas", 0 },
                        ["latent_image"] = new JsonArray { "concat_av", 0 }
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Upscale Refine Pass" }
                },

                // ═══════════════════════════════════════════════════════════════════════════════════
                // Decode and Save
                // ═══════════════════════════════════════════════════════════════════════════════════

                // Decode refined video latent back to frames
                // The VAE handles extracting the video portion from the combined AV latent
                // Using slot 0 ("output") matching h3-seed-upscale.json behavior
                ["vae_decode_video"] = new JsonObject
                {
                    ["class_type"] = "VAEDecode",
                    ["inputs"] = new JsonObject
                    {
                        ["samples"] = new JsonArray { "refine_sampler", 0 }, // output from sampler
                        ["vae"] = new JsonArray { "vae_video", 0 }
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Decode Video" }
                },

                // Decode audio latent back to audio
                // The audio VAE handles extracting the audio portion from the combined AV latent
                ["vae_decode_audio"] = new JsonObject
                {
                    ["class_type"] = "VAEDecodeAudio",
                    ["inputs"] = new JsonObject
                    {
                        ["samples"] = new JsonArray { "refine_sampler", 0 }, // same output slot
                        ["vae"] = new JsonArray { "vae_audio", 0 }
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Decode Audio" }
                },

                // Output the refined upscaled video
                ["output"] = new JsonObject
                {
                    ["class_type"] = "VHS_VideoCombine",
                    ["inputs"] = new JsonObject
                    {
                        ["images"] = new JsonArray { "vae_decode_video", 0 },
                        ["audio"] = new JsonArray { "vae_decode_audio", 0 },
                        ["frame_rate"] = 24,
                        ["loop_count"] = 0,
                        ["filename_prefix"] = filenamePrefix,
                        ["format"] = "video/h264-mp4",
                        ["pix_fmt"] = "yuv420p",
                        ["crf"] = Crf,
                        ["save_metadata"] = false,
                        ["trim_to_audio"] = false,
                        ["pingpong"] = false,
                        ["save_output"] = true
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Save Upscaled + Refined Video" }
                }
            };

            return (workflow, "output");
        }

        private async Task<string?> ResolveUpscaleOutputAsync(string promptId, string saveNodeId, string? filenamePrefix = null)
        {
            var isDefaultPrefix = filenamePrefix == null;
            filenamePrefix ??= $"video/{ProjectFolder}/upscale";
            try
            {
                var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, CancellationToken.None);
                if (byNode.TryGetValue(saveNodeId, out var outputs) && outputs.Count > 0)
                {
                    AddLog($"ComfyUI returned upscale output: {outputs[0]}");
                    var resolved = await ResolveVideoToLocalAsync(outputs[0]);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        return resolved;
                    }
                    AddLog($"Could not resolve to local path, searching on disk...");
                }
                else
                {
                    AddLog($"No outputs found for node {saveNodeId}, searching on disk...");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Warning: Could not resolve upscale output from ComfyUI: {ex.Message}");
            }

            // Fallback: search for the upscale file on disk with verbose logging
            var found = FindVideoOnDisk(filenamePrefix, verbose: true);
            if (!string.IsNullOrEmpty(found))
            {
                AddLog($"Found upscale on disk: {found}");
                return found;
            }

            // A per-clip upscale must not fall back to "any recent upscale" — that would hand back
            // another clip's file.
            if (!isDefaultPrefix) return null;

            // Try searching in the root video folder for any recent upscale file
            AddLog($"Checking root video folder for upscale files...");
            var rootUpscale = FindVideoOnDisk("video/upscale", verbose: true);
            if (!string.IsNullOrEmpty(rootUpscale))
            {
                AddLog($"Found upscale in root video folder: {rootUpscale}");
                return rootUpscale;
            }

            AddLog($"Warning: Could not find upscaled video. Check ComfyUI output folder manually.");
            return null;
        }

        // ── FFmpeg Helpers ───────────────────────────────────────────────────────────────────────

        private static string? _cachedFFmpegPath;
        private static bool _ffmpegResolved;
        private static readonly object _ffmpegLock = new();

        private string? FindFFmpeg()
        {
            lock (_ffmpegLock)
            {
                if (_ffmpegResolved) return _cachedFFmpegPath;
                _cachedFFmpegPath = ResolveFFmpegPath();
                _ffmpegResolved = true;
                return _cachedFFmpegPath;
            }
        }

        private string? ResolveFFmpegPath()
        {
            var baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;

            var possiblePaths = new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(baseDir, "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"),
                // The installer puts FFmpeg at <setup root>\App\ffmpeg\bin and records the root here,
                // so a dev/publish build run from elsewhere still finds the installed copy.
                InstalledFFmpegPath(),
            };

            foreach (var path in possiblePaths)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    AddLog($"Found FFmpeg at: {path}");
                    return path;
                }
            }

            // Search for ffmpeg* directories in C:\
            try
            {
                foreach (var ffmpegDir in Directory.EnumerateDirectories(@"C:\", "ffmpeg*"))
                {
                    var binPath = Path.Combine(ffmpegDir, "bin", "ffmpeg.exe");
                    if (File.Exists(binPath)) return binPath;
                    var rootPath = Path.Combine(ffmpegDir, "ffmpeg.exe");
                    if (File.Exists(rootPath)) return rootPath;
                }
            }
            catch { /* Ignore directory access errors */ }

            // Search PATH
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var ffmpegPath = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(ffmpegPath)) return ffmpegPath;
                }
            }

            AddLog("FFmpeg not found - join clips will not work");
            return null;
        }

        /// <summary>FFmpeg from the FlipPix installer (see scripts/flippix-installer.ps1), or null.</summary>
        private static string? InstalledFFmpegPath()
        {
            try
            {
                var rootFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "setup-root.txt");
                if (!File.Exists(rootFile)) return null;
                var root = File.ReadAllText(rootFile).Trim();
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "App", "ffmpeg", "bin", "ffmpeg.exe");
            }
            catch
            {
                return null;
            }
        }

        private void RunFFmpeg(string ffmpegPath, string arguments, int timeoutMs = 600000)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start FFmpeg: {ffmpegPath}");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"FFmpeg did not finish within {timeoutMs / 1000}s and was stopped.");
            }

            process.WaitForExit(); // Ensure streams are flushed

            var stderr = stderrTask.GetAwaiter().GetResult();
            _ = stdoutTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                var reason = stderr
                    .Split('\n')
                    .Select(l => l.Trim())
                    .LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "no error output";

                AddLog($"FFmpeg error: {reason}");
                throw new InvalidOperationException($"FFmpeg failed (exit code {process.ExitCode}): {reason}");
            }
        }

        // ── Generate Prompt from Images ──────────────────────────────────────────────────────────

        private async Task GeneratePromptAsync()
        {
            if (!ReferenceSlots.Any(s => s.HasImages))
            {
                AddLog("No reference images to analyze");
                return;
            }

            StatusText = "Analyzing images...";

            try
            {
                // Collect image paths
                var imagePaths = ReferenceSlots
                    .Where(s => s.HasImages)
                    .SelectMany(s => s.Images)
                    .Select(i => i.Path)
                    .ToList();

                // Use LM Studio to generate prompt from images
                var analysisPrompt = "Describe this image in detail for video generation. " +
                    "Focus on the subject, their appearance, clothing, pose, and the setting. " +
                    "Format the output for the MiniMax H3 video model with subject_definitions and detailed_description sections.";

                var result = await _lmStudioService.AnalyzeImageAsync(imagePaths.First(), analysisPrompt);

                if (!string.IsNullOrEmpty(result))
                {
                    // Wrap in spec format
                    PromptText = $@"subject_definitions:
<Subject 1> is the person depicted in <Picture 1>

summary:
[reference generation]

retention_analysis:
<Subject 1> (appears in [Shot 1]): fully_preserved

detailed_description:
{result}

overall_soundscape:
Ambient sounds matching the scene.

non_diegetic_music:
N/A
";
                    AddLog("Generated prompt from image analysis");
                }

                StatusText = "Ready";
            }
            catch (Exception ex)
            {
                AddLog($"Prompt generation failed: {ex.Message}");
                StatusText = "Ready";
            }
        }
    }
}

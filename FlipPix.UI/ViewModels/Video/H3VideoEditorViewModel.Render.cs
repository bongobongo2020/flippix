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

            IsGenerating = true;
            StatusText = "Generating clips...";
            _renderCts = new CancellationTokenSource();

            try
            {
                var pendingClips = TimelineClips.Where(c => c.State == H3ClipState.Pending).ToList();
                int total = pendingClips.Count;
                int completed = 0;

                // Acquire workflow lease
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", _renderCts.Token);

                foreach (var clip in pendingClips)
                {
                    if (_renderCts.Token.IsCancellationRequested) break;

                    clip.State = H3ClipState.Rendering;
                    StatusText = $"Generating clip {clip.DisplayIndex}/{TimelineClips.Count}...";
                    Progress = (double)completed / total * 100;

                    try
                    {
                        await GenerateSingleClipAsync(clip, _renderCts.Token);
                        clip.State = H3ClipState.Rendered;
                        completed++;
                        AddLog($"Clip {clip.DisplayIndex} rendered successfully");
                        AutoSaveTimeline(); // Save progress after each successful clip
                    }
                    catch (OperationCanceledException)
                    {
                        clip.State = H3ClipState.Pending;
                        AddLog($"Clip {clip.DisplayIndex} cancelled");
                        AutoSaveTimeline(); // Save state on cancel
                        break;
                    }
                    catch (Exception ex)
                    {
                        clip.State = H3ClipState.Failed;
                        AddLog($"Clip {clip.DisplayIndex} failed: {ex.Message}");
                        AutoSaveTimeline(); // Save state on failure
                    }
                }

                Progress = 100;
                StatusText = $"Completed {completed}/{total} clips";
            }
            finally
            {
                IsGenerating = false;
                _renderCts?.Dispose();
                _renderCts = null;
                GenerateClipsCommand.NotifyCanExecuteChanged();
                JoinClipsCommand.NotifyCanExecuteChanged();
            }
        }

        private async Task GenerateSingleClipAsync(H3TimelineClip clip, CancellationToken ct)
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
            var (workflow, saveNodeId, thumbnailNodeId) = await BuildClipWorkflowWithSaveNodeAsync(clip, uploadedImages);

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
                        StatusText = $"Generating clip {clip.DisplayIndex}: {pct:F0}%";
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, ct);

                if (string.IsNullOrEmpty(promptId))
                {
                    throw new Exception("ComfyUI returned empty prompt ID - workflow may have errors");
                }

                AddLog($"Clip {clip.DisplayIndex} completed with prompt ID: {promptId}");

                // Get the output video path from the save node
                var outputPath = await ResolveClipOutputPathAsync(promptId, saveNodeId, clip.DisplayIndex, ct);
                if (!string.IsNullOrEmpty(outputPath))
                {
                    clip.OutputPath = outputPath;
                    AddLog($"Clip {clip.DisplayIndex} output: {Path.GetFileName(outputPath)}");
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
                if (ex.InnerException != null)
                {
                    AddLog($"  Inner error: {ex.InnerException.Message}");
                }
                throw;
            }
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

        private string? FindVideoOnDisk(string prefix)
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
                        .Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .ToList();

                    if (files.Count > 0)
                        return files[0];
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

        // Two workflows: one with reference images, one without (text-to-video)
        private const string H3RefWorkflowPath = "workflow/video/h3-minimax/video_minimax_h3_r2v.json";
        private const string H3TextWorkflowPath = "workflow/video/h3-minimax/h3-singularity.json";

        // Node IDs in video_minimax_h3_r2v.json (reference-to-video)
        private const string RefPromptNodeId = "138";        // PrimitiveStringMultiline - prompt text
        private const string RefResolutionNodeId = "115";    // ResolutionSelector - aspect ratio, megapixels
        private const string RefDurationNodeId = "132";      // PrimitiveFloat - duration in seconds
        private const string RefSaveVideoNodeId = "92";      // SaveVideo - filename_prefix
        private const string RefSchedulerNodeId = "124";     // BasicScheduler - steps
        private const string RefSamplerNodeId = "123";       // KSamplerSelect - sampler_name
        private const string Picture1NodeId = "137";         // LoadImage for Picture 1 (ref_image_0)
        private const string Picture2NodeId = "139";         // LoadImage for Picture 2 (ref_image_1)

        // Node IDs in h3-singularity.json (text-to-video, no reference images)
        private const string TextPromptNodeId = "22:11";     // PrimitiveStringMultiline - prompt text
        private const string TextResolutionNodeId = "22:9";  // ResolutionSelector - aspect ratio, megapixels
        private const string TextDurationNodeId = "22:23";   // PrimitiveFloat - duration in seconds
        private const string TextSaveVideoNodeId = "18";     // VHS_VideoCombine - preview_1
        private const string TextStepsNodeId = "22:8";       // INTConstant - steps
        private const string TextSamplerNodeId = "22:6";     // KSamplerSelect - sampler_name

        private async Task<JsonObject> BuildClipWorkflowAsync(H3TimelineClip clip, string?[] uploadedImages)
        {
            var (workflow, _, _) = await BuildClipWorkflowWithSaveNodeAsync(clip, uploadedImages);
            return workflow;
        }

        // Node IDs for dynamically added thumbnail extraction
        private const string ThumbnailExtractNodeId = "h3ve_thumb_extract";
        private const string ThumbnailSaveNodeId = "h3ve_thumb_save";

        // Source node for images in text-to-video workflow (feeds into VHS_VideoCombine "18")
        private const string TextImagesSourceNodeId = "176";

        private async Task<(JsonObject workflow, string saveNodeId, string thumbnailNodeId)> BuildClipWorkflowWithSaveNodeAsync(H3TimelineClip clip, string?[] uploadedImages)
        {
            // Determine which workflow to use based on whether we have images
            bool hasImages = uploadedImages.Any(img => !string.IsNullOrEmpty(img));
            var workflowFile = hasImages ? H3RefWorkflowPath : H3TextWorkflowPath;
            var saveNodeId = hasImages ? RefSaveVideoNodeId : TextSaveVideoNodeId;

            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, workflowFile);
            if (!File.Exists(workflowPath))
            {
                AddLog($"ERROR: Workflow file not found: {workflowPath}");
                throw new FileNotFoundException($"Workflow file not found: {workflowFile}");
            }

            try
            {
                var json = await File.ReadAllTextAsync(workflowPath);
                var workflow = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(json);
                if (workflow == null)
                {
                    throw new Exception("Failed to parse workflow JSON");
                }

                AddLog($"Loaded {(hasImages ? "reference-to-video" : "text-to-video")} workflow with {workflow.Count} nodes");

                // Patch the workflow with clip-specific values
                PatchWorkflowForClip(workflow, clip, uploadedImages, hasImages);

                // Add thumbnail extraction nodes
                AddThumbnailNodes(workflow, clip, hasImages);

                return (workflow, saveNodeId, ThumbnailSaveNodeId);
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading workflow: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Adds nodes to extract and save the first frame as a thumbnail.
        /// This happens within ComfyUI so it's fast and doesn't require FFmpeg.
        /// </summary>
        private void AddThumbnailNodes(JsonObject workflow, H3TimelineClip clip, bool hasImages)
        {
            // Determine the source node for images based on workflow type
            // Ref workflow uses node 122 (VAEDecode), Text workflow uses node 176 (VAEDecode)
            var imagesSourceNode = hasImages ? "122" : TextImagesSourceNodeId;

            // Add ImageFromBatch node to extract the first frame
            // Note: ImageFromBatch uses "image" (singular) as input, and "batch_index" for position
            workflow[ThumbnailExtractNodeId] = new JsonObject
            {
                ["inputs"] = new JsonObject
                {
                    ["image"] = new JsonArray { imagesSourceNode, 0 },
                    ["batch_index"] = 0
                },
                ["class_type"] = "ImageFromBatch",
                ["_meta"] = new JsonObject
                {
                    ["title"] = "Extract Thumbnail Frame"
                }
            };

            // Add SaveImage node to save the extracted frame
            workflow[ThumbnailSaveNodeId] = new JsonObject
            {
                ["inputs"] = new JsonObject
                {
                    ["images"] = new JsonArray { ThumbnailExtractNodeId, 0 },
                    ["filename_prefix"] = $"video/{ProjectFolder}/clip_{clip.DisplayIndex:D5}_thumb"
                },
                ["class_type"] = "SaveImage",
                ["_meta"] = new JsonObject
                {
                    ["title"] = "Save Thumbnail"
                }
            };
        }

        private void PatchWorkflowForClip(JsonObject workflow, H3TimelineClip clip, string?[] uploadedImages, bool hasImages)
        {
            if (hasImages)
            {
                // Patch reference-to-video workflow (video_minimax_h3_r2v.json)
                PatchRefWorkflow(workflow, clip, uploadedImages);
            }
            else
            {
                // Patch text-to-video workflow (h3-singularity.json)
                PatchTextWorkflow(workflow, clip);
            }
        }

        private void PatchRefWorkflow(JsonObject workflow, H3TimelineClip clip, string?[] uploadedImages)
        {
            // Patch prompt text
            if (workflow[RefPromptNodeId] is JsonObject promptNode &&
                promptNode["inputs"] is JsonObject promptInputs)
            {
                promptInputs["value"] = clip.Prompt;
                AddLog($"Patched prompt in node {RefPromptNodeId}");
            }

            // Patch resolution
            if (workflow[RefResolutionNodeId] is JsonObject resNode &&
                resNode["inputs"] is JsonObject resInputs)
            {
                resInputs["aspect_ratio"] = SelectedAspectRatio;
                resInputs["megapixels"] = Megapixels;
                AddLog($"Patched resolution: {SelectedAspectRatio}, {Megapixels}MP");
            }

            // Patch duration
            if (workflow[RefDurationNodeId] is JsonObject durNode &&
                durNode["inputs"] is JsonObject durInputs)
            {
                durInputs["value"] = clip.DurationSeconds;
                AddLog($"Patched duration: {clip.DurationSeconds}s");
            }

            // Patch steps
            if (workflow[RefSchedulerNodeId] is JsonObject schedNode &&
                schedNode["inputs"] is JsonObject schedInputs)
            {
                schedInputs["steps"] = Steps;
                AddLog($"Patched steps: {Steps}");
            }

            // Patch sampler
            if (workflow[RefSamplerNodeId] is JsonObject sampNode &&
                sampNode["inputs"] is JsonObject sampInputs)
            {
                sampInputs["sampler_name"] = SelectedSampler;
                AddLog($"Patched sampler: {SelectedSampler}");
            }

            // Patch save filename
            if (workflow[RefSaveVideoNodeId] is JsonObject saveNode &&
                saveNode["inputs"] is JsonObject saveInputs)
            {
                saveInputs["filename_prefix"] = $"video/{ProjectFolder}/clip_{clip.DisplayIndex:D5}";
                AddLog($"Patched filename prefix: video/{ProjectFolder}/clip_{clip.DisplayIndex:D5}");
            }

            // Patch reference images using the uploaded filenames
            var pictureNodeIds = new[] { Picture1NodeId, Picture2NodeId };

            for (int i = 0; i < Math.Min(2, uploadedImages.Length); i++)
            {
                var uploadedFileName = uploadedImages[i];
                if (string.IsNullOrEmpty(uploadedFileName)) continue;

                if (workflow[pictureNodeIds[i]] is JsonObject picNode &&
                    picNode["inputs"] is JsonObject picInputs)
                {
                    picInputs["image"] = uploadedFileName;
                    AddLog($"Patched Picture {i + 1} in node {pictureNodeIds[i]}: {uploadedFileName}");
                }
            }
        }

        private void PatchTextWorkflow(JsonObject workflow, H3TimelineClip clip)
        {
            // Patch prompt text
            if (workflow[TextPromptNodeId] is JsonObject promptNode &&
                promptNode["inputs"] is JsonObject promptInputs)
            {
                promptInputs["value"] = clip.Prompt;
                AddLog($"Patched prompt in node {TextPromptNodeId}");
            }

            // Patch resolution
            if (workflow[TextResolutionNodeId] is JsonObject resNode &&
                resNode["inputs"] is JsonObject resInputs)
            {
                resInputs["aspect_ratio"] = SelectedAspectRatio;
                resInputs["megapixels"] = Megapixels;
                AddLog($"Patched resolution: {SelectedAspectRatio}, {Megapixels}MP");
            }

            // Patch duration
            if (workflow[TextDurationNodeId] is JsonObject durNode &&
                durNode["inputs"] is JsonObject durInputs)
            {
                durInputs["value"] = clip.DurationSeconds;
                AddLog($"Patched duration: {clip.DurationSeconds}s");
            }

            // Patch steps (INTConstant in this workflow)
            if (workflow[TextStepsNodeId] is JsonObject stepsNode &&
                stepsNode["inputs"] is JsonObject stepsInputs)
            {
                stepsInputs["value"] = Steps;
                AddLog($"Patched steps: {Steps}");
            }

            // Patch sampler
            if (workflow[TextSamplerNodeId] is JsonObject sampNode &&
                sampNode["inputs"] is JsonObject sampInputs)
            {
                sampInputs["sampler_name"] = SelectedSampler;
                AddLog($"Patched sampler: {SelectedSampler}");
            }

            // Patch save filename (VHS_VideoCombine in this workflow)
            if (workflow[TextSaveVideoNodeId] is JsonObject saveNode &&
                saveNode["inputs"] is JsonObject saveInputs)
            {
                saveInputs["filename_prefix"] = $"video/{ProjectFolder}/clip_{clip.DisplayIndex:D5}";
                AddLog($"Patched filename prefix: video/{ProjectFolder}/clip_{clip.DisplayIndex:D5}");
            }

            AddLog("Using text-to-video workflow (no reference images)");
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

        private async Task JoinClipsAsync()
        {
            if (!CanJoin) return;

            IsJoining = true;
            StatusText = "Joining clips...";

            try
            {
                var renderedClips = TimelineClips
                    .Where(c => c.IsRendered && !string.IsNullOrEmpty(c.OutputPath) && File.Exists(c.OutputPath))
                    .OrderBy(c => c.Index)
                    .ToList();

                if (renderedClips.Count == 0)
                {
                    AddLog("No rendered clips to join");
                    return;
                }

                AddLog($"Joining {renderedClips.Count} clips with FFmpeg...");

                // Determine output path
                var settings = _settingsService.Settings;
                var outputFolder = settings?.OutputFolderPath ?? Path.GetDirectoryName(renderedClips[0].OutputPath) ?? ".";
                var projectDir = Path.Combine(outputFolder, "video", ProjectFolder);
                Directory.CreateDirectory(projectDir);
                var outputPath = Path.Combine(projectDir, $"joined_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                // Use FFmpeg to concatenate the clips
                await Task.Run(() => JoinClipsWithFFmpeg(renderedClips.Select(c => c.OutputPath!).ToList(), outputPath));

                if (File.Exists(outputPath))
                {
                    JoinedVideoPath = outputPath;
                    PreviewVideoPath = outputPath;
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
                var args = Crossfade && CrossfadeFrames > 0
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

        private const string UpscaleWorkflowPath = "workflow/video/h3-minimax/h3-seed-upscale.json";

        private async Task UpscaleAsync()
        {
            if (!CanUpscale || string.IsNullOrEmpty(JoinedVideoPath)) return;

            IsUpscaling = true;
            StatusText = "Upscaling video...";

            try
            {
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", CancellationToken.None);

                // Upload the video to ComfyUI input folder
                AddLog($"Uploading video for upscale: {Path.GetFileName(JoinedVideoPath)}");
                var uploadedName = await _comfyUIService.UploadVideoAsync(JoinedVideoPath, CancellationToken.None);
                AddLog($"Uploaded as: {uploadedName}");

                // Build and execute the upscale workflow
                var (workflow, saveNodeId) = await BuildUpscaleWorkflowAsync(uploadedName);

                AddLog("Submitting upscale workflow to ComfyUI...");
                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        StatusText = $"Upscaling: {pct:F0}%";
                        Progress = pct;
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, CancellationToken.None);

                if (string.IsNullOrEmpty(promptId))
                {
                    throw new Exception("ComfyUI returned empty prompt ID");
                }

                // Get output path
                var outputPath = await ResolveUpscaleOutputAsync(promptId, saveNodeId);
                if (!string.IsNullOrEmpty(outputPath))
                {
                    PreviewVideoPath = outputPath;
                    AddLog($"Upscale complete: {Path.GetFileName(outputPath)}");
                }

                StatusText = "Upscale complete";
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

        private async Task<(JsonObject workflow, string saveNodeId)> BuildUpscaleWorkflowAsync(string uploadedVideoName)
        {
            // Try to load a workflow template, or build one dynamically
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UpscaleWorkflowPath);
            JsonObject workflow;
            var saveNodeId = "output";

            if (File.Exists(workflowPath))
            {
                AddLog($"Loading upscale workflow template...");
                var json = await File.ReadAllTextAsync(workflowPath);
                workflow = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(json)!;

                // Patch the video input and output prefix
                foreach (var node in workflow)
                {
                    if (node.Value is JsonObject nodeObj &&
                        nodeObj["class_type"]?.GetValue<string>() is string classType)
                    {
                        if (classType == "VHS_LoadVideo" || classType == "VHS_LoadVideoPath")
                        {
                            if (nodeObj["inputs"] is JsonObject inputs)
                            {
                                inputs["video"] = uploadedVideoName;
                                AddLog($"Patched video input in node {node.Key}");
                            }
                        }
                        else if (classType == "MinimaxH3LatentUpscaler3D")
                        {
                            if (nodeObj["inputs"] is JsonObject inputs)
                            {
                                inputs["mode.scale"] = UpscaleFactor;
                                AddLog($"Patched upscale factor to {UpscaleFactor}x in node {node.Key}");
                            }
                        }
                        else if (classType == "VHS_VideoCombine" || classType == "SaveVideo")
                        {
                            saveNodeId = node.Key;
                            if (nodeObj["inputs"] is JsonObject inputs)
                            {
                                inputs["filename_prefix"] = $"video/{ProjectFolder}/upscale";
                            }
                        }
                    }
                }
            }
            else
            {
                AddLog("Building H3 Latent Upscale workflow...");
                // Build a MinimaxH3LatentUpscaler3D workflow
                // This requires: Load video → VAE Encode → Latent Upscale → VAE Decode → Save
                workflow = new JsonObject
                {
                    // Load the H3 video VAE
                    ["vae_loader"] = new JsonObject
                    {
                        ["class_type"] = "VAELoader",
                        ["inputs"] = new JsonObject
                        {
                            ["vae_name"] = "minimax_h3_video_vae_fp16.safetensors"
                        }
                    },
                    // Load the input video
                    ["load_video"] = new JsonObject
                    {
                        ["class_type"] = "VHS_LoadVideo",
                        ["inputs"] = new JsonObject
                        {
                            ["video"] = uploadedVideoName,
                            ["force_rate"] = 24,
                            ["custom_width"] = 0,
                            ["custom_height"] = 0,
                            ["frame_load_cap"] = 0,
                            ["skip_first_frames"] = 0,
                            ["select_every_nth"] = 1,
                            ["format"] = "AnimateDiff"
                        }
                    },
                    // Encode to latent
                    ["vae_encode"] = new JsonObject
                    {
                        ["class_type"] = "VAEEncode",
                        ["inputs"] = new JsonObject
                        {
                            ["pixels"] = new JsonArray { "load_video", 0 },
                            ["vae"] = new JsonArray { "vae_loader", 0 }
                        }
                    },
                    // H3 Latent Upscaler
                    ["latent_upscale"] = new JsonObject
                    {
                        ["class_type"] = "MinimaxH3LatentUpscaler3D",
                        ["inputs"] = new JsonObject
                        {
                            ["latent"] = new JsonArray { "vae_encode", 0 },
                            ["model_name"] = "minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors",
                            ["mode"] = "scale by multiplier",
                            ["mode.scale"] = UpscaleFactor,
                            ["align"] = 32,
                            ["enable_temporal_chunking"] = true,
                            ["force_unload"] = true,
                            ["device"] = "cuda",
                            ["precision"] = "fp16"
                        }
                    },
                    // Decode back to pixels
                    ["vae_decode"] = new JsonObject
                    {
                        ["class_type"] = "VAEDecode",
                        ["inputs"] = new JsonObject
                        {
                            ["samples"] = new JsonArray { "latent_upscale", 0 },
                            ["vae"] = new JsonArray { "vae_loader", 0 }
                        }
                    },
                    // Output video
                    ["output"] = new JsonObject
                    {
                        ["class_type"] = "VHS_VideoCombine",
                        ["inputs"] = new JsonObject
                        {
                            ["images"] = new JsonArray { "vae_decode", 0 },
                            ["frame_rate"] = 24,
                            ["loop_count"] = 0,
                            ["filename_prefix"] = $"video/{ProjectFolder}/upscale",
                            ["format"] = "video/h264-mp4",
                            ["pingpong"] = false,
                            ["save_output"] = true,
                            ["crf"] = Crf
                        }
                    }
                };
                saveNodeId = "output";
            }

            return (workflow, saveNodeId);
        }

        private async Task<string?> ResolveUpscaleOutputAsync(string promptId, string saveNodeId)
        {
            try
            {
                var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, CancellationToken.None);
                if (byNode.TryGetValue(saveNodeId, out var outputs) && outputs.Count > 0)
                {
                    return await ResolveVideoToLocalAsync(outputs[0]);
                }
            }
            catch (Exception ex)
            {
                AddLog($"Warning: Could not resolve upscale output: {ex.Message}");
            }
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
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
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

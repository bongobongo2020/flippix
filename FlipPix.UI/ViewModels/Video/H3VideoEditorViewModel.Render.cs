using System;
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
                var renderedClips = TimelineClips.Where(c => c.IsRendered && File.Exists(c.OutputPath)).ToList();
                if (renderedClips.Count == 0)
                {
                    AddLog("No rendered clips to join");
                    return;
                }

                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", CancellationToken.None);

                // Build H3JointRender workflow
                var workflow = BuildJoinWorkflow(renderedClips);
                await _comfyUIService.QueuePromptAsync(workflow);

                AddLog($"Submitted join workflow for {renderedClips.Count} clips");
                StatusText = "Join complete";
            }
            catch (Exception ex)
            {
                AddLog($"Join failed: {ex.Message}");
                StatusText = "Join failed";
            }
            finally
            {
                IsJoining = false;
                UpscaleCommand.NotifyCanExecuteChanged();
            }
        }

        private JsonObject BuildJoinWorkflow(System.Collections.Generic.List<H3TimelineClip> clips)
        {
            var workflow = new JsonObject();

            // Build the sequence string for H3Timeline
            var sequence = string.Join("\n", clips.Select(c => c.OutputPath));

            workflow["timeline"] = new JsonObject
            {
                ["class_type"] = "H3Timeline",
                ["inputs"] = new JsonObject
                {
                    ["sequence"] = sequence,
                    ["base_folder"] = ProjectFolder,
                    ["preview_filename"] = "obvpm_h3_preview",
                    ["export_filename_prefix"] = "export",
                    ["crf"] = Crf,
                    ["level_lock"] = LevelLock,
                    ["level_lock_frames"] = LevelLockFrames,
                    ["level_lock_flicker"] = LevelLockFlicker,
                    ["crossfade"] = Crossfade,
                    ["crossfade_frames"] = CrossfadeFrames,
                    ["audio_declick"] = AudioDeclick
                }
            };

            workflow["render"] = new JsonObject
            {
                ["class_type"] = "H3JointRender",
                ["inputs"] = new JsonObject
                {
                    ["base_folder"] = ProjectFolder,
                    ["filename_prefix"] = "joined",
                    ["crf"] = Crf,
                    ["window_seconds"] = 5
                }
            };

            return workflow;
        }

        // ── Upscale ──────────────────────────────────────────────────────────────────────────────

        private async Task UpscaleAsync()
        {
            if (!CanUpscale || string.IsNullOrEmpty(JoinedVideoPath)) return;

            IsUpscaling = true;
            StatusText = "Upscaling video...";

            try
            {
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", CancellationToken.None);

                var workflow = BuildUpscaleWorkflow(JoinedVideoPath);
                await _comfyUIService.QueuePromptAsync(workflow);

                AddLog("Submitted upscale workflow");
                StatusText = "Upscale complete";
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

        private JsonObject BuildUpscaleWorkflow(string inputPath)
        {
            var workflow = new JsonObject();

            workflow["upscale"] = new JsonObject
            {
                ["class_type"] = "MinimaxH3LatentUpscaler3D",
                ["inputs"] = new JsonObject
                {
                    ["upscale_factor"] = UpscaleFactor,
                    ["model_name"] = "minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors"
                }
            };

            workflow["refine"] = new JsonObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JsonObject
                {
                    ["steps"] = (int)(Steps * RefineAmount),
                    ["denoise"] = RefineAmount,
                    ["sampler_name"] = SelectedSampler,
                    ["scheduler"] = SelectedScheduler
                }
            };

            workflow["output"] = new JsonObject
            {
                ["class_type"] = "SaveVideo",
                ["inputs"] = new JsonObject
                {
                    ["filename_prefix"] = $"{ProjectFolder}/upscale",
                    ["crf"] = Crf
                }
            };

            return workflow;
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

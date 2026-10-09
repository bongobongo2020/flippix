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
                    }
                    catch (OperationCanceledException)
                    {
                        clip.State = H3ClipState.Pending;
                        AddLog($"Clip {clip.DisplayIndex} cancelled");
                        break;
                    }
                    catch (Exception ex)
                    {
                        clip.State = H3ClipState.Failed;
                        AddLog($"Clip {clip.DisplayIndex} failed: {ex.Message}");
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
            var workflow = await BuildClipWorkflowAsync(clip, uploadedImages);

            // Log workflow summary
            AddLog($"Workflow has {workflow.Count} nodes, {workflow.ToJsonString().Length} chars");

            // Submit to ComfyUI - pass the JsonObject directly, not as a string
            AddLog($"Submitting clip {clip.DisplayIndex} to ComfyUI...");
            try
            {
                var result = await _comfyUIService.QueuePromptAsync(workflow, ct);

                if (string.IsNullOrEmpty(result))
                {
                    throw new Exception("ComfyUI returned empty prompt ID - workflow may have errors");
                }

                AddLog($"Clip {clip.DisplayIndex} queued with prompt ID: {result}");

                // Note: QueuePromptAsync returns the prompt ID
                // The actual workflow completion would need polling or websocket events
                // For now, we mark as submitted - actual success depends on ComfyUI execution
            }
            catch (Exception ex)
            {
                AddLog($"ERROR submitting clip {clip.DisplayIndex}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AddLog($"  Inner error: {ex.InnerException.Message}");
                }
                throw;
            }
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
            // Determine which workflow to use based on whether we have images
            bool hasImages = uploadedImages.Any(img => !string.IsNullOrEmpty(img));
            var workflowFile = hasImages ? H3RefWorkflowPath : H3TextWorkflowPath;

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
                return workflow;
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading workflow: {ex.Message}");
                throw;
            }
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

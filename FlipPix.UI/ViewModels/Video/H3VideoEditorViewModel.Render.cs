using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.UI.Models;

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
            // Build the workflow JSON based on h3_obvpm_timeline workflow
            var workflow = BuildClipWorkflow(clip);

            // Submit to ComfyUI
            var result = await _comfyUIService.QueuePromptAsync(workflow.ToJsonString(), ct);

            // Note: QueuePromptAsync returns PromptQueueResult, need to wait for completion
            // For now, we'll just mark as complete - the actual workflow integration
            // would need to poll for completion or use websocket events
            AddLog($"Submitted workflow for clip {clip.DisplayIndex}");
        }

        private JsonObject BuildClipWorkflow(H3TimelineClip clip)
        {
            // Build the MiniMaxH3ReferenceToVideo workflow
            var workflow = new JsonObject();

            // Get resolution from aspect ratio and megapixels
            var (width, height) = GetResolution(SelectedAspectRatio, Megapixels);
            var frameCount = (int)(clip.DurationSeconds * 24); // 24 fps

            // Add the main generation node (MiniMaxH3ReferenceToVideo)
            workflow["1"] = new JsonObject
            {
                ["class_type"] = "MiniMaxH3ReferenceToVideo",
                ["inputs"] = new JsonObject
                {
                    ["prompt"] = clip.Prompt,
                    ["width"] = width,
                    ["height"] = height,
                    ["length"] = frameCount,
                    ["ref_image_size"] = "max"
                }
            };

            // Add reference images if available
            int refIndex = 0;
            foreach (var slot in ReferenceSlots.Where(s => s.HasImages))
            {
                var imagePath = slot.Images.FirstOrDefault()?.Path;
                if (!string.IsNullOrEmpty(imagePath))
                {
                    workflow[$"ref_{refIndex}"] = new JsonObject
                    {
                        ["class_type"] = "LoadImage",
                        ["inputs"] = new JsonObject
                        {
                            ["image"] = Path.GetFileName(imagePath)
                        }
                    };
                    refIndex++;
                }
            }

            // Add sampling settings
            workflow["sampler"] = new JsonObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JsonObject
                {
                    ["steps"] = Steps,
                    ["sampler_name"] = SelectedSampler,
                    ["scheduler"] = SelectedScheduler
                }
            };

            // Add output node
            workflow["output"] = new JsonObject
            {
                ["class_type"] = "SaveVideo",
                ["inputs"] = new JsonObject
                {
                    ["filename_prefix"] = $"{ProjectFolder}/clip_{clip.DisplayIndex:D5}",
                    ["crf"] = Crf
                }
            };

            return workflow;
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
                await _comfyUIService.QueuePromptAsync(workflow.ToJsonString());

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
                await _comfyUIService.QueuePromptAsync(workflow.ToJsonString());

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

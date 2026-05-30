using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    public partial class WanScailGgufViewModel : WanScailViewModel
    {
        protected override string WorkflowFileName => Path.Combine("video", "wan", "scail-gguf.json");

        public WanScailGgufViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            System.IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, lmStudioService, logger, settingsService, serviceProvider, workflowCoordinator, fileDialogService)
        {
        }

        // GGUF workflow runs at 480p short-edge; portrait videos swap to 480×832, landscape to 832×480
        // RTX VSR then doubles to ~960×1664 (portrait) or ~1664×960 (landscape)
        protected override (int Width, int Height) ComputeOutputResolution(int videoW, int videoH, int maxEdge)
        {
            const int shortEdge = 480;
            const int longEdge  = 832;
            const int alignment = 32;
            if (videoH > videoW)
            {
                // Portrait: width is short edge, height scaled from AR (capped to avoid OOM)
                int h = (int)(Math.Round((double)shortEdge * videoH / videoW / alignment) * alignment);
                return (shortEdge, Math.Min(h, longEdge));
            }
            else
            {
                // Landscape or square: height is short edge, width scaled from AR
                int w = (int)(Math.Round((double)shortEdge * videoW / videoH / alignment) * alignment);
                return (Math.Min(w, longEdge), shortEdge);
            }
        }

        protected override JsonElement UpdateWorkflowParameters(
            JsonElement workflow,
            string characterImageName,
            string videoName,
            int startFrame,
            int framesInChunk,
            string prompt,
            string negativePrompt,
            int fps,
            int maxEdge,
            long seed,
            int outputWidth = 0,
            int outputHeight = 0)
        {
            var workflowJson = workflow.GetRawText();
            int w = outputWidth > 0 ? outputWidth : maxEdge;
            int h = outputHeight > 0 ? outputHeight : maxEdge;
            AddLog($"Updating GGUF workflow: start={startFrame}, frames={framesInChunk}, fps={fps}, resolution={w}x{h}");

            // Node 26: Character reference image
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "26", "image", characterImageName);

            // Node 61: Input video
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "61", new Dictionary<string, object>
            {
                { "video", videoName },
                { "skip_first_frames", startFrame },
                { "frame_load_cap", framesInChunk }
            });

            // Node 23: Prompts
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "23", new Dictionary<string, object>
            {
                { "positive_prompt", prompt },
                { "negative_prompt", negativePrompt }
            });

            // Node 52: FPS
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "52", "value", fps);

            // Node 8: Width, Node 10: Height — both derived from video aspect ratio
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "8", "value", w);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "10", "value", h);

            // Node 218: Seed
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "218", "seed", seed);

            AddLog("✓ WAN SCAIL GGUF workflow nodes updated");
            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }
    }
}

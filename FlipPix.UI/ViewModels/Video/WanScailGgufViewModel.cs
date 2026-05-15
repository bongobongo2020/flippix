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
            long seed)
        {
            var workflowJson = workflow.GetRawText();
            AddLog($"Updating GGUF workflow: start={startFrame}, frames={framesInChunk}, fps={fps}, maxEdge={maxEdge}");

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

            // Node 8: Width (maxEdge)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "8", "value", maxEdge);

            // Node 218: Seed
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "218", "seed", seed);

            AddLog("✓ WAN SCAIL GGUF workflow nodes updated");
            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }
    }
}

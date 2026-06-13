using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "WAN SCAIL II" tab. Uses the SCAIL-2 "simple" long-video workflow, which wraps the
    /// whole pipeline in a single SCAIL2SimpleVideo node that processes the entire clip
    /// internally. This view model therefore runs one whole-video execution rather than
    /// slicing the video into chunks in C#.
    /// </summary>
    public partial class WanScailGgufViewModel : WanScailViewModel
    {
        protected override string WorkflowFileName => Path.Combine("video", "wan", "SCAIL2_simple (1).json");

        // The SCAIL2SimpleVideo node handles long video internally (max_frames = 0 = whole clip),
        // so never slice in C#: one huge "chunk" means TotalChunks == 1 and the whole video is sent.
        protected override int FramesPerChunk => int.MaxValue;

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

        // ── SCAIL II-specific inputs ──────────────────────────────────────────

        private string _subject = string.Empty;
        /// <summary>Subject description fed to the SAM3 tracker (node 31), e.g. "a man boxing".</summary>
        public string Subject
        {
            get => _subject;
            set { if (_subject != value) { _subject = value; OnPropertyChanged(); } }
        }

        private bool _replaceBackground = true;
        /// <summary>
        /// True = "replace character and background" (SCAIL2 mode "animation");
        /// False = "replace character" only, keeping the original background (mode "replacement").
        /// </summary>
        public bool ReplaceBackground
        {
            get => _replaceBackground;
            set { if (_replaceBackground != value) { _replaceBackground = value; OnPropertyChanged(); } }
        }

        private bool _optimizeVram = true;
        /// <summary>
        /// When true, node 10 (DiffusionModelLoaderKJ) loads the 14B SCAIL2 weights as
        /// fp8_e4m3fn instead of "default", keeping the resident VRAM footprint as small as
        /// the file so a 24GB card is less likely to partially offload/stream weights each
        /// step. Toggle off to run the un-pinned "default" path and compare the timer.
        /// </summary>
        public bool OptimizeVram
        {
            get => _optimizeVram;
            set { if (_optimizeVram != value) { _optimizeVram = value; OnPropertyChanged(); } }
        }

        protected override void OnEnqueue(WanScailQueueItem item)
        {
            item.Subject = Subject;
            item.ReplaceBackground = ReplaceBackground;
            item.OptimizeVram = OptimizeVram;
            item.TrimSkipFrames = TrimSkipFrames;
            item.TrimFrameCap = TrimFrameCap;
        }

        // SCAIL2FitVideo sizes the pose video from a resolution preset, so we don't crop the
        // reference image to a forced aspect ratio. Returning (0,0) tells the base pipeline to
        // skip cropping and upload the character image as-is.
        protected override (int Width, int Height) ComputeOutputResolution(int videoW, int videoH, int maxEdge) => (0, 0);

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
            int outputHeight = 0,
            WanScailQueueItem? item = null)
        {
            var workflowJson = workflow.GetRawText();

            var subject = string.IsNullOrWhiteSpace(item?.Subject) ? "person" : item!.Subject.Trim();
            bool replaceBackground = item?.ReplaceBackground ?? true;
            // SCAIL2SimpleVideo.mode is an enum: "replacement" = character only (keep background),
            // "animation" = regenerate the whole frame (character + background).
            string mode = replaceBackground ? "animation" : "replacement";
            // SCAIL2FitVideo.resolution is an enum limited to "512p" / "704p".
            string resolution = maxEdge >= 704 ? "704p" : "512p";

            // Node 10 (DiffusionModelLoaderKJ): pin weights to fp8 when optimizing VRAM so the
            // 14B model stays small in VRAM; otherwise let it use the loader "default" dtype.
            bool optimizeVram = item?.OptimizeVram ?? true;
            string weightDtype = optimizeVram ? "fp8_e4m3fn" : "default";

            AddLog($"Updating SCAIL II (simple) workflow: whole video, fps={fps}, resolution={resolution}, " +
                   $"mode={mode}, subject=\"{subject}\", weight_dtype={weightDtype}");

            // Node 1: reference / character image (LoadImage)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "1", "image", characterImageName);

            // Node 10: diffusion model loader — VRAM-optimize toggle drives weight_dtype.
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "10", "weight_dtype", weightDtype);

            // Node 2: driving video (VHS_LoadVideo). Honour the in/out trim (in target-FPS frames):
            // skip_first_frames = in-point, frame_load_cap = kept length (0 = to the end).
            int skipFrames = item?.TrimSkipFrames ?? 0;
            int frameCap = item?.TrimFrameCap ?? 0;
            if (skipFrames > 0 || frameCap > 0)
                AddLog($"Trim: skip_first_frames={skipFrames}, frame_load_cap={(frameCap > 0 ? frameCap.ToString() : "all")}");
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "2", new Dictionary<string, object>
            {
                { "video", videoName },
                { "skip_first_frames", skipFrames },
                { "frame_load_cap", frameCap },
                { "force_rate", fps }
            });

            // Node 3: SCAIL2FitVideo resolution preset
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "3", "resolution", resolution);

            // Node 17: positive prompt, Node 18: negative prompt (CLIPTextEncode)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "17", "text", prompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "18", "text", negativePrompt);

            // Node 31: SAM3 subject to detect / track / mask
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "31", "text", subject);

            // Node 40: SCAIL2SimpleVideo — seed and replacement mode. max_frames stays 0 (whole video).
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "40", new Dictionary<string, object>
            {
                { "seed", seed },
                { "mode", mode }
            });

            // Node 43: final output (VHS_VideoCombine). Match the requested frame rate and pin into the
            // wan_scail subfolder so the filesystem-polling fallback can find it.
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "43", new Dictionary<string, object>
            {
                { "frame_rate", fps },
                { "filename_prefix", "wan_scail/SCAIL2_simple" },
                { "save_output", true }
            });

            AddLog("✓ SCAIL II workflow nodes updated");
            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }
    }
}

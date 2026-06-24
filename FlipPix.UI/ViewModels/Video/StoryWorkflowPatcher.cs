using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// Shared parameter injection for the "story" video workflows (Vantage Sulphur 2, 10Eros,
    /// LTX-22-B, DaSiWa WAN 2.2, WAN 2.2 FunCamera I2V).
    ///
    /// These workflows are offered both by the Story Video Generator (batch, via
    /// <see cref="VideoGeneratorMainViewModel"/>) and the Single Video tab (single image, via
    /// <see cref="LTX23BasicViewModel"/>). Keeping the node-id maps in one place means both callers
    /// stay in sync when a workflow JSON changes.
    /// </summary>
    public static class StoryWorkflowPatcher
    {
        /// <summary>
        /// Path (relative to the app's "workflow" folder) of the JSON for the given story workflow.
        /// </summary>
        public static string GetWorkflowRelativePath(VideoGeneratorMainViewModel.StoryVideoWorkflow workflow) => workflow switch
        {
            VideoGeneratorMainViewModel.StoryVideoWorkflow.Eros10S => Path.Combine("video", "ltx", "10Eros_10SNodes_InstantAction_I2VAPI.json"),
            VideoGeneratorMainViewModel.StoryVideoWorkflow.LTX22B => Path.Combine("video", "ltx", "LTX-22-B.json"),
            VideoGeneratorMainViewModel.StoryVideoWorkflow.DasiwaWan22 => Path.Combine("video", "story", "DasiwaWan22WorkflowsI2VSVI2_fastfidelityCAioV83API.json"),
            VideoGeneratorMainViewModel.StoryVideoWorkflow.Wan22I2V => Path.Combine("video", "story", "WAN22-I2V-API.json"),
            _ => Path.Combine("video", "ltx", "Vantage-Sulphur-2-WorkflowAPI.json")
        };

        /// <summary>
        /// Injects the image, prompts, fps, frame count and seed into a deserialized story workflow.
        /// </summary>
        /// <param name="seed">Use a value &gt; 0 to force a seed; pass 0 to randomise.</param>
        public static JsonElement Patch(
            Dictionary<string, JsonElement> workflowDict,
            VideoGeneratorMainViewModel.StoryVideoWorkflow workflow,
            string imageName,
            string imagePath,
            string positivePrompt,
            string negativePrompt,
            int videoLength,
            int fps,
            long seed,
            Action<string> log)
        {
            // WAN 2.2 FunCamera I2V (WanVideoWrapper) uses a different field convention than the
            // LTX/primitive workflows below (positive_prompt/negative_prompt/num_frames, raw frames,
            // FPS fixed on the VHS combine node), so it has its own dedicated handler.
            if (workflow == VideoGeneratorMainViewModel.StoryVideoWorkflow.Wan22I2V)
                return PatchWan22I2V(workflowDict, imageName, positivePrompt, negativePrompt, videoLength, fps, seed, log);

            string imageNode, positiveNode, negativeNode, frameNode, fpsNode;
            string[] seedNodes;
            bool frameInSeconds = false;
            string seedField = "noise_seed";

            switch (workflow)
            {
                case VideoGeneratorMainViewModel.StoryVideoWorkflow.DasiwaWan22:
                    imageNode = "23";        // LoadImage First-Frame-Image
                    positiveNode = "2368";   // PrimitiveStringMultiline positive
                    negativeNode = "2371";   // PrimitiveStringMultiline negative
                    frameNode = "1512:1668"; // PrimitiveInt "Seconds"
                    fpsNode = "1512:1669";   // PrimitiveFloat FPS
                    seedNodes = new[] { "1512:1670" }; // PrimitiveInt Seed (uses "value")
                    seedField = "value";
                    frameInSeconds = true;
                    break;
                case VideoGeneratorMainViewModel.StoryVideoWorkflow.Eros10S:
                    imageNode = "528";
                    positiveNode = "536";
                    negativeNode = "537";
                    frameNode = "511";
                    fpsNode = "542";
                    seedNodes = new[] { "524" };
                    break;
                case VideoGeneratorMainViewModel.StoryVideoWorkflow.LTX22B:
                    imageNode = "5016:2004";
                    positiveNode = "5026:5018";
                    negativeNode = "5026:5019";
                    frameNode = "5026:4988";
                    fpsNode = "5026:4989";
                    seedNodes = new[] { "5002:4832", "5001:4967", "5012:5009" };
                    break;
                default: // VantageSulphur2
                    imageNode = "255";
                    positiveNode = "393";
                    negativeNode = "328";
                    frameNode = "322";
                    fpsNode = "304";
                    seedNodes = new[] { "259" };
                    frameInSeconds = true;
                    break;
            }

            // Image
            SetNodeField(workflowDict, imageNode, "image", imageName, "Image", log);

            // Positive prompt
            SetNodeTextField(workflowDict, positiveNode, positivePrompt, "Positive Prompt", log);

            // Vantage Sulphur 2: set width/height from input image orientation (nodes 261, 299)
            if (workflow == VideoGeneratorMainViewModel.StoryVideoWorkflow.VantageSulphur2)
            {
                var (videoW, videoH) = GetVideoDimensionsForImage(imagePath, log);
                UpdatePrimitiveIntNode(workflowDict, "261", videoW, "Width", log);
                UpdatePrimitiveIntNode(workflowDict, "299", videoH, "Height", log);
            }

            // Vantage Sulphur 2: bypass VisionLLMNode (412:390) by redirecting Any Switch (394)
            // any_01 normally points to 412:390 (VisionLLM), redirect it to 393 (manual prompt)
            if (workflow == VideoGeneratorMainViewModel.StoryVideoWorkflow.VantageSulphur2 && workflowDict.ContainsKey("394"))
            {
                var switchNode = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["394"].GetRawText());
                if (switchNode != null && switchNode.ContainsKey("inputs"))
                {
                    var switchInputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(switchNode["inputs"]));
                    if (switchInputs != null)
                    {
                        switchInputs["any_01"] = new object[] { "393", 0 };
                        switchNode["inputs"] = switchInputs;
                        workflowDict["394"] = JsonSerializer.SerializeToElement(switchNode);
                        log("✓ Node 394 (Prompt Selector) - Bypassed VisionLLM, using manual prompt");
                    }
                }
            }

            // Negative prompt
            if (!string.IsNullOrEmpty(negativePrompt))
                SetNodeTextField(workflowDict, negativeNode, negativePrompt, "Negative Prompt", log);

            // FPS
            SetNodeField(workflowDict, fpsNode, "value", (double)fps, "FPS", log);

            // Frame count (Vantage/DaSiWa use seconds, others use frames)
            object frameValue = frameInSeconds ? (object)(videoLength / (double)fps) : (object)videoLength;
            SetNodeField(workflowDict, frameNode, "value", frameValue, frameInSeconds ? "Duration(s)" : "Frame Count", log);

            // Seed
            var seedValue = seed > 0 ? seed : ((long)new Random().Next() << 32) | (uint)new Random().Next();
            foreach (var sid in seedNodes)
                SetNodeField(workflowDict, sid, seedField, seedValue, "Seed", log);

            log("Story LTX workflow parameters updated successfully");
            return JsonSerializer.SerializeToElement(workflowDict);
        }

        /// <summary>
        /// Parameter injection for the WAN 2.2 FunCamera I2V workflow (WAN22-I2V-API.json), a
        /// flattened ComfyUI-WanVideoWrapper graph. Node ids match the API file:
        ///   521 LoadImage (start image), 548 WanVideoTextEncode (positive_prompt + negative_prompt),
        ///   514 WanVideoImageToVideoEncode (num_frames), 561 PrimitiveInt (seed feeding both samplers),
        ///   30 VHS_VideoCombine (frame_rate). Frame count is raw frames (WAN wants 4n+1), not seconds.
        /// </summary>
        private static JsonElement PatchWan22I2V(
            Dictionary<string, JsonElement> workflowDict,
            string imageName,
            string positivePrompt,
            string negativePrompt,
            int videoLength,
            int fps,
            long seed,
            Action<string> log)
        {
            SetNodeField(workflowDict, "521", "image", imageName, "Start Image", log);
            SetNodeField(workflowDict, "548", "positive_prompt", positivePrompt, "Positive Prompt", log);
            if (!string.IsNullOrEmpty(negativePrompt))
                SetNodeField(workflowDict, "548", "negative_prompt", negativePrompt, "Negative Prompt", log);

            // WAN 2.2 14B I2V is far more VRAM-hungry per frame than LTX: at ~1024px the tested-safe
            // single-shot length is 81 frames (the source workflow's own default). The app's default
            // length (240, sized for LTX) rounds to 237 frames here and OOMs at the sampler, so
            // clamp to [5, 81] and snap to a valid 4n+1 count.
            int wanFrames = Math.Clamp(videoLength, 5, 81);
            wanFrames = ((wanFrames - 1) / 4) * 4 + 1;
            if (wanFrames != videoLength)
                log($"WAN 2.2 I2V: clamped frame count {videoLength} → {wanFrames} (4n+1, VRAM-safe)");
            SetNodeField(workflowDict, "514", "num_frames", wanFrames, "Frame Count", log);
            SetNodeField(workflowDict, "30", "frame_rate", (double)fps, "FPS", log);

            // Both samplers (562:308 high / 562:392 low) read the seed from PrimitiveInt 561.
            var seedValue = seed > 0 ? seed : ((long)new Random().Next() << 32) | (uint)new Random().Next();
            SetNodeField(workflowDict, "561", "value", seedValue, "Seed", log);

            log("WAN 2.2 FunCamera I2V workflow parameters updated successfully");
            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private static void SetNodeField(Dictionary<string, JsonElement> dict, string nodeId, string field, object value, string label, Action<string> log)
        {
            if (!dict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(dict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            inputs[field] = value;
            node["inputs"] = inputs;
            dict[nodeId] = JsonSerializer.SerializeToElement(node);
            log($"✓ Node {nodeId} ({label}) - Updated");
        }

        private static void SetNodeTextField(Dictionary<string, JsonElement> dict, string nodeId, string text, string label, Action<string> log)
        {
            if (!dict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(dict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            if (inputs.ContainsKey("value")) inputs["value"] = text;
            else inputs["text"] = text;
            node["inputs"] = inputs;
            dict[nodeId] = JsonSerializer.SerializeToElement(node);
            log($"✓ Node {nodeId} ({label}) - Updated");
        }

        private static void UpdatePrimitiveIntNode(Dictionary<string, JsonElement> workflowDict, string nodeId, int value, string label, Action<string> log)
        {
            if (!workflowDict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            inputs["value"] = value;
            node["inputs"] = inputs;
            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
            log($"✓ Node {nodeId} ({label}) - {value}px");
        }

        /// <summary>
        /// LTX-native output dimensions (multiples of 32) chosen from the input image's orientation.
        /// </summary>
        private static (int width, int height) GetVideoDimensionsForImage(string imagePath, Action<string> log)
        {
            try
            {
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();

                    int imgW = bi.PixelWidth;
                    int imgH = bi.PixelHeight;

                    bool portrait = imgH > imgW;
                    bool square = imgW == imgH;

                    if (square) return (720, 720);
                    if (portrait) return (720, 1280);
                    return (1280, 720); // landscape
                }
            }
            catch (Exception ex)
            {
                log($"WARNING: Could not read image dimensions for orientation detection: {ex.Message}");
            }

            return (1280, 720);
        }
    }
}

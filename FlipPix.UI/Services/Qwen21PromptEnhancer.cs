using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.ComfyUI.Services;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Runs the rewriter in qwen21-prompt-enhancer.json as its own job before the image job.
    /// <para>TextGenerate (472) returns an empty string for some prompts without reporting an error,
    /// the same way on every seed. Left inside the image job, the encoder then renders a blank prompt:
    /// an unrelated picture from a run ComfyUI calls a success. The graph has no way to test for empty
    /// text, so the text is read back here and the image job gets it through the switch's ORIGINAL
    /// route (468 -> 474), which leaves the rewriter out of that job.</para>
    /// </summary>
    public static class Qwen21PromptEnhancer
    {
        private const string PromptNode = "468";
        private const string SwitchFlagNode = "473";
        private const string EnhancerNode = "472";
        private const string PreviewNode = "475";

        // Everything the rewrite needs and nothing that loads the image model.
        private static readonly string[] EnhancerNodes = { PromptNode, "470", "471", "469", EnhancerNode, PreviewNode };

        /// <summary>
        /// Returns the image job to submit. With the enhancer off the graph comes back as it was;
        /// otherwise 468 holds the rewrite, or the original prompt when there is none, and 473 is off.
        /// </summary>
        public static Task<JsonElement> ResolveAsync(
            ComfyUIService comfyUI, object graph, Action<string> log, CancellationToken cancellationToken) =>
            ResolveAsync(graph, async (enhancerGraph, token) =>
            {
                var promptId = await comfyUI.ExecuteWorkflowAsync(enhancerGraph, cancellationToken: token);
                var outputs = await comfyUI.HttpClient.GetTextOutputsByNodeAsync(promptId, token);
                return outputs.TryGetValue(PreviewNode, out var lines) ? string.Concat(lines) : null;
            }, log, cancellationToken);

        public static async Task<JsonElement> ResolveAsync(
            object graph,
            Func<JsonElement, CancellationToken, Task<string?>> runEnhancer,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            var element = JsonSerializer.SerializeToElement(graph);
            var root = JsonNode.Parse(element.GetRawText())!.AsObject();

            if (root[SwitchFlagNode]?["inputs"]?["value"]?.GetValue<bool>() != true)
                return element;

            string? rewritten = null;
            try
            {
                rewritten = await runEnhancer(BuildEnhancerGraph(root), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"Qwen 2.1: prompt enhancer failed ({ex.Message})");
            }

            if (string.IsNullOrWhiteSpace(rewritten))
            {
                log("Qwen 2.1: prompt enhancer returned no text, using the original prompt");
            }
            else
            {
                log($"Qwen 2.1: enhanced prompt: {rewritten}");
                root[PromptNode]!["inputs"]!["value"] = rewritten.Trim();
            }

            root[SwitchFlagNode]!["inputs"]!["value"] = false;
            return JsonSerializer.SerializeToElement(root);
        }

        /// <summary>The rewriter alone, with its preview reading 472 directly instead of the switch.</summary>
        private static JsonElement BuildEnhancerGraph(JsonObject root)
        {
            var enhancer = new JsonObject(EnhancerNodes.Select(id =>
                new System.Collections.Generic.KeyValuePair<string, JsonNode?>(id, root[id]!.DeepClone())));
            enhancer[PreviewNode]!["inputs"]!["source"] = new JsonArray(EnhancerNode, 0);
            return JsonSerializer.SerializeToElement(enhancer);
        }
    }
}

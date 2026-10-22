using System.Text.Json;
using System.Text.Json.Nodes;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Keeps each tab's ComfyUI outputs in a folder of its own under output/, instead of loose in the top
    /// of it. Hundreds of loose files there slowed every folder listing — badly on ChromeOS, whose Linux
    /// container reaches a server's share through a file bridge — and buried each tab's results among
    /// every other tab's.
    /// </summary>
    public static class OutputFolders
    {
        public const string CameraAngles = "camera-angles";
        public const string Ideogram = "gram";
        public const string QwenInpaint = "qwen-inpaint";
        public const string KleinInpaint = "klein-inpaint";

        /// <summary>
        /// The folder a text-to-image model saves into, named after its file prefix, or null for Z-Image,
        /// whose graphs already save under ZImage/.
        /// </summary>
        public static string? ForTextWorkflow(TextGeneratorWorkflow workflow) => workflow switch
        {
            TextGeneratorWorkflow.Qwen2512 => "qwen2512",
            TextGeneratorWorkflow.Klien => "F2K_txt2img",
            TextGeneratorWorkflow.Anima => "Anima",
            TextGeneratorWorkflow.Krea2 => "Krea2",
            TextGeneratorWorkflow.ZimageBase => "ZBase",
            TextGeneratorWorkflow.Qwen21Enhancer => "Qwen21",
            TextGeneratorWorkflow.Moss => "Moss",
            _ => null,
        };

        /// <summary>
        /// Puts every node in an API-format graph that saves under a plain name ("Krea2") into
        /// <paramref name="folder"/> ("Krea2/Krea2"). A prefix that already names a folder is left alone,
        /// as is a prefix wired from another node. Covers the prefixes a graph ships with as well as the
        /// ones a tab sets, so a model whose graph decides its own name lands in its folder too.
        /// </summary>
        public static JsonElement RouteSaves(JsonElement workflow, string? folder)
        {
            if (string.IsNullOrEmpty(folder) || workflow.ValueKind != JsonValueKind.Object) return workflow;
            if (JsonNode.Parse(workflow.GetRawText()) is not JsonObject root) return workflow;

            foreach (var (_, node) in root)
            {
                if (node?["inputs"] is not JsonObject inputs) continue;
                if (inputs["filename_prefix"] is not JsonValue value || !value.TryGetValue<string>(out var prefix)) continue;
                if (string.IsNullOrEmpty(prefix) || prefix.Contains('/') || prefix.Contains('\\')) continue;
                inputs["filename_prefix"] = $"{folder}/{prefix}";
            }

            return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
        }

        /// <summary><see cref="RouteSaves(JsonElement, string?)"/> for a graph built as an object model.</summary>
        public static object RouteSaves(object workflow, string? folder) =>
            string.IsNullOrEmpty(folder) ? workflow
            : RouteSaves(workflow is JsonElement element ? element : JsonSerializer.SerializeToElement(workflow), folder);

        /// <summary>Joins a folder onto a plain save name, for a tab that sets its node's prefix itself.</summary>
        public static string Prefix(string folder, string name) => $"{folder}/{name}";
    }
}

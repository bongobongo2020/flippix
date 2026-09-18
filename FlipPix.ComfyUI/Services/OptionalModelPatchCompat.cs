using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlipPix.ComfyUI.Services;

/// <summary>
/// Drops model-patch nodes the connected ComfyUI doesn't have, instead of failing the whole prompt.
///
/// <para><b>The break.</b> Several workflow files were exported from a newer ComfyUI than the one they
/// are submitted to, and carry <c>ModelAttentionBackend</c> (widget <c>"comfy kitchen attention"</c>).
/// A server that predates it has no such class, so pre-submit validation rejects the graph with
/// "needs custom node(s) that aren't installed" — and there is nothing to install: it is core ComfyUI,
/// not a pack, so <see cref="FlipPix.Core.Interfaces.IMissingNodeResolver"/> can't offer a fix either.
/// Six workflows carry it (Ideogram, Z-Image, MiniMax I2V/FFLF, the cast-hybrid graph the H3 Cast,
/// Cast Hybrid and Ensemble tabs share), and it took down the storyboard pass as well as the render.</para>
///
/// <para><b>The fix.</b> These nodes are pure pass-throughs: MODEL in, the same MODEL out with a
/// setting patched onto it. When the class is absent from <c>/object_info</c>, the node is unwired —
/// every consumer of its output is re-pointed at whatever fed its own input — and deleted. What is lost
/// is the backend override, which is exactly what a server without the node was going to do anyway:
/// fall back to its own default attention. Everything else about the graph is unchanged.</para>
///
/// <para><b>Why not translate it.</b> The comfyui-ppm pack's <c>ModelAttentionSelector</c> looks like a
/// stand-in but offers a different set of backends (no "comfy kitchen"), so mapping onto it would swap
/// one silent behaviour change for another and add a pack dependency. Dropping the patch is the honest
/// equivalent.</para>
///
/// <para>Idempotent, a no-op when the server has the class, and a no-op on a graph with no such node.
/// Bypassed chains resolve transitively, so two stacked patches both disappear cleanly.</para>
/// </summary>
public static class OptionalModelPatchCompat
{
    /// <summary>
    /// Class types safe to unwire when missing, mapped to the input their output passes through.
    /// Only output slot 0 is passed through — everything here is a single-MODEL-out patch node.
    /// </summary>
    private static readonly Dictionary<string, string> PassthroughInput = new(StringComparer.Ordinal)
    {
        ["ModelAttentionBackend"] = "model",
    };

    /// <summary>
    /// Removes every bypassable node whose class is missing from <paramref name="loadedClassTypes"/>.
    /// Best-effort: returns the input untouched if it can't be read as an API graph.
    /// </summary>
    /// <param name="loadedClassTypes">Class types the connected ComfyUI has loaded (/object_info keys).</param>
    /// <param name="log">Called once if anything was bypassed, for the tab's log.</param>
    public static object Bypass(object workflow, IReadOnlyCollection<string> loadedClassTypes, Action<string>? log = null)
    {
        try
        {
            var json = workflow is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(workflow);
            if (JsonNode.Parse(json) is not JsonObject root) return workflow;

            var bypassed = Patch(root, loadedClassTypes);
            if (bypassed.Count == 0) return workflow;

            log?.Invoke(Describe(bypassed));
            return root;
        }
        catch
        {
            return workflow;
        }
    }

    /// <summary>Unwires the graph in place; returns a label per node dropped.</summary>
    private static List<string> Patch(JsonObject root, IReadOnlyCollection<string> loadedClassTypes)
    {
        var loaded = loadedClassTypes as ISet<string> ?? new HashSet<string>(loadedClassTypes, StringComparer.Ordinal);

        // id -> the link its output stands in for, before any rewiring.
        var replacements = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var labels = new List<string>();

        foreach (var (id, node) in root.ToList())
        {
            if (node is not JsonObject obj) continue;
            var classType = obj["class_type"]?.GetValue<string>();
            if (classType == null || !PassthroughInput.TryGetValue(classType, out var inputName)) continue;
            if (loaded.Contains(classType)) continue;

            // Only a wired input can be passed through; a literal (or nothing) leaves the graph as it
            // is, so the missing node is still reported rather than silently producing a broken link.
            if (obj["inputs"] is not JsonObject inputs || inputs[inputName] is not JsonArray link || link.Count < 2)
                continue;

            replacements[id] = link;
            labels.Add($"{id} ({classType})");
        }

        if (replacements.Count == 0) return labels;

        // Re-point every consumer, following chains of bypassed nodes to the first surviving source.
        foreach (var (_, node) in root)
        {
            if (node is not JsonObject obj || obj["inputs"] is not JsonObject inputs) continue;
            foreach (var (name, value) in inputs.ToList())
            {
                if (value is not JsonArray link || link.Count < 2) continue;
                var sourceId = LinkId(link);
                if (sourceId == null || !replacements.ContainsKey(sourceId)) continue;

                var resolved = Resolve(sourceId, replacements);
                inputs[name] = new JsonArray(resolved[0]?.DeepClone(), resolved[1]?.DeepClone());
            }
        }

        foreach (var id in replacements.Keys) root.Remove(id);
        return labels;
    }

    /// <summary>Walks a chain of bypassed nodes to the link that survives it.</summary>
    private static JsonArray Resolve(string id, Dictionary<string, JsonArray> replacements)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var link = replacements[id];
        while (seen.Add(id))
        {
            var next = LinkId(link);
            if (next == null || !replacements.TryGetValue(next, out var upstream)) break;
            id = next;
            link = upstream;
        }
        return link;
    }

    /// <summary>The source node id of a link, whether the export wrote it as a string or a number.</summary>
    private static string? LinkId(JsonArray link) => link[0] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Describe(IEnumerable<string> bypassed) =>
        $"Model-patch node(s) {string.Join(", ", bypassed)} aren't loaded in the connected ComfyUI and " +
        "were unwired — they pass their model straight through, so the graph runs on the server's " +
        "default attention backend instead of the one the workflow was exported with.";
}

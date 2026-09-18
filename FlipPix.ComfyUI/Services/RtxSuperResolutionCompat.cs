using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlipPix.ComfyUI.Services;

/// <summary>
/// Makes an <c>RTXVideoSuperResolution</c> node survive either version of the
/// <c>Nvidia_RTX_Nodes_ComfyUI</c> pack.
///
/// <para><b>The break.</b> The node's signature changed. It used to take three widgets —
/// <c>scale</c>, <c>quality</c>, <c>deblur</c> — and now takes <c>resize_type</c>, a
/// <c>COMFY_DYNAMICCOMBO_V3</c> whose "scale by multiplier" branch supplies <c>resize_type.scale</c>,
/// plus <c>quality</c>. <c>deblur</c> is gone. A graph exported against the old shape is rejected by an
/// updated server with <c>Required input is missing: resize_type</c> (or, on the pack version that still
/// declares the widget loosely, reaches the GPU and dies with
/// <c>execute() missing 1 required positional argument: 'resize_type'</c>) — because ComfyUI only checks
/// the inputs a node <i>declares</i> and silently ignores the rest.</para>
///
/// <para><b>The fix, and why it is a union rather than a migration.</b> That same "ignores the rest"
/// behaviour is what makes a graph carrying <i>both</i> shapes correct on either pack: each version
/// reads the widgets it declares and never sees the others. So this writes the union and neither
/// version has to be detected, which keeps a workflow file portable between an updated ComfyUI and one
/// that has not been updated yet — and costs no <c>/object_info</c> round trip to decide.</para>
///
/// <para><b>Why it runs on every submit.</b> The repo's workflow files already carry the union, but they
/// have lost it twice now: a re-export from a ComfyUI running the older pack writes the old widgets back,
/// and the tab silently stops working against the server. Sixteen nodes across sixteen files regressed
/// that way in one commit. Applying this in <c>ComfyUIHttpClient.SubmitPromptAsync</c> means a stale
/// re-export can no longer break a tab, and no view model has to remember to call it.</para>
///
/// <para>Idempotent, and a no-op on a graph with no such node.</para>
/// </summary>
public static class RtxSuperResolutionCompat
{
    public const string ClassType = "RTXVideoSuperResolution";

    /// <summary>The dynamic-combo branch our graphs use; the other is "target dimensions".</summary>
    private const string ScaleMode = "scale by multiplier";

    private const double DefaultScale = 2.0;
    private const string DefaultQuality = "ULTRA";
    private const string DefaultDeblur = "MEDIUM";

    /// <summary>
    /// Rewrites every <c>RTXVideoSuperResolution</c> node in an API graph to carry both signatures.
    /// </summary>
    /// <param name="log">Called once per node patched, for the tab's log.</param>
    /// <returns>The graph, unchanged when it has no such node.</returns>
    public static string Normalize(string json, Action<string>? log = null)
    {
        var root = JsonNode.Parse(json)?.AsObject();
        if (root == null) return json;

        var patched = Patch(root);
        if (patched.Count == 0) return json;

        log?.Invoke(Describe(patched));
        return root.ToJsonString();
    }

    /// <summary>
    /// Same rewrite over a workflow held as an object (the shape the submit path passes around).
    /// Best-effort: returns the input untouched if it can't be read as an API graph.
    /// </summary>
    public static object Normalize(object workflow, Action<string>? log = null)
    {
        try
        {
            var json = workflow is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(workflow);
            if (JsonNode.Parse(json) is not JsonObject root) return workflow;

            var patched = Patch(root);
            if (patched.Count == 0) return workflow;

            log?.Invoke(Describe(patched));
            return root;
        }
        catch
        {
            return workflow;
        }
    }

    /// <summary>Patches the graph in place; returns a label per node changed.</summary>
    private static List<string> Patch(JsonObject root)
    {
        var patched = new List<string>();
        foreach (var (id, node) in root.ToList())
        {
            if (node is not JsonObject obj) continue;
            if (obj["class_type"]?.GetValue<string>() != ClassType) continue;
            if (obj["inputs"] is not JsonObject inputs) continue;

            var mode = inputs["resize_type"]?.GetValue<string>();
            var targetDimensions = string.Equals(mode, "target dimensions", StringComparison.OrdinalIgnoreCase);

            // The multiplier, from whichever shape the export happened to use.
            var scale = ReadDouble(inputs["resize_type.scale"])
                        ?? ReadDouble(inputs["scale"])
                        ?? DefaultScale;

            var before = inputs.Count;

            // New shape. "target dimensions" is left alone — its own width/height widgets are the
            // answer there, and nothing in this repo uses that branch.
            if (!targetDimensions)
            {
                inputs["resize_type"] = ScaleMode;
                inputs["resize_type.scale"] = scale;
            }

            // Old shape. Kept (or restored) so a server still on the previous pack finds its widgets;
            // an updated one never looks at them.
            inputs["scale"] = scale;
            inputs["deblur"] ??= DefaultDeblur;
            inputs["quality"] ??= DefaultQuality;

            if (inputs.Count != before)
                patched.Add($"{id} (×{scale.ToString("0.#", CultureInfo.InvariantCulture)})");
        }

        return patched;
    }

    private static string Describe(IEnumerable<string> patched) =>
        $"RTX super-resolution node(s) {string.Join(", ", patched)} rewritten to carry both the old " +
        "(scale/deblur) and new (resize_type) widget sets — the workflow was exported against one " +
        "version of Nvidia_RTX_Nodes_ComfyUI and the server may have the other.";

    private static double? ReadDouble(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var d) ? d : null;
}

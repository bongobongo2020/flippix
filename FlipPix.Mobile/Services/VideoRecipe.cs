using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlipPix.Mobile.Services;

/// <summary>
/// MiniMax I2V (Ref2VA) for the phone: the desktop 🌀 MiniMax I2V tab's default render, one pass.
/// The pictures are <i>references</i> (how people and places look), not the first frame; the prompt
/// says what happens.
///
/// <para>Fixed to what the desktop ships by default, so there is nothing to tune: the Shipped stack,
/// 0.7 MP finished canvas (drafted at a quarter and latent-upscaled 2×), SLA at 0.85 with 64-row
/// blocks, audio enhancement on, RTX upscale off. One pass reaches 15 s, so there are no
/// continuations and the loop half of the graph is pruned away.</para>
///
/// Node map: the same ids as <c>MiniMaxI2VViewModel.BuildWorkflow</c> in FlipPix.UI.
/// </summary>
public static class VideoRecipe
{
    public const int MaxReferences = 4;
    public const int MaxSeconds = 15;
    private const double FinishedMegapixels = 0.7;
    private const double UpscaleFactor = 2.0; // must stay integral; see the desktop's LatentUpscaleFactor

    private const string NodeReference0 = "10";
    private const string NodeRef2V = "4145:174";
    private const string NodePrompt = "56";
    private const string NodeSeconds = "4145:147";
    private const string NodeSeed = "4145:149";
    private const string NodeResolution = "60";
    private const string NodeSink = "49";           // base pass only; 52 is base + continuations

    // The model wire, as the Shipped stack rebuilds it: Sol-Attn switch → turbo LoRA → sigma shift →
    // Spectrum → the preview overrides. The five switched LoRA seats in the file are stepped over and
    // pruned, exactly as the desktop does, rather than left for ComfyUI to validate.
    private const string NodeSolAttn = "55:3706";
    private const string NodeTurboLora = "55:3690";
    private const string NodeSigmaShift = "55:3704";
    private const string NodeSpectrum = "55:3705";
    private static readonly string[] NodePreview = { "39", "51" };

    private const string NodeSampler = "4145:140";
    private const string NodeDraftSigmas = "draft_split";
    private const string NodeUpscaler = "4145:4318";
    private const string NodeDetailSwitch = "4145:4220";
    private const string NodeRtxSwitch = "4145:139";
    private const string NodeAudioSwitch = "4145:143";

    /// <summary>ResolutionSelector's combo, widest to tallest. Anything else fails validation.</summary>
    private static readonly (string Option, double Ratio)[] Aspects =
    {
        ("21:9 (Ultrawide)", 21.0 / 9.0),
        ("16:9 (Widescreen)", 16.0 / 9.0),
        ("3:2 (Photo)", 3.0 / 2.0),
        ("4:3 (Standard)", 4.0 / 3.0),
        ("1:1 (Square)", 1.0),
        ("3:4 (Portrait Standard)", 3.0 / 4.0),
        ("2:3 (Portrait Photo)", 2.0 / 3.0),
        ("9:16 (Portrait Widescreen)", 9.0 / 16.0),
    };

    /// <summary>The nearest aspect option to the first picture, compared in log space like H3Canvas.</summary>
    public static string AspectFor(int width, int height)
    {
        if (width <= 0 || height <= 0) return "16:9 (Widescreen)";
        var r = Math.Log((double)width / height);
        return Aspects.OrderBy(a => Math.Abs(Math.Log(a.Ratio) - r)).First().Option;
    }

    public static double AspectRatioOf(string option) =>
        Aspects.FirstOrDefault(a => a.Option == option).Ratio is var r && r > 0 ? r : 16.0 / 9.0;

    public static JsonObject Build(IReadOnlyList<string> uploadedRefs, string prompt, int seconds,
        string aspect, long seed, string filePrefix)
    {
        if (uploadedRefs.Count == 0) throw new ArgumentException("At least one reference picture is needed.");
        var g = Workflows.Load("h3-minimax-i2v.json");

        // References: node 10 is picture 1; the rest are new loaders wired into the autogrow slots.
        Workflows.Set(g, NodeReference0, "image", uploadedRefs[0]);
        var loaders = new List<string> { NodeReference0 };
        for (var i = 1; i < uploadedRefs.Count && i < MaxReferences; i++)
        {
            var id = $"i2v_ref_{i}";
            g[id] = new JsonObject
            {
                ["class_type"] = "LoadImage",
                ["inputs"] = new JsonObject { ["image"] = uploadedRefs[i] },
            };
            loaders.Add(id);
        }
        var refInputs = Inputs(g, NodeRef2V);
        foreach (var key in refInputs.Select(kv => kv.Key).Where(k => k.StartsWith("ref_images.ref_image_")).ToList())
            refInputs.Remove(key);
        for (var i = 0; i < loaders.Count; i++) refInputs[$"ref_images.ref_image_{i}"] = new JsonArray(loaders[i], 0);
        refInputs["ref_image_size"] = "match";

        Workflows.Set(g, NodePrompt, "value", prompt);
        Workflows.Set(g, NodeSeconds, "value", Math.Clamp(seconds, 1, MaxSeconds));
        Workflows.Set(g, NodeSeed, "noise_seed", seed);

        // ResolutionSelector sizes the sampled draft: a quarter of the finished area.
        Workflows.Set(g, NodeResolution, "aspect_ratio", aspect);
        Workflows.Set(g, NodeResolution, "megapixels", FinishedMegapixels / (UpscaleFactor * UpscaleFactor));
        Workflows.Set(g, NodeResolution, "multiple", 32);

        foreach (var sla in new[] { "sla_base", "sla_loop" })
        {
            Workflows.Set(g, sla, "enabled", true);
            Workflows.Set(g, sla, "sparsity_ratio", 0.85);
            Workflows.Set(g, sla, "block_size", "64"); // 128 rows = 1.6 s of audio per block: robotic speech
        }
        Workflows.Set(g, NodeSolAttn, "switch", false);

        Workflows.Set(g, NodeTurboLora, "model", new JsonArray(NodeSolAttn, 0));
        Workflows.Set(g, NodeSigmaShift, "model", new JsonArray(NodeTurboLora, 0));
        Workflows.Set(g, NodeSigmaShift, "shift_video", 12);
        Workflows.Set(g, NodeSigmaShift, "shift_audio", 3);
        Workflows.Set(g, NodeSpectrum, "model", new JsonArray(NodeSigmaShift, 0));
        foreach (var p in NodePreview) Workflows.Set(g, p, "model", new JsonArray(NodeSpectrum, 0));

        // Draft (4 of 8 unshifted steps) → 2× latent upscale → 3-step finish.
        Workflows.Set(g, NodeUpscaler, "mode.scale", UpscaleFactor);
        Workflows.Set(g, NodeDetailSwitch, "switch", true);
        Workflows.Set(g, NodeSampler, "sigmas", new JsonArray(NodeDraftSigmas, 0));
        Workflows.Set(g, NodeRtxSwitch, "switch", false);
        Workflows.Set(g, NodeAudioSwitch, "switch", true);

        Workflows.Set(g, NodeSink, "filename_prefix", filePrefix);
        Workflows.Set(g, NodeSink, "save_output", true);

        PruneTo(g, NodeSink);
        return g;
    }

    /// <summary>
    /// Keeps only what the sink can reach. Every VHS_VideoCombine is an output node and runs whether
    /// or not anything reads it, so the continuation half has to be deleted, not merely unhooked.
    /// </summary>
    private static void PruneTo(JsonObject g, string sink)
    {
        var keep = new HashSet<string>();
        var stack = new Stack<string>(new[] { sink });
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!keep.Add(id) || g[id]?["inputs"] is not JsonObject inputs) continue;
            foreach (var kv in inputs)
                if (kv.Value is JsonArray { Count: 2 } link && link[0] is JsonValue v && v.TryGetValue<string>(out var src))
                    stack.Push(src);
        }
        foreach (var id in g.Select(kv => kv.Key).Where(k => !keep.Contains(k)).ToList()) g.Remove(id);
    }

    private static JsonObject Inputs(JsonObject g, string id) =>
        g[id]?["inputs"] as JsonObject ?? throw new InvalidOperationException($"The bundled workflow has no node {id}; it has drifted.");

    // ── The script ──────────────────────────────────────────────────────────────────────────────

    public static string SystemPrompt() => Workflows.LoadText("h3-r2va.md");

    /// <summary>
    /// The spec forbids inventing sound, so a request that says nothing about it comes back "N/A" and
    /// the clip is silent in intent. Asking for the place's own sound counts as the user stating it.
    /// </summary>
    public const string SoundRequest =
        "Sound: the user wants the natural ambient sound of the setting in overall_soundscape, and any " +
        "words a character says in the idea below spoken aloud as dialogue. Still no music unless asked.";

    /// <summary>The user message the desktop's Analyze sends, for a single segment.</summary>
    public static string Request(int pictureCount, int seconds, string idea)
    {
        var lines = new List<string> { $"You are given {pictureCount} reference picture(s), in order:" };
        for (var i = 0; i < pictureCount; i++) lines.Add($"  <Picture {i + 1}>");
        lines.Add("");
        lines.Add($"Write ONE segment. Target duration: {seconds} seconds.");
        lines.Add(SoundRequest);
        lines.Add("");
        lines.Add("Draft idea from the user:");
        lines.Add(string.IsNullOrWhiteSpace(idea)
            ? "(none — build a single natural beat out of what the pictures show, and add nothing beyond it)"
            : idea.Trim());
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Takes the model's reply down to the six-field block: no fences, no bold, no leading "Prompt:",
    /// and nothing after a second segment marker should the model have written more than asked.
    /// </summary>
    public static string CleanScript(string reply)
    {
        var s = reply.Replace("**", "");
        s = Regex.Replace(s, @"^```[a-zA-Z]*\s*|```\s*$", "", RegexOptions.Multiline).Trim();
        var start = s.IndexOf("Ref2VA:", StringComparison.OrdinalIgnoreCase);
        if (start > 0) s = s[start..];
        var marker = s.IndexOf("=== SEGMENT", StringComparison.Ordinal);
        if (marker > 0) s = s[..marker];
        return s.Trim();
    }

    /// <summary>
    /// Without an LLM the idea still has to arrive in the six-field shape the model was trained on.
    /// Every picture is bound as a subject and the idea goes, word for word, into the timeline.
    /// </summary>
    public static string ScriptWithoutLlm(int pictureCount, int seconds, string idea)
    {
        var subjects = string.Join("\n", Enumerable.Range(1, pictureCount)
            .Select(i => $"<Subject {i}> is what <Picture {i}> shows."));
        var what = string.IsNullOrWhiteSpace(idea) ? "The scene in the pictures comes to life." : idea.Trim();
        return $"""
            Ref2VA:

            subject_definitions:
            {subjects}

            summary:
            {what}

            retention_analysis:
            Appearance, clothing and setting are carried over from the pictures.

            detailed_description:
            0.00s-{seconds:0.00}s: {what}

            overall_soundscape:
            Natural ambient sound of the setting.

            non_diegetic_music:
            None.
            """;
    }
}

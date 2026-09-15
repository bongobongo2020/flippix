using System.IO;
using System.Text.Json.Nodes;

namespace FlipPix.Tests;

/// <summary>
/// The contract between <c>workflow/video/h3-minimax/h3-taomate.json</c> and ⚡ H3 Express's 🍥 TaoMate
/// stack. The graph is generated from the author's export by <c>tools/build_h3_taomate.py</c>, so a
/// re-export — or a hand edit of the built file — can quietly move an id the render path writes to. Every
/// one of those writes is a <c>SetInput</c> or a <c>RequireClass</c> that throws at submit time, on a tab
/// that runs folders of films unattended overnight; these assertions are the same statements, made at
/// build time instead.
///
/// <para>What the render path itself does with the graph — the panels injected per cast member, the seeds,
/// the RIFE relink and the prune — needs a live ComfyUI to validate against and lives in
/// <c>tools/verify_h3_taomate.py</c>.</para>
/// </summary>
public sealed class H3TaoMateWorkflowTests
{
    private static JsonObject Graph()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "workflow", "video", "h3-minimax", "h3-taomate.json");
        Assert.True(File.Exists(path), $"the TaoMate graph is not in the output: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static JsonObject Inputs(JsonObject root, string id)
    {
        Assert.True(root.ContainsKey(id), $"node {id} is not in the graph");
        return root[id]!["inputs"]!.AsObject();
    }

    private static string Class(JsonObject root, string id) =>
        root[id]!["class_type"]!.GetValue<string>();

    [Theory]
    // The ids H3ErosViewModel.ApplyCommonInputs writes to, shared by every stack.
    [InlineData("22:11", "PrimitiveStringMultiline")]
    [InlineData("22:23", "PrimitiveFloat")]
    [InlineData("22:8", "INTConstant")]
    [InlineData("22:9", "ResolutionSelector")]
    [InlineData("5", "MiniMaxH3ReferenceToVideo")]
    [InlineData("171:4", "UNETLoader")]
    [InlineData("21", "Power Lora Loader (rgthree)")]
    // The ids H3ExpressViewModel's TaoMate finish writes to.
    [InlineData("tm:pass1", "ClownsharKSampler_Beta")]
    [InlineData("tm:pass2", "ClownsharKSampler_Beta")]
    [InlineData("tm:rtx", "RTXVideoSuperResolution")]
    [InlineData("190", "VAEDecodeAudio")]
    [InlineData("165", "RIFEInterpolation")]
    [InlineData("34", "VHS_VideoCombine")]
    public void Every_node_the_tab_drives_is_there_with_the_class_it_expects(string id, string expected)
    {
        var root = Graph();
        Assert.True(root.ContainsKey(id), $"node {id} ({expected}) is missing");
        Assert.Equal(expected, Class(root, id));
    }

    [Fact]
    public void The_relay_hands_one_schedule_from_the_base_weights_to_the_taomate_lora()
    {
        var root = Graph();
        var first = Inputs(root, "tm:pass1");
        var second = Inputs(root, "tm:pass2");

        // Pass 1 starts the schedule and stops part-way; pass 2 resamples the rest of it.
        Assert.Equal("standard", first["sampler_mode"]!.GetValue<string>());
        Assert.Equal("resample", second["sampler_mode"]!.GetValue<string>());
        Assert.Equal("tm:pass1", second["latent_image"]!.AsArray()[0]!.GetValue<string>());

        var handoff = first["steps_to_run"]!.GetValue<int>();
        Assert.True(handoff > 0, "pass 1 runs the whole schedule — there is nothing to hand over");
        Assert.Equal(-1, second["steps_to_run"]!.GetValue<int>());

        // Both legs must read the SAME step count, which is the one the tab writes into 22:8: a resample
        // continuing a schedule of a different length is not a continuation of the one pass 1 stopped in.
        foreach (var leg in new[] { first, second })
            Assert.Equal("22:8", leg["steps"]!.AsArray()[0]!.GetValue<string>());
        Assert.True(handoff < Inputs(root, "22:8")["value"]!.GetValue<int>(),
                    "the handoff step is past the end of the schedule");

        // Pass 2's weights are the TaoMate LoRA on the same checkpoint; pass 1's carry the author's
        // attention backend and sigma shift and no LoRA.
        Assert.Equal("tm:tao", second["model"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("LoraLoaderModelOnly", Class(root, "tm:tao"));
        Assert.Contains("taomate", Inputs(root, "tm:tao")["lora_name"]!.GetValue<string>(),
                        StringComparison.OrdinalIgnoreCase);
        Assert.Equal("tm:shift", first["model"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("MiniMaxH3SigmaShift", Class(root, "tm:shift"));
    }

    [Fact]
    public void The_lora_seat_sits_above_the_split_so_a_spliced_lora_reaches_both_legs()
    {
        var root = Graph();

        // Node 21 is what H3ExpressViewModel.ApplyCommonInputs retargets. Both model wires have to come
        // off it, or the tab's LoRA would apply to one leg of the relay and not the other.
        Assert.Equal("171:4", Inputs(root, "21")["model"]!.AsArray()[0]!.GetValue<string>());
        foreach (var wire in new[] { "tm:attn", "tm:tao" })
            Assert.Equal("21", Inputs(root, wire)["model"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public void The_finish_is_a_frame_upscale_the_sink_reads_through_rife()
    {
        var root = Graph();

        Assert.Equal("tm:pass2", Inputs(root, "189")["samples"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("tm:pass2", Inputs(root, "190")["samples"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("189", Inputs(root, "tm:rtx")["images"]!.AsArray()[0]!.GetValue<string>());

        // RIFE after the upscale, as the finish assumes when it relinks the sink past it.
        Assert.Equal("tm:rtx", Inputs(root, "165")["images"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("165", Inputs(root, "34")["images"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("190", Inputs(root, "34")["audio"]!.AsArray()[0]!.GetValue<string>());

        // The factor the tab's finished-size arithmetic and its frame-stack warning are both based on.
        Assert.Equal("scale by multiplier", Inputs(root, "tm:rtx")["resize_type"]!.GetValue<string>());
        Assert.Equal(2.0, Inputs(root, "tm:rtx")["resize_type.scale"]!.GetValue<double>());

        // There must be no latent upscaler: this stack samples at the real canvas, and a stray one would
        // mean the built graph came from the wrong source.
        foreach (var (_, node) in root)
            Assert.NotEqual("MinimaxH3LatentUpscaler3D", node!["class_type"]!.GetValue<string>());
    }

    [Fact]
    public void Nothing_reads_a_node_that_is_not_in_the_graph()
    {
        var root = Graph();
        foreach (var (id, node) in root)
            foreach (var (name, value) in node!["inputs"]!.AsObject())
                if (value is JsonArray { Count: 2 } link && link[0] is JsonValue from &&
                    from.TryGetValue<string>(out var source))
                    Assert.True(root.ContainsKey(source), $"node {id}.{name} reads missing node {source}");
    }

    [Fact]
    public void The_reference_node_ships_no_reference_slots()
    {
        var root = Graph();

        // AttachReferences clears and rewrites ref_images.ref_image_N from the cast's uploaded panels. A
        // slot shipped in the file would be one pointing at a loader this graph does not have.
        foreach (var (name, _) in Inputs(root, "5"))
            Assert.False(name.StartsWith("ref_images.", StringComparison.Ordinal),
                         $"the graph ships {name}, which the tab would have to clear");
        Assert.Equal("22:11", Inputs(root, "5")["prompt"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("22:9", Inputs(root, "5")["width"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("22:24", Inputs(root, "5")["length"]!.AsArray()[0]!.GetValue<string>());
    }
}

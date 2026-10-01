using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using FlipPix.UI.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// 🌊 HyperFlow on both tabs that offer it: <c>h3-hyperflow.json</c> as ⚡ H3 Express submits it, and the
/// patch 🌀 MiniMax I2V writes over <c>h3-minimax-i2v.json</c>.
///
/// <para>What is specific to this stack, and so what is checked: the drafts sample the adapter's own sigma
/// grid rather than a scheduler, the LoRA seat sits <i>below</i> the adapter (a LoRA above it would switch
/// the checkpoint-bound curve refit off), SLA 0.90 is last on every wire, and the upscale pass runs on the
/// TaoMate 3-step LoRA instead of HyperFlow for exactly two steps. On I2V the shipped turbo LoRA, sigma shift
/// and Sol-Attn switch are off the wire.</para>
/// </summary>
public sealed class HyperFlowStackTests
{
    private static JsonObject Load(string file)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax", file);
        Assert.True(File.Exists(path), $"{file} is not in the output: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static JsonObject Inputs(JsonObject root, string id)
    {
        Assert.True(root.ContainsKey(id), $"node {id} is not in the graph");
        return root[id]!["inputs"]!.AsObject();
    }

    private static string Class(JsonObject root, string id) => root[id]!["class_type"]!.GetValue<string>();

    private static (string Node, int Slot) Link(JsonObject root, string id, string input)
    {
        var link = Inputs(root, id)[input]?.AsArray();
        Assert.True(link is { Count: 2 }, $"{id}.{input} is not a link");
        return (link![0]!.GetValue<string>(), link[1]!.GetValue<int>());
    }

    private static List<string> ModelWire(JsonObject root, string from)
    {
        var chain = new List<string>();
        var id = from;
        while (true)
        {
            chain.Add(id);
            if (Inputs(root, id)["model"] is not JsonArray link || link.Count != 2) return chain;
            id = link[0]!.GetValue<string>();
            Assert.True(chain.Count < 64, "the model wire loops back on itself");
        }
    }

    // ── ⚡ H3 Express: h3-hyperflow.json ────────────────────────────────────────────────────────────

    /// <summary>The ids the shared render path writes to, with the classes it expects there.</summary>
    [Theory]
    [InlineData("22:11", "PrimitiveStringMultiline")]
    [InlineData("22:23", "PrimitiveFloat")]
    [InlineData("22:8", "INTConstant")]
    [InlineData("22:9", "ResolutionSelector")]
    [InlineData("5", "MiniMaxH3ReferenceToVideo")]
    [InlineData("171:4", "UNETLoader")]
    [InlineData("21", "Power Lora Loader (rgthree)")]
    [InlineData("242", "LTXVSeparateAVLatent")]
    [InlineData("243", "MinimaxH3LatentUpscaler3D")]
    [InlineData("135:26", "SamplerCustomAdvanced")]
    [InlineData("135:27", "RandomNoise")]
    [InlineData("189", "VAEDecode")]
    [InlineData("190", "VAEDecodeAudio")]
    [InlineData("259", "VAEDecode")]
    [InlineData("258", "VAEDecodeAudio")]
    [InlineData("165", "RIFEInterpolation")]
    [InlineData("34", "VHS_VideoCombine")]
    [InlineData("hf:apply", "ApplyHyperFlowH3")]
    [InlineData("hf:sla", "H3SLAAttention")]
    [InlineData("hf:p2lora", "LoraLoaderModelOnly")]
    [InlineData("hf:p2sla", "H3SLAAttention")]
    [InlineData("hf:p2sigmas", "ManualSigmas")]
    public void Express_graph_carries_the_family_ids(string id, string expected)
    {
        var root = Load("h3-hyperflow.json");
        Assert.True(root.ContainsKey(id), $"node {id} is not in h3-hyperflow.json");
        Assert.Equal(expected, Class(root, id));
    }

    [Theory]
    [InlineData("125:12")]
    [InlineData("133:129")]
    [InlineData("143:139")]
    public void Every_draft_samples_the_adapters_grid(string sampler)
    {
        var root = Load("h3-hyperflow.json");
        Assert.Equal(("hf:apply", 1), Link(root, sampler, "sigmas"));
    }

    /// <summary>Drafts: checkpoint → backend → adapter → the LoRA seat → SLA.</summary>
    [Fact]
    public void Express_draft_wire_puts_the_seat_below_the_adapter_and_sla_last()
    {
        var root = Load("h3-hyperflow.json");
        Assert.Equal(new[] { "hf:sla", "21", "hf:apply", "196", "171:4" }, ModelWire(root, "hf:sla"));
        foreach (var guider in new[] { "125:14", "133:130", "143:140" })
            Assert.Equal(("hf:sla", 0), Link(root, guider, "model"));
    }

    /// <summary>The upscale pass: "replace engine LoRA" — TaoMate on the backend, not HyperFlow, two steps.</summary>
    [Fact]
    public void Express_upscale_pass_runs_two_steps_on_taomate_instead_of_hyperflow()
    {
        var root = Load("h3-hyperflow.json");
        Assert.Equal(new[] { "hf:p2sla", "hf:p2lora", "196", "171:4" }, ModelWire(root, "hf:p2sla"));
        Assert.Equal(("hf:p2sla", 0), Link(root, "135:29", "model"));
        Assert.Equal("H3/taomate_h3_3step_comfy.safetensors",
                     Inputs(root, "hf:p2lora")["lora_name"]!.GetValue<string>());
        Assert.Equal(("hf:p2sigmas", 0), Link(root, "135:26", "sigmas"));
        var sigmas = Inputs(root, "hf:p2sigmas")["sigmas"]!.GetValue<string>().Split(',');
        Assert.Equal(3, sigmas.Length);          // three points, two steps
    }

    [Theory]
    [InlineData("hf:sla")]
    [InlineData("hf:p2sla")]
    public void Express_sla_is_the_recipes_0_90(string id) =>
        Assert.Equal(0.90, Inputs(Load("h3-hyperflow.json"), id)["sparsity_ratio"]!.GetValue<double>());

    [Fact]
    public void Express_adapter_is_the_recipe()
    {
        var root = Load("h3-hyperflow.json");
        var hf = Inputs(root, "hf:apply");
        Assert.True(hf["experimental_curve_refit"]!.GetValue<bool>());
        Assert.Equal("bypass", hf["lora_mode"]!.GetValue<string>());
        Assert.Equal(1.0, hf["strength"]!.GetValue<double>());
        Assert.Equal(H3ExpressViewModel.HyperFlowModel, Inputs(root, "171:4")["unet_name"]!.GetValue<string>());
        Assert.Equal("custom_node_hyperflow_8step_v1.0_comfyui_pruned.safetensors",
                     hf["hyperflow_file"]!.GetValue<string>());
    }

    [Fact]
    public void Express_graph_links_all_resolve()
    {
        var root = Load("h3-hyperflow.json");
        foreach (var (id, node) in root)
            foreach (var (name, value) in node!["inputs"]!.AsObject())
                if (value is JsonArray { Count: 2 } a && a[0] is JsonValue v && v.TryGetValue<string>(out var src))
                    Assert.True(root.ContainsKey(src), $"{id}.{name} points at {src}, which is not in the graph");
    }

    // ── 🌀 MiniMax I2V: the patch ───────────────────────────────────────────────────────────────────

    private static readonly MethodInfo Patch =
        typeof(MiniMaxI2VViewModel).GetMethod("ApplyRenderStack", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MiniMaxI2VViewModel.ApplyRenderStack is gone or is no longer static.");

    private static JsonObject PatchedI2V(params MiniMaxI2VLoraChoice[] loras)
    {
        var root = Load("h3-minimax-i2v.json");
        var item = new MiniMaxI2VQueueItem
        {
            Stack = I2VStack.HyperFlow,
            DiffusionModel = MiniMaxI2VViewModel.ShippedModelFor(I2VStack.HyperFlow),
            FirstPassSteps = MiniMaxI2VViewModel.AuthoredStepsFor(I2VStack.HyperFlow, false),
            Loras = loras.ToList(),
            UpscaleSteps = 4,
            UseLatentUpscale = true,
            ContinuationPrompts = { "a continuation" },
        };
        Patch.Invoke(null, new object[] { root, item, "52", 1234L });
        return root;
    }

    /// <summary>Checkpoint → backend → adapter → Spectrum → the user's LoRA → preview → SLA. No turbo LoRA, no
    /// sigma shift, and the tab's Sol-Attn switch stepped over.</summary>
    [Theory]
    [InlineData("sla_base", "39")]
    [InlineData("sla_loop", "51")]
    public void I2V_wire_runs_through_the_adapter_with_the_lora_below_it(string sla, string preview)
    {
        var root = PatchedI2V(new MiniMaxI2VLoraChoice { Name = "H3/example.safetensors", Strength = 0.8 });
        Assert.Equal(new[] { sla, preview, "i2v_lora_0", "55:3705", "hf_apply", "55:3703", "55:3701" },
                     ModelWire(root, sla));
    }

    [Theory]
    [InlineData("base", new[] { "4145:4223", "4145:4215" })]
    [InlineData("loop", new[] { "4146:4236", "4146:4237" })]
    public void I2V_upscale_pass_runs_on_taomate_instead_of_hyperflow(string tag, string[] guiders)
    {
        var root = PatchedI2V();
        var sla = $"hf_p2sla_{tag}";
        Assert.Equal(new[] { sla, "hf_p2lora", "55:3703", "55:3701" }, ModelWire(root, sla));
        foreach (var guider in guiders) Assert.Equal((sla, 0), Link(root, guider, "model"));
        Assert.Equal(Inputs(Load("h3-hyperflow.json"), "hf:p2sigmas")["sigmas"]!.GetValue<string>(),
                     Inputs(root, "finish_sigmas")["sigmas"]!.GetValue<string>());
    }

    [Fact]
    public void I2V_draft_cuts_the_adapters_grid_in_half()
    {
        var root = PatchedI2V();
        Assert.Equal(("hf_apply", 1), Link(root, "draft_split", "sigmas"));
        Assert.Equal(4, Inputs(root, "draft_split")["step"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("sla_base")]
    [InlineData("sla_loop")]
    [InlineData("hf_p2sla_base")]
    [InlineData("hf_p2sla_loop")]
    public void I2V_forces_sla_on_at_0_90(string sla)
    {
        var inputs = Inputs(PatchedI2V(), sla);
        Assert.True(inputs["enabled"]!.GetValue<bool>());
        Assert.Equal(0.90, inputs["sparsity_ratio"]!.GetValue<double>());
    }

    [Fact]
    public void I2V_adapter_has_the_express_graphs_settings()
    {
        var i2v = Inputs(PatchedI2V(), "hf_apply");
        var express = Inputs(Load("h3-hyperflow.json"), "hf:apply");
        foreach (var key in new[] { "hyperflow_file", "strength", "lora_mode", "variant", "experimental_curve_refit" })
            AssertSame(key, express[key], i2v[key]);

        var i2vLora = Inputs(PatchedI2V(), "hf_p2lora");
        var expressLora = Inputs(Load("h3-hyperflow.json"), "hf:p2lora");
        foreach (var key in new[] { "lora_name", "strength_model" })
            AssertSame(key, expressLora[key], i2vLora[key]);
    }

    /// <summary>Equal as values: the file says <c>1.0</c> where C# writes <c>1</c>.</summary>
    private static void AssertSame(string key, JsonNode? expected, JsonNode? actual)
    {
        Assert.NotNull(actual);
        static double? Number(JsonNode n) =>
            double.TryParse(n.ToJsonString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

        if (Number(expected!) is { } ed)
            Assert.True(Number(actual!) == ed, $"{key}: expected {ed}, got {actual!.ToJsonString()}");
        else
            Assert.Equal(expected!.ToJsonString(), actual!.ToJsonString());
    }
}

using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using FlipPix.UI.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// The contract between <c>workflow/video/h3-minimax/h3-minimax-i2v.json</c> and 🌀 MiniMax I2V's render
/// card.
///
/// <para>Unlike ⚡ H3 Express, where a stack is a second workflow file, every stack on this tab is a
/// <i>patch</i> applied to the one graph — so the thing worth testing is not the file's node ids alone but
/// what <c>ApplyRenderStack</c> leaves behind: a model wire that still reaches the samplers, a relay whose
/// second leg is the end of the chain rather than the middle of it, and no link pointing at a node that is
/// not there. All of that is decided without a GPU, a ComfyUI or an LLM, which is what makes it testable
/// here rather than in <c>tools/</c>.</para>
///
/// <para>The patch is reached by reflection: it is a private static of a WPF view model, and going through
/// the view model itself would want a Dispatcher, a settings service and a live server for nothing.</para>
/// </summary>
public sealed class MiniMaxI2VStackTests
{
    private const string Sink = "52";              // VHS_VideoCombine, base pass + continuations
    private const string SinkSingle = "49";        // VHS_VideoCombine, base pass only

    private static string GraphPath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax", "h3-minimax-i2v.json");

    private static JsonObject Graph()
    {
        Assert.True(File.Exists(GraphPath), $"the MiniMax I2V graph is not in the output: {GraphPath}");
        return JsonNode.Parse(File.ReadAllText(GraphPath))!.AsObject();
    }

    private static readonly MethodInfo Patch =
        typeof(MiniMaxI2VViewModel).GetMethod("ApplyRenderStack",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MiniMaxI2VViewModel.ApplyRenderStack is gone or is no " +
                                              "longer static — the tests below cannot reach it.");

    private static JsonObject Patched(I2VStack stack, params MiniMaxI2VLoraChoice[] loras)
    {
        var root = Graph();
        var item = new MiniMaxI2VQueueItem
        {
            Stack = stack,
            DiffusionModel = MiniMaxI2VViewModel.ShippedModelFor(stack),
            FirstPassSteps = MiniMaxI2VViewModel.AuthoredStepsFor(stack, false),
            Loras = loras.ToList(),
            UpscaleSteps = 4,
            UseLatentUpscale = stack != I2VStack.TaoMate,
            ContinuationPrompts = { "a continuation" },
        };
        Patch.Invoke(null, new object[] { root, item, Sink, 1234L });
        return root;
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

    /// <summary>Walks a model input back up the wire until it reaches a node with none — the checkpoint.</summary>
    private static List<string> ModelWire(JsonObject root, string from)
    {
        var chain = new List<string>();
        var id = from;
        while (true)
        {
            chain.Add(id);
            var inputs = Inputs(root, id);
            if (inputs["model"] is not JsonArray link || link.Count != 2) return chain;
            id = link[0]!.GetValue<string>();
            Assert.True(chain.Count < 64, "the model wire loops back on itself");
        }
    }

    // ── The ids the patch writes to, as the file has them ───────────────────────────────────────────

    [Theory]
    [InlineData("55:3701", "DiffusionModelLoaderKJ")]   // the checkpoint
    [InlineData("55:3703", "ModelAttentionBackend")]
    [InlineData("55:3706", "ComfySwitchNode")]          // the Sol-Attn switch the wire is built from
    [InlineData("55:3690", "LoraLoaderModelOnly")]      // the lightx2v turbo LoRA
    [InlineData("55:3704", "MiniMaxH3SigmaShift")]
    [InlineData("55:3705", "SpectrumApplyMiniMaxH3")]
    [InlineData("39", "ModelPreviewOverrideKJ")]
    [InlineData("51", "ModelPreviewOverrideKJ")]
    [InlineData("sla_base", "H3SLAAttention")]
    [InlineData("sla_loop", "H3SLAAttention")]
    [InlineData("draft_sched", "BasicScheduler")]
    [InlineData("draft_split", "SplitSigmas")]
    [InlineData("finish_sigmas", "ManualSigmas")]
    [InlineData("4145:148", "BasicScheduler")]          // the base pass's whole schedule
    [InlineData("4146:104", "BasicScheduler")]          // the loop's
    [InlineData("4145:145", "KSamplerSelect")]
    [InlineData("4146:71", "KSamplerSelect")]
    [InlineData("4145:140", "SamplerCustomAdvanced")]   // the base pass
    [InlineData("4146:92", "SamplerCustomAdvanced")]    // the loop's
    [InlineData("4145:135", "ConditioningZeroOut")]
    [InlineData("4146:67", "ConditioningZeroOut")]
    [InlineData("4146:121", "MiniMaxH3AddGuide")]
    [InlineData("4146:126", "easy forLoopStart")]
    [InlineData("4145:4223", "BasicGuider")]
    [InlineData("4145:4215", "CFGGuider")]
    [InlineData("4146:4236", "BasicGuider")]
    [InlineData("4146:4237", "CFGGuider")]
    public void The_graph_still_has_the_node_the_patch_writes_to(string id, string expected)
    {
        var root = Graph();
        Assert.True(root.ContainsKey(id), $"node {id} is not in h3-minimax-i2v.json");
        Assert.Equal(expected, Class(root, id));
    }

    // ── Every stack leaves a graph whose links all resolve ──────────────────────────────────────────

    [Theory]
    [InlineData(I2VStack.Shipped)]
    [InlineData(I2VStack.Eros)]
    [InlineData(I2VStack.Singularity)]
    [InlineData(I2VStack.TaoMate)]
    [InlineData(I2VStack.Bunny)]
    public void Every_link_points_at_a_node_that_exists(I2VStack stack)
    {
        var root = Patched(stack,
            new MiniMaxI2VLoraChoice { Name = "H3/example.safetensors", Strength = 0.8 });

        foreach (var node in root)
        {
            if (node.Value?["inputs"] is not JsonObject inputs) continue;
            foreach (var input in inputs)
            {
                if (input.Value is not JsonArray link || link.Count != 2) continue;
                if (link[0] is not JsonValue v || !v.TryGetValue<string>(out var source)) continue;
                Assert.True(root.ContainsKey(source),
                            $"{stack}: {node.Key}.{input.Key} points at {source}, which is not in the graph");
            }
        }
    }

    [Theory]
    [InlineData(I2VStack.Shipped)]
    [InlineData(I2VStack.Eros)]
    [InlineData(I2VStack.Singularity)]
    [InlineData(I2VStack.TaoMate)]
    [InlineData(I2VStack.Bunny)]
    public void Both_sinks_still_reach_the_checkpoint(I2VStack stack)
    {
        var root = Patched(stack);
        foreach (var sink in new[] { Sink, SinkSingle })
        {
            var seen = new HashSet<string>();
            var stack_ = new Stack<string>();
            stack_.Push(sink);
            while (stack_.Count > 0)
            {
                var id = stack_.Pop();
                if (!seen.Add(id)) continue;
                if (root[id]?["inputs"] is not JsonObject inputs) continue;
                foreach (var input in inputs)
                    if (input.Value is JsonArray link && link.Count == 2 &&
                        link[0] is JsonValue v && v.TryGetValue<string>(out var src))
                        stack_.Push(src);
            }
            Assert.True(seen.Contains("55:3701"),
                        $"{stack}: sink {sink} no longer reaches the checkpoint");
        }
    }

    // ── The model wire ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shipped_keeps_the_turbo_lora_and_the_12_3_shift()
    {
        var root = Patched(I2VStack.Shipped);
        var wire = ModelWire(root, "sla_base");

        Assert.Contains("55:3690", wire);                       // the turbo LoRA
        Assert.Contains("55:3704", wire);                       // the sigma shift
        Assert.Equal(12, Inputs(root, "55:3704")["shift_video"]!.GetValue<double>());
        Assert.Equal(3, Inputs(root, "55:3704")["shift_audio"]!.GetValue<double>());
        Assert.Equal("euler", Inputs(root, "4145:145")["sampler_name"]!.GetValue<string>());
        Assert.Equal("simple", Inputs(root, "draft_sched")["scheduler"]!.GetValue<string>());
    }

    [Fact]
    public void Eros_takes_the_turbo_lora_and_the_shift_off_the_wire()
    {
        var root = Patched(I2VStack.Eros);
        var wire = ModelWire(root, "sla_base");

        Assert.DoesNotContain("55:3690", wire);
        Assert.DoesNotContain("55:3704", wire);
        Assert.Equal("er_sde", Inputs(root, "4145:145")["sampler_name"]!.GetValue<string>());
        Assert.Equal("er_sde", Inputs(root, "4146:71")["sampler_name"]!.GetValue<string>());
        Assert.Equal("beta", Inputs(root, "4145:148")["scheduler"]!.GetValue<string>());
        Assert.Equal(12, Inputs(root, "4145:148")["steps"]!.GetValue<int>());
    }

    [Fact]
    public void Singularity_keeps_the_shift_but_not_the_turbo_lora()
    {
        var root = Patched(I2VStack.Singularity);
        var wire = ModelWire(root, "sla_base");

        Assert.DoesNotContain("55:3690", wire);
        Assert.Contains("55:3704", wire);
        Assert.Equal("euler", Inputs(root, "4145:145")["sampler_name"]!.GetValue<string>());
        Assert.Equal(10, Inputs(root, "4146:104")["steps"]!.GetValue<int>());
    }

    [Fact]
    public void The_users_loras_land_on_the_wire_in_order_and_zero_strength_rows_do_not()
    {
        var root = Patched(I2VStack.Shipped,
            new MiniMaxI2VLoraChoice { Name = "H3/first.safetensors", Strength = 1.0 },
            new MiniMaxI2VLoraChoice { Name = "H3/muted.safetensors", Strength = 0.0 },
            new MiniMaxI2VLoraChoice { Name = "H3/second.safetensors", Strength = 0.5 });

        var wire = ModelWire(root, "sla_base");
        var loaded = wire
            .Where(id => Class(root, id) == "LoraLoaderModelOnly")
            .Select(id => Inputs(root, id)["lora_name"]!.GetValue<string>())
            .ToList();

        Assert.Contains("H3/first.safetensors", loaded);
        Assert.Contains("H3/second.safetensors", loaded);
        Assert.DoesNotContain("H3/muted.safetensors", loaded);

        // Walked from the sampler back to the checkpoint, so the list is in reverse load order.
        Assert.True(loaded.IndexOf("H3/second.safetensors") < loaded.IndexOf("H3/first.safetensors"),
                    "the LoRA rows are not stacked in the order they are listed");
    }

    [Theory]
    [InlineData(I2VStack.Shipped)]
    [InlineData(I2VStack.Eros)]
    [InlineData(I2VStack.Singularity)]
    [InlineData(I2VStack.TaoMate)]
    [InlineData(I2VStack.Bunny)]
    public void The_five_shipped_lora_seats_are_left_unreferenced(I2VStack stack)
    {
        var root = Patched(stack, new MiniMaxI2VLoraChoice { Name = "H3/mine.safetensors", Strength = 1.0 });
        var seats = new[] { "4361:4351", "4361:4352", "4361:4353", "4361:4354", "4361:4355",
                            "4361:4356", "4361:4357", "4361:4358", "4361:4359", "4361:4360" };

        foreach (var node in root)
        {
            if (node.Value?["inputs"] is not JsonObject inputs) continue;
            if (seats.Contains(node.Key)) continue;      // the seats still point at each other; the prune takes them
            foreach (var input in inputs)
                if (input.Value is JsonArray link && link.Count == 2 &&
                    link[0] is JsonValue v && v.TryGetValue<string>(out var src))
                    Assert.False(seats.Contains(src),
                                 $"{stack}: {node.Key}.{input.Key} still reads shipped LoRA seat {src}");
        }
    }

    // ── 🐰 BUNNY ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("base", "4145:140")]
    [InlineData("loop", "4146:92")]
    public void Bunny_makes_the_branchs_sampler_stage_one_of_two(string tag, string sampler)
    {
        var root = Patched(I2VStack.Bunny);
        var stage2 = $"bn_stage2_{tag}";

        Assert.Equal("SamplerCustomAdvanced", Class(root, stage2));

        // Stage 1 runs the top of the split schedule; stage 2 runs the tail out on DisableNoise.
        Assert.Equal(($"bn_split_{tag}", 0), Link(root, sampler, "sigmas"));
        Assert.Equal(($"bn_split_{tag}", 1), Link(root, stage2, "sigmas"));
        Assert.Equal((sampler, 0), Link(root, stage2, "latent_image"));
        Assert.Equal("DisableNoise", Class(root, Link(root, stage2, "noise").Node));

        // The split is cut out of the whole schedule with three steps woven into the mid sigmas.
        Assert.Equal(("bn_ext_" + tag, 0), Link(root, $"bn_split_{tag}", "sigmas"));
        Assert.Equal("ExtendIntermediateSigmas", Class(root, $"bn_ext_{tag}"));
        Assert.Equal(0.25, Inputs(root, $"bn_split_{tag}")["denoise"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("base", "4145:140", "4145:4311")]
    [InlineData("loop", "4146:92", "4146:4312")]
    public void Bunny_hands_the_take_to_the_finish_from_stage_two(string tag, string sampler, string separate)
    {
        var root = Patched(I2VStack.Bunny);
        var stage2 = $"bn_stage2_{tag}";

        // Everything that read the branch's sampler now reads the cleanup — including the separator that
        // takes the denoised output off slot 1, which is the one a slot-blind retarget would have broken.
        Assert.Equal((stage2, 1), Link(root, separate, "av_latent"));
        Assert.NotEqual(sampler, Link(root, separate, "av_latent").Node);
    }

    [Fact]
    public void Bunny_samples_the_action_at_full_strength_and_the_cleanup_at_0_65()
    {
        var root = Patched(I2VStack.Bunny);

        var action = ModelWire(root, "sla_base")
            .Where(id => Class(root, id) == "LoraLoaderModelOnly")
            .Select(id => (Inputs(root, id)["lora_name"]!.GetValue<string>(),
                           Inputs(root, id)["strength_model"]!.GetValue<double>()))
            .ToList();
        Assert.Contains(("H3/H3_Combat_V2.safetensors", 1.0), action);

        Assert.Equal(0.65, Inputs(root, "bn_cleanup_base")["strength_model"]!.GetValue<double>());
        Assert.Equal("H3/H3_Combat_V2.safetensors",
                     Inputs(root, "bn_cleanup_base")["lora_name"]!.GetValue<string>());

        // The latent-upscale finish reads the cleanup wire, as h3-bunny.json's own finish sampler does.
        Assert.Equal(("bn_sla_base", 0), Link(root, "4145:4223", "model"));
        Assert.Equal(("bn_sla_base", 0), Link(root, "4145:4215", "model"));
    }

    // ── 🍥 TaoMate ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("base")]
    [InlineData("loop")]
    public void TaoMate_relays_one_schedule_across_two_clownshar_legs(string tag)
    {
        var root = Patched(I2VStack.TaoMate);

        Assert.Equal("ClownsharKSampler_Beta", Class(root, $"tm_leg1_{tag}"));
        Assert.Equal("ClownsharKSampler_Beta", Class(root, $"tm_leg2_{tag}"));

        var leg1 = Inputs(root, $"tm_leg1_{tag}");
        var leg2 = Inputs(root, $"tm_leg2_{tag}");

        Assert.Equal("linear/euler", leg1["sampler_name"]!.GetValue<string>());
        Assert.Equal("beta57", leg1["scheduler"]!.GetValue<string>());
        Assert.Equal(6, leg1["steps_to_run"]!.GetValue<int>());
        Assert.Equal("standard", leg1["sampler_mode"]!.GetValue<string>());

        Assert.Equal(-1, leg2["steps_to_run"]!.GetValue<int>());
        Assert.Equal("resample", leg2["sampler_mode"]!.GetValue<string>());
        Assert.Equal(($"tm_leg1_{tag}", 0), Link(root, $"tm_leg2_{tag}", "latent_image"));

        // Leg 2 reads the TaoMate LoRA, and that wire has neither the attention backend nor the shift —
        // which is how h3-taomate.json wires it.
        var legWire = ModelWire(root, Link(root, $"tm_leg2_{tag}", "model").Node);
        Assert.Contains(legWire, id => Class(root, id) == "LoraLoaderModelOnly" &&
                                       Inputs(root, id)["lora_name"]!.GetValue<string>()
                                           .Contains("taomate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("55:3703", legWire);
        Assert.DoesNotContain("55:3704", legWire);

        // Leg 1 does have both.
        var firstWire = ModelWire(root, Link(root, $"tm_leg1_{tag}", "model").Node);
        Assert.Contains("55:3704", firstWire);
        Assert.Equal(6, Inputs(root, "55:3704")["shift_audio"]!.GetValue<double>());
    }

    [Fact]
    public void TaoMate_puts_leg_two_where_the_branchs_sampler_was()
    {
        var root = Patched(I2VStack.TaoMate);

        // Clownshar hands back one latent, so the readers that took the denoised output off slot 1 are
        // moved to slot 0 rather than to an output that does not exist.
        Assert.Equal(("tm_leg2_base", 0), Link(root, "4145:4311", "av_latent"));
        Assert.Equal(("tm_leg2_loop", 0), Link(root, "4146:4312", "av_latent"));
        Assert.Equal(("tm_leg2_base", 0), Link(root, "4145:4220", "on_false"));
    }

    [Fact]
    public void TaoMates_continuation_seed_is_indexed_off_the_loop_counter()
    {
        var root = Patched(I2VStack.TaoMate);

        // The base pass takes one number; the loop cannot, so the same index switch the prompt and duration
        // use is rebuilt over three literals.
        Assert.Equal(1234, Inputs(root, "tm_leg1_base")["seed"]!.GetValue<long>());
        Assert.Equal(("tm_loop_seed", 0), Link(root, "tm_leg1_loop", "seed"));
        Assert.Equal(("4146:126", 1), Link(root, "tm_loop_seed", "index"));
        Assert.Equal(1235, Inputs(root, "tm_loop_seed_0")["value"]!.GetValue<long>());
        Assert.Equal(1237, Inputs(root, "tm_loop_seed_2")["value"]!.GetValue<long>());
    }

    // ── The finish ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_upscale_step_count_picks_its_own_sigmas()
    {
        Assert.Equal("0.9035, 0.8000, 0.6316, 0.3158, 0.0000",
                     Inputs(Patched(I2VStack.Shipped), "finish_sigmas")["sigmas"]!.GetValue<string>());
    }

    [Fact]
    public void The_draft_cut_moves_with_the_step_count()
    {
        // draft_split takes the top half of the schedule, so a 12-step stack cuts at 6, not at the 4 the
        // file ships — or the draft would be handed more of the denoising than it was tuned for.
        Assert.Equal(6, Inputs(Patched(I2VStack.Eros), "draft_split")["step"]!.GetValue<int>());
        Assert.Equal(4, Inputs(Patched(I2VStack.Shipped), "draft_split")["step"]!.GetValue<int>());
    }

    [Fact]
    public void Rife_goes_in_front_of_the_saved_sink_only()
    {
        var root = Graph();
        var before = Link(root, Sink, "images");
        var item = new MiniMaxI2VQueueItem
        {
            Stack = I2VStack.Shipped,
            FirstPassSteps = 8,
            UseRife = true,
        };
        Patch.Invoke(null, new object[] { root, item, Sink, 7L });

        Assert.Equal("RIFEInterpolation", Class(root, "i2v_rife"));
        Assert.Equal(("i2v_rife", 0), Link(root, Sink, "images"));
        Assert.Equal(before, Link(root, "i2v_rife", "images"));
        Assert.Equal(48, Inputs(root, Sink)["frame_rate"]!.GetValue<int>());

        // The other sink is untouched: a continuation is built from the frames the base pass produced, and
        // interpolating those would hand the loop twice the frames at the wrong rate.
        Assert.NotEqual(("i2v_rife", 0), Link(root, SinkSingle, "images"));
        Assert.Equal(24, Inputs(root, SinkSingle)["frame_rate"]!.GetValue<int>());
    }

    [Fact]
    public void Rife_is_not_added_unless_it_is_asked_for()
    {
        Assert.False(Patched(I2VStack.Shipped).ContainsKey("i2v_rife"));
    }

    // ── The card's own arithmetic ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(I2VStack.Shipped, 8)]
    [InlineData(I2VStack.Eros, 12)]
    [InlineData(I2VStack.Singularity, 10)]
    [InlineData(I2VStack.TaoMate, 10)]
    [InlineData(I2VStack.Bunny, 8)]
    public void Each_stack_is_authored_at_its_own_step_count(I2VStack stack, int expected) =>
        Assert.Equal(expected, MiniMaxI2VViewModel.AuthoredStepsFor(stack, erSde: false));

    [Fact]
    public void The_singularity_er_sde_option_is_authored_at_the_eros_count() =>
        Assert.Equal(12, MiniMaxI2VViewModel.AuthoredStepsFor(I2VStack.Singularity, erSde: true));

    [Theory]
    [InlineData(I2VStack.Shipped, "minimax_h3_ref2va_pruned_int8_convrot")]
    [InlineData(I2VStack.Eros, "10Eros_Max_h3_TURBO-hybrid_beta4_int8_convrot")]
    [InlineData(I2VStack.Singularity, "Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8")]
    [InlineData(I2VStack.TaoMate, "minimax_h3_fl2va_pruned_int8_convrot")]
    [InlineData(I2VStack.Bunny, "minimax_h3_hybrid_fl2va_ref2va_b25-49-int8")]
    public void Each_stack_names_the_checkpoint_its_express_graph_loads(I2VStack stack, string file) =>
        Assert.Equal($"h3-minimax/{file}.safetensors", MiniMaxI2VViewModel.ShippedModelFor(stack));

    /// <summary>The checkpoints named above are the ones the Express graphs really load, read out of those
    /// files rather than trusted to a copy-paste.</summary>
    [Theory]
    [InlineData(I2VStack.Eros, "h3-eros.json")]
    [InlineData(I2VStack.Singularity, "h3-singularity.json")]
    [InlineData(I2VStack.TaoMate, "h3-taomate.json")]
    [InlineData(I2VStack.Bunny, "h3-bunny.json")]
    public void And_that_checkpoint_is_the_one_that_graph_ships(I2VStack stack, string file)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "workflow", "video", "h3-minimax", file);
        Assert.True(File.Exists(path), $"{file} is not in the output");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        var unet = root["171:4"]?["inputs"]?["unet_name"]?.GetValue<string>();
        Assert.Equal(MiniMaxI2VViewModel.ShippedModelFor(stack), unet);
    }
}

using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// The contract between 🌀 MiniMax I2V's 💎 All-In-One Ultra workflow and the graph it was tested on.
///
/// <para>The authored file (<c>Minimax H3 All-In-One Ultra (Full-Res Tiled).json</c>) is what was run on
/// the server for best quality, so the strongest statement worth making is that the card's defaults, sent
/// through the tab's builder, produce <i>exactly</i> that graph: every widget value the author left, every
/// link of the model wire, and nothing else. The rest checks that each dial reaches the node it names and
/// that no option leaves a link pointing at a node that is not there.</para>
/// </summary>
public sealed class MiniMaxI2VUltraTests
{
    private static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax");

    private static string ApiJson()
    {
        var path = Path.Combine(Dir, "h3-minimax-ultra.json");
        Assert.True(File.Exists(path), $"the Ultra graph is not in the output: {path}");
        return File.ReadAllText(path);
    }

    private static JsonObject Authored()
    {
        var path = Path.Combine(Dir, "Minimax H3 All-In-One Ultra (Full-Res Tiled).json");
        Assert.True(File.Exists(path), $"the authored Ultra workflow is not in the output: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static JsonArray Widgets(JsonObject authored, int id) =>
        authored["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<int>() == id)!["widgets_values"]!.AsArray();

    private static readonly MethodInfo Build =
        typeof(MiniMaxI2VViewModel).GetMethod("BuildUltraWorkflow", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MiniMaxI2VViewModel.BuildUltraWorkflow is gone or no longer static.");

    private static JsonObject Built(MiniMaxI2VUltraOptions? options = null, string aspect = "16:9 (Widescreen)",
                                    int pictures = 2, int audios = 0, int videos = 0)
    {
        var item = new MiniMaxI2VQueueItem
        {
            Workflow = I2VWorkflow.Ultra,
            Ultra = options ?? new MiniMaxI2VUltraOptions(),
            Prompt = "a prompt",
            AspectRatio = aspect,
        };
        var args = new object?[]
        {
            ApiJson(), item,
            Enumerable.Range(0, pictures).Select(i => $"pic{i}.png").ToList(),
            Enumerable.Range(0, audios).Select(i => $"aud{i}.wav").ToList(),
            Enumerable.Range(0, videos).Select(i => $"vid{i}.mp4").ToList(),
            4242L, "mmi2v_test", 0,
        };
        var json = (string)Build.Invoke(null, args)!;
        return JsonNode.Parse(json)!.AsObject();
    }

    private static JsonObject Inputs(JsonObject root, string id)
    {
        Assert.True(root.ContainsKey(id), $"node {id} is not in the graph");
        return root[id]!["inputs"]!.AsObject();
    }

    private static string LinkSource(JsonObject root, string id, string input)
    {
        var link = Inputs(root, id)[input] as JsonArray;
        Assert.True(link is { Count: 2 }, $"{id}.{input} is not a link");
        return link![0]!.GetValue<string>();
    }

    /// <summary>Equal as ComfyUI would read them: 12 and 12.0 are the same float widget value.</summary>
    private static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is JsonValue va && b is JsonValue vb &&
            va.TryGetValue<double>(out var da) && vb.TryGetValue<double>(out var db))
            return Math.Abs(da - db) < 1e-9;
        return JsonNode.DeepEquals(a, b);
    }

    private static void NoDanglingLinks(JsonObject root)
    {
        foreach (var (id, node) in root)
            foreach (var (name, value) in node!["inputs"]!.AsObject())
                if (value is JsonArray { Count: 2 } link && link[0] is JsonValue v && v.TryGetValue<string>(out var src))
                    Assert.True(root.ContainsKey(src), $"{id}.{name} links to missing node {src}");
    }

    [Fact]
    public void Defaults_are_the_authored_workflow_values()
    {
        var a = Authored();
        var o = new MiniMaxI2VUltraOptions();

        Assert.Equal(Widgets(a, 100)[0]!.GetValue<string>(), o.DiffusionModel);
        Assert.Equal(Widgets(a, 33)[0]!.GetValue<string>(), o.TextEncoder);
        Assert.Equal(Widgets(a, 112)[0]!.GetValue<string>(), o.AspectRatio);
        Assert.Equal(Widgets(a, 112)[1]!.GetValue<double>(), o.Megapixels, 4);
        Assert.Equal(Widgets(a, 111)[0]!.GetValue<double>(), o.DurationSeconds, 4);
        Assert.Equal(Widgets(a, 121)[4]!.GetValue<string>() == "max", o.MaxFidelityReferences);
        Assert.Equal(Widgets(a, 120)[0]!.GetValue<string>(), o.AttentionBackend);

        var sparse = Widgets(a, 204);
        Assert.Equal(sparse[1]!.GetValue<double>(), o.SparseTau, 4);
        Assert.Equal(sparse[2]!.GetValue<double>(), o.SparseStart, 4);
        Assert.Equal(sparse[3]!.GetValue<double>(), o.SparseEnd, 4);

        Assert.Equal(Widgets(a, 186)[1]!.GetValue<double>(), o.MysticStrength, 4);
        Assert.Equal(Widgets(a, 242)[1]!.GetValue<double>(), o.DmadStrength, 4);
        Assert.Equal(Widgets(a, 215)[1]!.GetValue<double>(), o.CombatStrength, 4);
        Assert.Equal(Widgets(a, 217)[1]!.GetValue<double>(), o.MotionRepairStrength, 4);
        Assert.Equal(Widgets(a, 216)[1]!.GetValue<double>(), o.FightFlowStrength, 4);
        Assert.Equal(Widgets(a, 218)[1]!.GetValue<double>(), o.SpeedSliderStrength, 4);
        Assert.Equal(Widgets(a, 223)[0]!.GetValue<bool>(), o.HighMotionFix);

        Assert.Equal(Widgets(a, 219)[0]!.GetValue<double>(), o.ShiftVideo, 4);
        Assert.Equal(Widgets(a, 219)[1]!.GetValue<double>(), o.ShiftAudio, 4);
        Assert.Equal(Widgets(a, 165)[0]!.GetValue<string>(), o.Scheduler);
        Assert.Equal(Widgets(a, 165)[1]!.GetValue<int>(), o.Steps);

        // widgets_values: seed, control_after_generate, cfg, transition_step, lowres_scale, rho, w_min, w_max,
        // upscaler_model, highres_tiling, upscaler_unload.
        var sl = Widgets(a, 224);
        Assert.Equal(sl[2]!.GetValue<double>(), o.Cfg, 4);
        Assert.Equal(sl[3]!.GetValue<int>(), o.TransitionStep);
        Assert.Equal(sl[4]!.GetValue<double>(), o.LowresScale, 4);
        Assert.Equal(sl[5]!.GetValue<double>(), o.Rho, 4);
        Assert.Equal(sl[6]!.GetValue<double>(), o.WMin, 4);
        Assert.Equal(sl[7]!.GetValue<double>(), o.WMax, 4);
        Assert.Equal(sl[8]!.GetValue<string>(), o.UpscalerModel);
        Assert.Equal(sl[9]!.GetValue<bool>(), o.HighresTiling);
        Assert.Equal(sl[10]!.GetValue<bool>(), o.UpscalerUnload);

        // UniBlockSwap is authored bypassed (mode 4).
        var swap = a["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<int>() == 138)!;
        Assert.Equal(4, swap["mode"]!.GetValue<int>());
        Assert.False(o.LowVramBlockSwap);
        Assert.Equal(Widgets(a, 138)[0]!.GetValue<int>(), o.BlockSwapBlocks);

        Assert.True(o.IsAuthored);
    }

    [Fact]
    public void Defaults_build_the_authored_graph_unchanged()
    {
        var api = JsonNode.Parse(ApiJson())!.AsObject();
        var built = Built();

        // Only the run's own values may differ from the file: prompt, seed, output prefix, the loaders.
        var perRun = new HashSet<(string, string)>
        {
            ("190", "value"), ("224", "seed"), ("163", "filename_prefix"),
        };

        foreach (var (id, node) in api)
        {
            if (id is "215" or "217" or "216" or "218" or "222" or "223")
                continue; // the High motion fix group — off as authored, so the lazy switch never reaches it
            Assert.True(built.ContainsKey(id), $"authored node {id} ({node!["class_type"]}) was dropped");
            foreach (var (name, value) in node!["inputs"]!.AsObject())
            {
                if (perRun.Contains((id, name)) || name.StartsWith("ref_", StringComparison.Ordinal)) continue;
                if (id == "219" && name == "model") continue; // past the switch — see the next assertion
                Assert.True(Same(value, built[id]!["inputs"]![name]),
                            $"{id}.{name}: authored {value?.ToJsonString()}, built {built[id]!["inputs"]![name]?.ToJsonString()}");
            }
        }

        // The switch's false input is DMAD; with the fix off the shift reads DMAD directly.
        Assert.Equal("242", LinkSource(built, "219", "model"));
        Assert.Equal(4242L, Inputs(built, "224")["seed"]!.GetValue<long>());
        NoDanglingLinks(built);
    }

    [Fact]
    public void Model_wire_runs_as_authored()
    {
        var built = Built();
        Assert.Equal("221", LinkSource(built, "224", "model"));
        Assert.Equal("221", LinkSource(built, "165", "model"));
        Assert.Equal("219", LinkSource(built, "221", "model"));
        Assert.Equal("242", LinkSource(built, "219", "model"));
        Assert.Equal("186", LinkSource(built, "242", "model"));
        Assert.Equal("204", LinkSource(built, "186", "model"));
        Assert.Equal("120", LinkSource(built, "204", "model"));
        Assert.Equal("100", LinkSource(built, "120", "model"));
    }

    [Fact]
    public void High_motion_fix_switches_in_the_four_loras_in_authored_order()
    {
        var built = Built(new MiniMaxI2VUltraOptions { HighMotionFix = true });
        Assert.Equal("222", LinkSource(built, "219", "model"));
        Assert.True(Inputs(built, "223")["value"]!.GetValue<bool>());
        Assert.Equal("242", LinkSource(built, "222", "on_false"));
        Assert.Equal("218", LinkSource(built, "222", "on_true"));
        Assert.Equal("216", LinkSource(built, "218", "model"));
        Assert.Equal("217", LinkSource(built, "216", "model"));
        Assert.Equal("215", LinkSource(built, "217", "model"));
        Assert.Equal("242", LinkSource(built, "215", "model"));
        Assert.Equal(3.0, Inputs(built, "218")["strength_model"]!.GetValue<double>(), 4);
        NoDanglingLinks(built);
    }

    [Fact]
    public void Switched_off_nodes_are_stepped_over_and_pruned()
    {
        var built = Built(new MiniMaxI2VUltraOptions { UseSparseAttention = false, UseMystic = false });
        Assert.False(built.ContainsKey("204"));
        Assert.False(built.ContainsKey("186"));
        Assert.Equal("120", LinkSource(built, "242", "model"));
        NoDanglingLinks(built);
    }

    [Fact]
    public void Low_vram_puts_the_block_swap_back_where_it_was_authored()
    {
        var built = Built(new MiniMaxI2VUltraOptions { LowVramBlockSwap = true });
        Assert.Equal("UniBlockSwap", built["138"]!["class_type"]!.GetValue<string>());
        Assert.Equal("100", LinkSource(built, "138", "model"));
        Assert.Equal("138", LinkSource(built, "120", "model"));
        Assert.Equal(-1, Inputs(built, "138")["num_blocks"]!.GetValue<int>());
    }

    [Fact]
    public void References_land_on_the_autogrow_slots_in_order()
    {
        var built = Built(pictures: 9, audios: 2, videos: 1);
        var ref2v = Inputs(built, "121");
        for (var i = 0; i < 9; i++)
        {
            var src = LinkSource(built, "121", $"ref_images.ref_image_{i}");
            Assert.Equal($"pic{i}.png", Inputs(built, src)["image"]!.GetValue<string>());
        }
        Assert.Equal("VHS_LoadAudioUpload", built[LinkSource(built, "121", "ref_audios.ref_audio_1")]!["class_type"]!.GetValue<string>());
        Assert.Equal("VHS_LoadVideo", built[LinkSource(built, "121", "ref_videos.ref_video_0")]!["class_type"]!.GetValue<string>());
        Assert.False(ref2v.ContainsKey("ref_audios.ref_audio_2"));
        Assert.Equal("max", ref2v["ref_image_size"]!.GetValue<string>());
        NoDanglingLinks(built);
    }

    [Fact]
    public void Card_values_reach_their_nodes()
    {
        var o = new MiniMaxI2VUltraOptions
        {
            Steps = 12, Scheduler = "simple", TransitionStep = 7, LowresScale = 0.5, Cfg = 1.5,
            ShiftVideo = 10, ShiftAudio = 4, DurationSeconds = 10, Megapixels = 1.5,
            UpscalerModel = "none", HighresTiling = false, MaxFidelityReferences = false,
        };
        var built = Built(o, aspect: "9:16 (Portrait Widescreen)");
        Assert.Equal(12, Inputs(built, "165")["steps"]!.GetValue<int>());
        Assert.Equal("simple", Inputs(built, "165")["scheduler"]!.GetValue<string>());
        var sl = Inputs(built, "224");
        Assert.Equal(7, sl["transition_step"]!.GetValue<int>());
        Assert.Equal(0.5, sl["lowres_scale"]!.GetValue<double>(), 4);
        Assert.Equal("none", sl["upscaler_model"]!.GetValue<string>());
        Assert.False(sl["highres_tiling"]!.GetValue<bool>());
        Assert.Equal(10.0, Inputs(built, "111")["value"]!.GetValue<double>(), 4);
        Assert.Equal("9:16 (Portrait Widescreen)", Inputs(built, "112")["aspect_ratio"]!.GetValue<string>());
        Assert.Equal("match", Inputs(built, "121")["ref_image_size"]!.GetValue<string>());
    }

    [Fact]
    public void Transition_step_is_kept_below_the_schedule()
    {
        var built = Built(new MiniMaxI2VUltraOptions { Steps = 6, TransitionStep = 9 });
        Assert.Equal(5, Inputs(built, "224")["transition_step"]!.GetValue<int>());
    }

    [Fact]
    public void A_stereo_aspect_is_fed_as_literal_dimensions()
    {
        var built = Built(aspect: "2:1 (Stereo SBS)");
        Assert.False(built.ContainsKey("112"));
        Assert.Equal("PrimitiveInt", built[LinkSource(built, "121", "width")]!["class_type"]!.GetValue<string>());
        NoDanglingLinks(built);
    }

    [Fact]
    public void Options_round_trip_and_clone_independently()
    {
        var o = new MiniMaxI2VUltraOptions { HighMotionFix = true, LowresScale = 0.6000000000000001 };
        Assert.Equal(0.6, o.LowresScale);
        var copy = o.Clone();
        copy.Steps = 20;
        Assert.Equal(10, o.Steps);
        Assert.True(copy.HighMotionFix);

        var json = System.Text.Json.JsonSerializer.Serialize(o);
        var back = System.Text.Json.JsonSerializer.Deserialize<MiniMaxI2VUltraOptions>(json)!;
        Assert.True(back.HighMotionFix);
        o.Reset();
        Assert.True(o.IsAuthored);
    }
}

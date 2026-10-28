using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// The contract between 🌀 MiniMax I2V's 🌹 ErosMax Turbo 2-Pass workflow and the graph it was authored as.
///
/// <para>The card's defaults, sent through the tab's builder, must produce the authored Normal-mode graph:
/// every widget value the author left on the nodes that run, every link of the model wire, and the
/// continuation branch reduced to the false side of its switches. The rest checks that each dial reaches the
/// node it names, that Continue mode keeps the whole continuation branch, and that no option leaves a link
/// pointing at a node that is not there.</para>
/// </summary>
public sealed class MiniMaxI2VTwoPassTests
{
    private static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax");

    private static string ApiJson()
    {
        var path = Path.Combine(Dir, "h3-erosmax-2pass.json");
        Assert.True(File.Exists(path), $"the 2-Pass graph is not in the output: {path}");
        return File.ReadAllText(path);
    }

    private static JsonObject Authored()
    {
        var path = Path.Combine(Dir, "H3_Turbo_2Pass_ErosMax_b7_experimental_refmods_v3 (1).json");
        Assert.True(File.Exists(path), $"the authored 2-Pass workflow is not in the output: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static JsonArray Widgets(JsonObject authored, int id) =>
        authored["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<int>() == id)!["widgets_values"]!.AsArray();

    private static readonly MethodInfo Build =
        typeof(MiniMaxI2VViewModel).GetMethod("BuildTwoPassWorkflow", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MiniMaxI2VViewModel.BuildTwoPassWorkflow is gone or no longer static.");

    private static JsonObject Built(MiniMaxI2VTwoPassOptions? options = null, string aspect = "4:3 (Standard)",
                                    int pictures = 0, int audios = 0, int videos = 0, bool[]? soundtracks = null,
                                    string? continueSource = null)
    {
        var item = new MiniMaxI2VQueueItem
        {
            Workflow = I2VWorkflow.TwoPass,
            TwoPass = options ?? new MiniMaxI2VTwoPassOptions(),
            Prompt = "a prompt",
            AspectRatio = aspect,
        };
        var args = new object?[]
        {
            ApiJson(), item,
            Enumerable.Range(0, pictures).Select(i => $"pic{i}.png").ToList(),
            Enumerable.Range(0, audios).Select(i => $"aud{i}.wav").ToList(),
            Enumerable.Range(0, videos).Select(i => $"vid{i}.mp4").ToList(),
            (soundtracks ?? Enumerable.Repeat(true, videos).ToArray()).ToList(),
            continueSource,
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
        var o = new MiniMaxI2VTwoPassOptions();

        Assert.Equal(Widgets(a, 30)[0]!.GetValue<string>(), o.DiffusionModel);
        Assert.Equal(Widgets(a, 15)[0]!.GetValue<string>(), o.TextEncoder);
        Assert.Equal(Widgets(a, 3)[0]!.GetValue<string>(), o.VideoVae);
        Assert.EndsWith(o.TurboLora, Widgets(a, 36)[0]!.GetValue<string>());
        Assert.Equal(4, a["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<int>() == 36)!["mode"]!.GetValue<int>());
        Assert.False(o.UseTurboLora);

        Assert.Equal(Widgets(a, 20)[0]!.GetValue<string>(), o.AspectRatio);
        Assert.Equal(Widgets(a, 20)[1]!.GetValue<double>(), o.FirstPassMegapixels, 4);
        Assert.Equal(Widgets(a, 23)[0]!.GetValue<double>(), o.FinalMegapixels, 4);
        Assert.Equal(Widgets(a, 17)[0]!.GetValue<double>(), o.DurationSeconds, 4);

        Assert.Equal(Widgets(a, 18)[0]!.GetValue<string>(), o.Sampler);
        Assert.Equal(Widgets(a, 24)[0]!.GetValue<string>(), o.FirstPassScheduler);
        Assert.Equal(Widgets(a, 24)[1]!.GetValue<int>(), o.FirstPassSteps);
        Assert.Equal(Widgets(a, 102)[0]!.GetValue<string>(), o.SecondPassScheduler);
        Assert.Equal(Widgets(a, 102)[1]!.GetValue<int>(), o.SecondPassSteps);
        Assert.Equal(Widgets(a, 102)[2]!.GetValue<double>(), o.SecondPassDenoise, 4);
        Assert.Equal(Widgets(a, 185)[0]!.GetValue<string>(), o.ManualSigmas);
        Assert.Equal(Widgets(a, 38)[0]!.GetValue<double>(), o.ShiftVideo, 4);
        Assert.Equal(Widgets(a, 38)[1]!.GetValue<double>(), o.ShiftAudio, 4);
        Assert.Equal(Widgets(a, 94)[0]!.GetValue<bool>(), o.PreviewOnly);
        Assert.Equal(Widgets(a, 87)[0]!.GetValue<bool>(), o.UseFirstPassAudio);

        var sparse = Widgets(a, 100);
        Assert.Equal(sparse[0]!.GetValue<string>(), o.SparseMethod);
        Assert.Equal(sparse[1]!.GetValue<double>(), o.SparseKeepPercent, 4);
        Assert.Equal(sparse[2]!.GetValue<double>(), o.SparseStart, 4);
        Assert.Equal(sparse[3]!.GetValue<double>(), o.SparseEnd, 4);
        Assert.Equal(sparse[4]!.GetValue<string>(), o.DenseBlocks);
        Assert.Equal(sparse[7]!.GetValue<string>(), o.SinkConditioning);
        Assert.Equal(Widgets(a, 37)[0]!.GetValue<string>(), o.AttentionBackend);

        Assert.Equal(Widgets(a, 39)[1]!.GetValue<double>(), o.RtxScale, 4);
        Assert.Equal(Widgets(a, 39)[2]!.GetValue<string>(), o.RtxQuality);
        Assert.Equal(Widgets(a, 173)[0]!.GetValue<int>(), o.FrameGenMultiplier);
        Assert.Equal(Widgets(a, 155)[2]!.GetValue<string>(), o.FrameGenMode);
        Assert.Equal(Widgets(a, 174)[0]!.GetValue<double>(), o.GrainStrength, 4);
        Assert.Equal(Widgets(a, 178)[0]!.GetValue<string>(), o.EncoderPreset);

        Assert.Equal(Widgets(a, 117)[0]!.GetValue<bool>(), o.PreviousHasAudio);
        Assert.Equal(Widgets(a, 131)[0]!.GetValue<bool>(), o.UseLastFrameReference);
        Assert.Equal(Widgets(a, 144)[0]!.GetValue<bool>(), o.UseSourceAudioGuide);
        Assert.Equal(Widgets(a, 153)[0]!.GetValue<string>(), o.GuideFrames.ToString());
        Assert.Equal(Widgets(a, 103)[0]!.GetValue<bool>(), o.ContinueMode);

        Assert.True(o.IsAuthored);
    }

    [Fact]
    public void Defaults_build_the_authored_normal_graph()
    {
        var api = JsonNode.Parse(ApiJson())!.AsObject();
        var built = Built();

        var perRun = new HashSet<(string, string)>
        {
            ("2", "value"), ("31", "noise_seed"), ("73", "value"), ("74", "value"),
        };
        // Everything a Normal take runs: the models, both passes, decode, RTX and the Normal export.
        var normal = new[]
        {
            "30", "15", "3", "4", "38", "100", "37", "12", "2", "17", "20", "23", "87", "94", "103", "173", "174",
            "31", "18", "24", "102", "39", "40", "73", "74",
            "151:19", "151:11", "151:21", "151:35", "151:26", "151:10", "151:16", "151:29", "151:32",
            "151:95", "151:98", "151:88", "151:25", "151:86", "151:14",
            "152:75", "152:158", "152:176", "152:135", "152:137",
        };
        foreach (var id in normal) Assert.True(built.ContainsKey(id), $"authored node {id} was dropped");
        // …and nothing of the continuation branch.
        foreach (var id in new[] { "151:105", "151:108", "151:109", "151:110", "151:111", "152:114", "152:136", "152:138", "152:150" })
            Assert.False(built.ContainsKey(id), $"continuation node {id} is still in a Normal graph");

        foreach (var (id, node) in built)
        {
            if (!api.ContainsKey(id)) continue;
            foreach (var (name, value) in api[id]!["inputs"]!.AsObject())
            {
                if (perRun.Contains((id, name)) || name.StartsWith("ref_", StringComparison.Ordinal)) continue;
                if (value is JsonArray) continue; // links are checked below, where they were rewired
                Assert.True(Same(value, node!["inputs"]![name]),
                            $"{id}.{name}: authored {value?.ToJsonString()}, built {node!["inputs"]![name]?.ToJsonString()}");
            }
        }

        // The model wire, with the bypassed Turbo LoRA and the empty LoRA stack stepped over.
        Assert.Equal("30", LinkSource(built, "38", "model"));
        Assert.Equal("38", LinkSource(built, "100", "model"));
        Assert.Equal("100", LinkSource(built, "37", "model"));
        Assert.Equal("37", LinkSource(built, "12", "model"));
        Assert.Equal("12", LinkSource(built, "151:29", "model"));
        // Pass 2 reads the same model through the added preview override (display only).
        Assert.Equal("tp_preview2", LinkSource(built, "151:32", "model"));
        Assert.Equal("37", LinkSource(built, "tp_preview2", "model"));

        // No RefMod, no continuation: the conditioning goes straight from Ref2V to both guiders.
        Assert.Equal("151:11", LinkSource(built, "151:29", "conditioning"));
        Assert.Equal("151:11", LinkSource(built, "151:32", "conditioning"));
        // FrameGen at 1× is skipped: the encode reads RTX super resolution.
        Assert.Equal("39", LinkSource(built, "152:135", "images"));
        Assert.Equal("39", LinkSource(built, "151:86", "run_after"));
        Assert.Equal("152:137", LinkSource(built, "40", "video"));
        Assert.Equal("152:75", LinkSource(built, "40", "filename_prefix"));
        Assert.Equal(4242L, Inputs(built, "31")["noise_seed"]!.GetValue<long>());
        Assert.False(Inputs(built, "103")["value"]!.GetValue<bool>());
        Assert.Equal("minimax_i2v/mmi2v_test", Inputs(built, "73")["value"]!.GetValue<string>());
        NoDanglingLinks(built);
    }

    [Fact]
    public void Refining_pass_preview_is_a_copy_of_the_authored_one_and_optional()
    {
        var built = Built();
        Assert.Equal("ModelPreviewOverrideKJ", built["tp_preview2"]!["class_type"]!.GetValue<string>());
        foreach (var (name, value) in Inputs(built, "12"))
            if (name != "model") Assert.True(JsonNode.DeepEquals(value, Inputs(built, "tp_preview2")[name]), name);

        var off = Built(new MiniMaxI2VTwoPassOptions { LivePreviewRefiningPass = false });
        Assert.False(off.ContainsKey("tp_preview2"));
        Assert.Equal("37", LinkSource(off, "151:32", "model"));

        // A preview-only take has no second pass to watch.
        Assert.False(Built(new MiniMaxI2VTwoPassOptions { PreviewOnly = true }).ContainsKey("tp_preview2"));
        NoDanglingLinks(built);
    }

    [Fact]
    public void Text_to_video_needs_no_reference()
    {
        var built = Built();
        Assert.DoesNotContain(Inputs(built, "151:11"), kv => kv.Key.StartsWith("ref_", StringComparison.Ordinal) && kv.Key.Contains('.'));
        Assert.DoesNotContain(built, kv => kv.Value!["class_type"]!.GetValue<string>() is "LoadImage" or "VHS_LoadVideo");
    }

    [Fact]
    public void References_land_on_the_autogrow_slots_with_their_soundtracks()
    {
        var built = Built(pictures: 4, audios: 3, videos: 2, soundtracks: new[] { true, false });
        for (var i = 0; i < 4; i++)
            Assert.Equal($"pic{i}.png", Inputs(built, LinkSource(built, "151:11", $"ref_images.ref_image_{i}"))["image"]!.GetValue<string>());
        var v0 = LinkSource(built, "151:11", "ref_videos.ref_video_0");
        Assert.Equal("VHS_LoadVideo", built[v0]!["class_type"]!.GetValue<string>());
        Assert.Equal(24, Inputs(built, v0)["force_rate"]!.GetValue<int>());
        Assert.Equal(v0, LinkSource(built, "151:11", "ref_video_audios.ref_video_audio_0"));
        Assert.Equal(2, Inputs(built, "151:11")["ref_video_audios.ref_video_audio_0"]![1]!.GetValue<int>());
        Assert.False(Inputs(built, "151:11").ContainsKey("ref_video_audios.ref_video_audio_1")); // silent clip
        Assert.Equal("LoadAudio", built[LinkSource(built, "151:11", "ref_audios.ref_audio_2")]!["class_type"]!.GetValue<string>());
        Assert.False(Inputs(built, "151:11").ContainsKey("ref_images.ref_image_4"));
        NoDanglingLinks(built);
    }

    [Fact]
    public void Continue_mode_keeps_the_whole_continuation_branch()
    {
        var o = new MiniMaxI2VTwoPassOptions { UseLastFrameReference = true, GuideFrames = 39, FrameGenMultiplier = 3 };
        var built = Built(o, pictures: 2, continueSource: "source.mp4");

        Assert.True(Inputs(built, "103")["value"]!.GetValue<bool>());
        Assert.Equal("source.mp4", Inputs(built, "151:105")["video"]!.GetValue<string>());
        Assert.Equal("VHS_LoadVideo", built["151:105"]!["class_type"]!.GetValue<string>());
        Assert.Equal(1, Inputs(built, "153")["value"]!.GetValue<int>());
        Assert.Equal("minimax_i2v", Inputs(built, "147")["value"]!.GetValue<string>());
        // Guides on both passes, behind the Normal/Continue switches.
        Assert.Equal("151:110", LinkSource(built, "151:29", "conditioning"));
        Assert.Equal("151:108", LinkSource(built, "151:110", "on_true"));
        Assert.Equal("151:109", LinkSource(built, "151:111", "on_true"));
        // The source's last frame is the picture after the two loaded.
        Assert.Equal("151:134", LinkSource(built, "151:11", "ref_images.ref_image_2"));
        // Stitched export; FrameGen skipped whatever the multiplier.
        Assert.Equal("152:138", LinkSource(built, "40", "video"));
        Assert.Equal("152:114", LinkSource(built, "152:138", "on_true"));
        Assert.Equal("152:150", LinkSource(built, "40", "filename_prefix"));
        Assert.Equal("39", LinkSource(built, "152:127", "images"));
        Assert.False(built.ContainsKey("155"));
        NoDanglingLinks(built);
    }

    [Fact]
    public void FrameGen_switches_in_above_one_x()
    {
        var built = Built(new MiniMaxI2VTwoPassOptions { FrameGenMultiplier = 2 });
        Assert.Equal("156", LinkSource(built, "152:135", "images"));
        Assert.Equal("155", LinkSource(built, "156", "on_false"));
        Assert.Equal(2, Inputs(built, "173")["value"]!.GetValue<int>());
        NoDanglingLinks(built);
    }

    [Fact]
    public void Loras_turbo_and_refmods_go_on_the_wire()
    {
        var o = new MiniMaxI2VTwoPassOptions
        {
            UseTurboLora = true, TurboLora = "minimax_h3_fl2v_lightx2v_turbo_4step_v0.1_comfy.safetensors",
            Loras = { new MiniMaxI2VLora { Name = "H3/a.safetensors", Strength = 0.7 }, new MiniMaxI2VLora { Name = "", Strength = 1 } },
            RefMods = { new MiniMaxI2VRefModSlot { Mod = "vanellope_example", Strength = 0.8, Components = "Visual" } },
            RefModRetention = 0.7,
        };
        var built = Built(o);

        Assert.Equal("30", LinkSource(built, "36", "model"));
        Assert.Equal("36", LinkSource(built, "101", "model"));
        Assert.Equal("101", LinkSource(built, "38", "model"));
        var stack = Inputs(built, "101");
        Assert.Equal("H3/a.safetensors", stack["lora_1"]!["lora"]!.GetValue<string>());
        Assert.Equal(0.7, stack["lora_1"]!["strength"]!.GetValue<double>(), 4);
        Assert.False(stack.ContainsKey("lora_2"));

        Assert.Equal("151:182", LinkSource(built, "151:29", "conditioning"));
        Assert.Equal("181", LinkSource(built, "151:182", "mods"));
        Assert.Equal("vanellope_example", Inputs(built, "181")["mod_1"]!.GetValue<string>());
        Assert.Equal("Visual", Inputs(built, "181")["components_1"]!.GetValue<string>());
        Assert.Equal("(none)", Inputs(built, "181")["mod_2"]!.GetValue<string>());
        Assert.Equal(0.7, Inputs(built, "151:182")["retention"]!.GetValue<double>(), 4);
        NoDanglingLinks(built);
    }

    [Fact]
    public void Card_values_reach_their_nodes()
    {
        var o = new MiniMaxI2VTwoPassOptions
        {
            FirstPassSteps = 10, SecondPassSteps = 5, SecondPassDenoise = 0.5, Sampler = "euler",
            FirstPassMegapixels = 0.4, FinalMegapixels = 1.5, DurationSeconds = 8,
            UseFirstPassAudio = false, UseSparseAttention = true, SparseMethod = "sol-attn", SparseTau = 1.5,
            UseRtxUpscale = false, GrainStrength = 0, Crf = 20, EncoderPreset = "slow", MaxFidelityReferences = true,
        };
        var built = Built(o, aspect: "16:9 (Widescreen)");
        Assert.Equal(10, Inputs(built, "24")["steps"]!.GetValue<int>());
        Assert.Equal(5, Inputs(built, "102")["steps"]!.GetValue<int>());
        Assert.Equal(0.5, Inputs(built, "102")["denoise"]!.GetValue<double>(), 4);
        Assert.Equal("euler", Inputs(built, "18")["sampler_name"]!.GetValue<string>());
        Assert.Equal(0.4, Inputs(built, "20")["megapixels"]!.GetValue<double>(), 4);
        Assert.Equal("16:9 (Widescreen)", Inputs(built, "20")["aspect_ratio"]!.GetValue<string>());
        Assert.Equal(1.5, Inputs(built, "23")["value"]!.GetValue<double>(), 4);
        Assert.False(Inputs(built, "94")["value"]!.GetValue<bool>()); // a preview prunes pass 2: H3EditorTwoPassTests
        Assert.False(Inputs(built, "87")["value"]!.GetValue<bool>());
        Assert.Equal("sol-attn", Inputs(built, "100")["selection"]!.GetValue<string>());
        Assert.Equal(1.5, Inputs(built, "100")["selection.tau"]!.GetValue<double>(), 4);
        Assert.False(Inputs(built, "100").ContainsKey("selection.keep_percent"));
        Assert.False(built.ContainsKey("39"));
        Assert.Equal("151:25", LinkSource(built, "152:135", "images"));
        Assert.Equal(20, Inputs(built, "152:135")["crf"]!.GetValue<int>());
        Assert.Equal("slow", Inputs(built, "152:135")["encoder_preset"]!.GetValue<string>());
        Assert.Equal("max", Inputs(built, "151:11")["ref_image_size"]!.GetValue<string>());
        NoDanglingLinks(built);
    }

    [Fact]
    public void Dense_attention_and_manual_sigmas()
    {
        var built = Built(new MiniMaxI2VTwoPassOptions { UseSparseAttention = false, UseManualSigmas = true, ManualSigmas = "0.8, 0.4, 0" });
        Assert.False(built.ContainsKey("100"));
        Assert.Equal("38", LinkSource(built, "37", "model"));
        Assert.Equal("185", LinkSource(built, "151:16", "sigmas"));
        Assert.Equal("0.8, 0.4, 0", Inputs(built, "185")["sigmas"]!.GetValue<string>());
        Assert.False(built.ContainsKey("102"));
        NoDanglingLinks(built);
    }

    [Fact]
    public void A_stereo_aspect_is_fed_as_literal_dimensions()
    {
        var built = Built(aspect: "2:1 (Stereo SBS)");
        Assert.False(built.ContainsKey("20"));
        Assert.Equal("PrimitiveInt", built[LinkSource(built, "151:11", "width")]!["class_type"]!.GetValue<string>());
        NoDanglingLinks(built);
    }

    [Fact]
    public void Turbo_lora_is_resolved_against_the_servers_folders()
    {
        var resolve = typeof(MiniMaxI2VViewModel).GetMethod("ResolveLoraName", BindingFlags.NonPublic | BindingFlags.Static)!;
        var available = new List<string> { "H3/other.safetensors", "minimax_h3_fl2v_lightx2v_turbo_4step_v0.1_comfy.safetensors" };
        Assert.Equal("minimax_h3_fl2v_lightx2v_turbo_4step_v0.1_comfy.safetensors",
                     resolve.Invoke(null, new object[] { available, "Minimax_H3\\minimax_h3_fl2v_lightx2v_turbo_4step_v0.1_comfy.safetensors" }));
        Assert.Equal("H3/other.safetensors", resolve.Invoke(null, new object[] { available, "other.safetensors" }));
        Assert.Null(resolve.Invoke(null, new object[] { available, "missing.safetensors" }));
    }

    [Fact]
    public void Options_round_trip_and_clone_independently()
    {
        var o = new MiniMaxI2VTwoPassOptions { ContinueMode = true, RtxScale = 1.2000000000000002 };
        o.Loras.Add(new MiniMaxI2VLora { Name = "H3/a.safetensors", Strength = 1 });
        o.RefMods.Add(new MiniMaxI2VRefModSlot { Mod = "x" });
        Assert.Equal(1.2, o.RtxScale);

        var copy = o.Clone();
        copy.Loras[0].Strength = 0.5;
        copy.RefMods[0].Strength = 0.3;
        Assert.Equal(1.0, o.Loras[0].Strength);
        Assert.Equal(1.0, o.RefMods[0].Strength);
        Assert.False(o.IsAuthored);

        var json = System.Text.Json.JsonSerializer.Serialize(o);
        var back = System.Text.Json.JsonSerializer.Deserialize<MiniMaxI2VTwoPassOptions>(json)!;
        Assert.False(back.ContinueMode); // a session opens on Normal
        Assert.Equal("x", back.RefMods[0].Mod);
        o.Reset();
        o.Loras.Clear();
        o.RefMods.Clear();
        Assert.True(o.IsAuthored);
    }
}

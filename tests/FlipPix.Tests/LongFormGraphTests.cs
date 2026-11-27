using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.Core.Models;
using FlipPix.UI.Services;
using Xunit;

namespace FlipPix.Tests;

public class LongFormGraphTests
{
    private static LongFormRender Story(int startClip = 1, IReadOnlyList<MiniMaxI2VLora>? loras = null, IReadOnlyList<string>? cast = null) => new()
    {
        Shots = new[]
        {
            new LongFormShot("beat_01", "A crouches by the apples.", 4, 11),
            new LongFormShot("beat_02", "A springs up.", 5, 22),
            new LongFormShot("beat_03", "B stumbles.", 6, 33),
        },
        OpeningImage = "opening.png",
        CastImages = cast ?? Array.Empty<string>(),
        RunName = "fflf_20261127_120000",
        StartClip = startClip,
        DiffusionModel = "h3-minimax/10Eros_Max_SingularityV2_PRISM2_PikaFlex_0.85_int8HQ.safetensors",
        TextEncoder = "qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors",
        Loras = loras ?? new List<MiniMaxI2VLora> { new() { Name = "H3\\AmateurHour.safetensors", Strength = 0.6 } },
        OutputSubfolder = "video/fflf/fflf_20261127_120000",
        Filename = "fflf_20261127_120000_longform",
    };

    private static JsonObject Inputs(JsonObject g, string id) => g[id]!["inputs"]!.AsObject();
    private static string Source(JsonObject g, string id, string input) => Inputs(g, id)[input]!.AsArray()[0]!.GetValue<string>();

    private static void AssertLinksResolve(JsonObject g)
    {
        foreach (var (_, node) in g)
            foreach (var (_, value) in node!["inputs"]!.AsObject())
                if (value is JsonArray link && link.Count == 2 && link[0] is JsonValue v && v.TryGetValue<string>(out var target))
                    Assert.True(g.ContainsKey(target), $"link to missing node {target}");
    }

    private static JsonArray Shots(JsonObject g) =>
        JsonNode.Parse(Inputs(g, "lf_plan")["plan_json"]!.GetValue<string>())!["shots"]!.AsArray();

    [Fact]
    public void Build_IsOneContextLoopChainWithColourStableDrift()
    {
        var g = LongFormGraph.Build(Story());
        AssertLinksResolve(g);

        Assert.Equal("color_drift_av", Inputs(g, "lf_policy")["incoming_transition"]!.GetValue<string>());
        Assert.Equal("fflf_20261127_120000", Inputs(g, "lf_plan")["run_name"]!.GetValue<string>());
        // The Drift-Control model route: the sampler reads the model Chain Context hands back.
        Assert.Equal("lf_shift", Source(g, "lf_context", "model"));
        Assert.Equal("lf_context", Source(g, "lf_guider", "model"));
        Assert.Equal("res_multistep", Inputs(g, "lf_sampler")["sampler_name"]!.GetValue<string>());
        Assert.Equal("scene_1_anchor", Inputs(g, LongFormGraph.AssembleNode)["color_stabilization"]!.GetValue<string>());
        Assert.Equal("video/fflf/fflf_20261127_120000", Inputs(g, LongFormGraph.AssembleNode)["output_subfolder"]!.GetValue<string>());
        Assert.False(Inputs(g, "lf_review")["enabled"]!.GetValue<bool>());
        // A preview of each beat as it lands.
        Assert.Equal(LongFormGraph.ClipPreviewPrefix, Inputs(g, "clip_preview")["filename_prefix"]!.GetValue<string>());
    }

    [Fact]
    public void Build_AddsTheCarriedPrefixToBeatsAfterTheFirst()
    {
        var shots = Shots(LongFormGraph.Build(Story()));

        Assert.Equal(4, shots[0]!["duration_seconds"]!.GetValue<double>());
        Assert.Equal(Math.Round(5 + 39.0 / 24, 3), shots[1]!["duration_seconds"]!.GetValue<double>());
        Assert.Equal(Math.Round(6 + 39.0 / 24, 3), shots[2]!["duration_seconds"]!.GetValue<double>());
        Assert.Equal(new long[] { 11, 22, 33 }, shots.Select(s => s!["seed"]!.GetValue<long>()));
    }

    [Fact]
    public void Build_SendsTheOpeningImageOnEveryBeatAndPinsItOnBeatOneOnly()
    {
        var g = LongFormGraph.Build(Story(cast: new[] { "sheet_A.png", "sheet_B.png" }));
        var refs = Inputs(g, "lf_ref2v");

        Assert.Equal("lf_opening", refs["ref_images.ref_image_0"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("sheet_A.png", Inputs(g, refs["ref_images.ref_image_1"]!.AsArray()[0]!.GetValue<string>())["image"]!.GetValue<string>());
        Assert.Equal("sheet_B.png", Inputs(g, refs["ref_images.ref_image_2"]!.AsArray()[0]!.GetValue<string>())["image"]!.GetValue<string>());
        Assert.Equal("max", refs["ref_image_size"]!.GetValue<string>());

        // The frame-0 guide sits behind a lazy switch on "is the first scene".
        var sw = Inputs(g, "lf_first_frame_switch");
        Assert.Equal("lf_first_scene", sw["switch"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(1, sw["switch"]!.AsArray()[1]!.GetValue<int>());
        Assert.Equal("lf_first_frame", sw["on_true"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("lf_ref2v", sw["on_false"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("lf_first_frame_switch", Source(g, "lf_context", "conditioning"));
    }

    [Fact]
    public void Build_StacksTheLorasAndSkipsTheStackWithoutAny()
    {
        var g = LongFormGraph.Build(Story());
        var stack = Inputs(g, "lf_loras");
        Assert.Equal("H3/AmateurHour.safetensors", stack["lora_1"]!["lora"]!.GetValue<string>());
        Assert.Equal("lf_loras", Source(g, "lf_shift", "model"));

        var bare = LongFormGraph.Build(Story(loras: Array.Empty<MiniMaxI2VLora>()));
        Assert.False(bare.ContainsKey("lf_loras"));
        Assert.Equal("lf_attention", Source(bare, "lf_shift", "model"));
    }

    [Fact]
    public void Build_ResumesAtTheStartClipWithoutRefusingRewordedEarlierBeats()
    {
        var fresh = LongFormGraph.Build(Story());
        Assert.Equal(1, Inputs(fresh, "lf_loop_start")["start_clip"]!.GetValue<int>());
        Assert.True(Inputs(fresh, "lf_loop_start")["verify_resume_history"]!.GetValue<bool>());

        var resume = LongFormGraph.Build(Story(startClip: 3));
        Assert.Equal(3, Inputs(resume, "lf_loop_start")["start_clip"]!.GetValue<int>());
        Assert.Equal(3, Inputs(resume, "lf_preflight")["start_clip"]!.GetValue<int>());
        Assert.False(Inputs(resume, "lf_loop_start")["verify_resume_history"]!.GetValue<bool>());
        Assert.Equal(3, Shots(resume).Count);   // the whole story stays in the plan
    }

    [Fact]
    public void ComposeH3Prompt_ContinuationMakesPictureOneAReferenceOnly()
    {
        var cast = new List<FflfGraph.H3CastMember> { new("A", "on the left", "dark wavy hair", "teal sports bra") };
        var first = FflfGraph.ComposeH3Prompt("A crouches.", 4, null, "a grocery aisle", cast, false, isFirstBeat: true, continuesChain: true);
        var later = FflfGraph.ComposeH3Prompt("A springs up.", 5, null, "a grocery aisle", cast, false, isFirstBeat: false, continuesChain: true);

        Assert.Contains("the video starts exactly on it", first);
        Assert.Contains("The shot opens on <Picture 1>.", first);

        Assert.DoesNotContain("starts exactly on", later);
        Assert.DoesNotContain("The shot opens on <Picture 1>", later);
        Assert.Contains("never jump back to that pose", later);
        Assert.Contains("[continuation + reference generation]", later);
        Assert.Contains("<Picture 1> (reference): reference", later);
        Assert.Contains("The shot carries straight on from the moment before.", later);
    }
}

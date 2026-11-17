using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlipPix.Core.Models;
using FlipPix.UI.Services;
using Xunit;

namespace FlipPix.Tests;

/// <summary>The 🥋 Action tab: the combat presets, the graph it adds to FFLF's, and the director's words.</summary>
public class H3ActionTests
{
    private static string WorkflowFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax");
    private static string TwoPassJson() => File.ReadAllText(Path.Combine(WorkflowFolder, "h3-erosmax-2pass.json"));

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The authored workflow computes every LoRA strength with a ComfyMathExpression: a sum of
    /// <c>weight*max(0,1-abs(a-preset))</c> terms. Reads the weight for each preset number out of one.</summary>
    private static double[] WeightsByPreset(JsonNode workflow, string title)
    {
        var node = workflow["nodes"]!.AsArray().Single(n => n!["title"]?.GetValue<string>() == title)!;
        var expression = node["widgets_values"]![0]!.GetValue<string>();
        // The manual branches (b*c, d*e, b*min(…)) carry no preset term, so every match is an automatic weight.
        var weights = new double[9];
        foreach (Match m in Regex.Matches(expression, @"(\d*\.?\d+)\*max\(0,1-abs\(a-(\d)\)\)"))
            weights[int.Parse(m.Groups[2].Value)] = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return weights;
    }

    [Fact]
    public void Presets_AreTheAuthoredWorkflowsFormulas()
    {
        var workflow = JsonNode.Parse(File.ReadAllText(Path.Combine(WorkflowFolder, "action-director-9-combat-presets.json")))!;
        var names = new Dictionary<H3CombatLora, string>
        {
            [H3CombatLora.Combat] = "Combat", [H3CombatLora.Weapon] = "Weapon", [H3CombatLora.GunFu] = "GunFu",
            [H3CombatLora.Motion] = "Motion", [H3CombatLora.FightFlow] = "Fight Flow",
        };

        Assert.Equal(9, H3CombatPreset.All.Count);
        foreach (var (lora, name) in names)
        {
            var first = WeightsByPreset(workflow, $"一采配方｜{name}");
            var refine = WeightsByPreset(workflow, $"二采配方｜{name}");
            foreach (var preset in H3CombatPreset.All)
            {
                Assert.Equal(first[preset.Number], preset.FirstPass[lora], 6);
                Assert.Equal(refine[preset.Number], preset.Refine[lora], 6);
            }
        }
    }

    [Fact]
    public void Presets_NameTheWorkflowsLoraFiles()
    {
        var workflow = JsonNode.Parse(File.ReadAllText(Path.Combine(WorkflowFolder, "action-director-9-combat-presets.json")))!;
        var files = workflow["nodes"]!.AsArray()
            .Where(n => n!["type"]!.GetValue<string>() == "LoraLoaderModelOnly")
            .Select(n => n!["widgets_values"]![0]!.GetValue<string>()).ToHashSet();

        Assert.All(H3CombatPreset.Loras, l => Assert.Contains(l.FileName, files));
        Assert.Equal(H3CombatPreset.All[2], H3CombatPreset.ByNumber(99));   // out of range → the default, A2
    }

    [Fact]
    public void RefineFor_ScalesAndCapsAHandSetMix()
    {
        var refine = H3CombatPreset.RefineFor(new H3CombatMix(1.0, 1.0, 0.9, 0.5, 0.65));

        Assert.Equal(0.65, refine.Combat, 4);
        Assert.Equal(0.35, refine.Weapon, 4);     // 0.55 capped
        Assert.Equal(0.65, refine.GunFu, 4);
        Assert.Equal(0.2, refine.Motion, 4);
        Assert.Equal(0.25, refine.FightFlow, 4);
        Assert.True(H3CombatPreset.RefineFor(default).IsEmpty);
    }

    // ── Graph ───────────────────────────────────────────────────────────────────────────────────────

    private static List<MiniMaxI2VLora> Named(H3CombatMix mix) =>
        H3CombatPreset.Loras.Where(l => mix[l.Lora] > 0)
            .Select(l => new MiniMaxI2VLora { Name = $"H3/{l.FileName}", Strength = mix[l.Lora] }).ToList();

    private static FflfBeatRender Request(H3CombatPreset preset, bool soften = true, bool preview = false, string lora = H3FflfSettings.DefaultLora) => new()
    {
        StartImage = "last_frame.png",
        IdentityImage = "opening.png",
        Prompt = "p",
        Seed = 7,
        PreviewOnly = preview,
        Lora = lora,
        ExtraLoras = Named(preset.FirstPass),
        RefineLoras = soften ? Named(preset.Refine) : null,
        OutputPrefix = "video/action/run/beat_02",
    };

    private static List<(string Name, double Strength)> Rows(JsonObject root, string stack) =>
        root[stack]!["inputs"]!.AsObject()
            .Where(kv => kv.Key.StartsWith("lora_", StringComparison.Ordinal))
            .Select(kv => (kv.Value!["lora"]!.GetValue<string>(), kv.Value!["strength"]!.GetValue<double>())).ToList();

    private static string Source(JsonObject root, string node, string input = "model") =>
        root[node]!["inputs"]![input]!.AsArray()[0]!.ToString();

    private static void AssertLinksResolve(JsonObject root)
    {
        foreach (var (id, node) in root)
            foreach (var (name, value) in node!["inputs"]!.AsObject())
                if (value is JsonArray { Count: 2 } link && link[0] is JsonValue v && v.TryGetValue<string>(out var target)
                    && link[1] is JsonValue slot && slot.TryGetValue<int>(out _))
                    Assert.True(root.ContainsKey(target), $"{id}.{name} → {target} is not in the graph");
    }

    [Fact]
    public void Build_StacksTheMixAfterTheTabsLora()
    {
        var a3 = H3CombatPreset.ByNumber(3);
        var root = FflfGraph.Build(TwoPassJson(), Request(a3), out _);

        Assert.Equal(new[]
        {
            (H3FflfSettings.DefaultLora, 0.25),
            ("H3/H3_Combat_V2.safetensors", 0.7),
            ("H3/Motion_Repair_V2.safetensors", 0.5),
            ("H3/H3_Fight_Flow_V1.safetensors", 0.65),
        }, Rows(root, "101"));
        AssertLinksResolve(root);
    }

    [Fact]
    public void Build_RefinePassReadsItsOwnWeakerStack()
    {
        var a3 = H3CombatPreset.ByNumber(3);
        var root = FflfGraph.Build(TwoPassJson(), Request(a3), out _);
        var copy = FflfGraph.RefineNodePrefix + "101";

        // The copy keeps the tab's LoRA and carries the second-pass strengths.
        Assert.Equal(new[]
        {
            (H3FflfSettings.DefaultLora, 0.25),
            ("H3/H3_Combat_V2.safetensors", 0.45),
            ("H3/Motion_Repair_V2.safetensors", 0.2),
            ("H3/H3_Fight_Flow_V1.safetensors", 0.25),
        }, Rows(root, copy));

        // Pass 2's guider reaches the copy and never the first-pass stack; pass 1's still reaches the original.
        string Top(string guider)
        {
            var id = Source(root, guider);
            while (root[id]!["class_type"]!.GetValue<string>() != "H3PowerLoraStack") id = Source(root, id);
            return id;
        }
        Assert.Equal(copy, Top("151:32"));
        Assert.Equal("101", Top("151:29"));
        // Both stacks sit on the same loader wire, and the pass-2 scheduler reads the refine wire.
        Assert.Equal(Source(root, "101"), Source(root, copy));
        Assert.StartsWith(FflfGraph.RefineNodePrefix, Source(root, "102"));
        AssertLinksResolve(root);
    }

    [Fact]
    public void Build_RefineDropsALoraThePresetLeavesOut()
    {
        // A7 runs Combat on the first pass only.
        var root = FflfGraph.Build(TwoPassJson(), Request(H3CombatPreset.ByNumber(7)), out _);

        Assert.Contains(Rows(root, "101"), r => r.Name.EndsWith("H3_Combat_V2.safetensors"));
        Assert.DoesNotContain(Rows(root, FflfGraph.RefineNodePrefix + "101"), r => r.Name.EndsWith("H3_Combat_V2.safetensors"));
    }

    [Fact]
    public void Build_OneWireWhenTheRefineMixIsNotAsked()
    {
        var same = FflfGraph.Build(TwoPassJson(), Request(H3CombatPreset.ByNumber(2), soften: false), out _);
        Assert.DoesNotContain(same, kv => kv.Key.StartsWith(FflfGraph.RefineNodePrefix));

        // A draft has no second pass to give a mix to.
        var draft = FflfGraph.Build(TwoPassJson(), Request(H3CombatPreset.ByNumber(2), preview: true), out _);
        Assert.DoesNotContain(draft, kv => kv.Key.StartsWith(FflfGraph.RefineNodePrefix));
        AssertLinksResolve(draft);
    }

    [Fact]
    public void Build_RefineWithNoLoraLeftReadsTheLoaderWire()
    {
        // No tab LoRA and a hand-set mix that scales to nothing on the refine: no empty stack is left behind.
        var request = Request(H3CombatPreset.ByNumber(0), lora: string.Empty) with { RefineLoras = new List<MiniMaxI2VLora>() };
        var root = FflfGraph.Build(TwoPassJson(), request, out _);

        Assert.False(root.ContainsKey(FflfGraph.RefineNodePrefix + "101"));
        var id = Source(root, "151:32");
        while (id.StartsWith(FflfGraph.RefineNodePrefix) || id == "tp_preview2") id = Source(root, id);
        Assert.Equal(Source(root, "101"), id);
        AssertLinksResolve(root);
    }

    [Fact]
    public void Build_SendsTheCastAfterTheStartFrameAndTheOpeningImage()
    {
        var request = Request(H3CombatPreset.ByNumber(2)) with { CastImages = new[] { "fighter_a.png", "fighter_b.png", "one_too_many.png" } };
        var root = FflfGraph.Build(TwoPassJson(), request, out _);
        var refs = root["151:11"]!["inputs"]!.AsObject();

        string Picture(int i) => root[refs[$"ref_images.ref_image_{i}"]!.AsArray()[0]!.ToString()]!["inputs"]!["image"]!.GetValue<string>();
        Assert.Equal(new[] { "last_frame.png", "opening.png", "fighter_a.png", "fighter_b.png" }, Enumerable.Range(0, 4).Select(Picture));
        Assert.DoesNotContain("one_too_many.png", root.ToJsonString());
    }

    [Fact]
    public void Build_AuthoredSamplingReplacesTheTurboPairing()
    {
        var request = Request(H3CombatPreset.ByNumber(2)) with { Sampler = "res_multistep", FirstPassScheduler = "simple", FirstPassSteps = 20 };
        var root = FflfGraph.Build(TwoPassJson(), request, out _);

        Assert.Equal("res_multistep", root["18"]!["inputs"]!["sampler_name"]!.GetValue<string>());
        Assert.Equal("simple", root["24"]!["inputs"]!["scheduler"]!.GetValue<string>());
        Assert.Equal(20, root["24"]!["inputs"]!["steps"]!.GetValue<int>());
    }

    // ── The director ────────────────────────────────────────────────────────────────────────────────

    private const string Reply = """
        <think>two fighters, a garage</think>
        **SETTING:** A, the man in the charcoal coat, stands camera-left; B, the woman in the red jacket,
        stands camera-right. A wet parking garage under sodium light.

        ## BEAT 1 — The Grab
        [0.0-1.2s] A catches B's left wrist; B twists her arm down and out.
        [1.2-5.0s] B shoves A's chest with both palms; A slides back a step. They face each other.

        BEAT 2: "Between The Cars!"
        [0.0-2.0s] A swings a right hook; B ducks under it.
        [2.0-5.0s] B drives a knee into A's ribs; A drops to one knee beside the car.
        """;

    [Fact]
    public void ParseScript_ReadsTheSettingAndTheBeats()
    {
        var script = ActionDirector.ParseScript(Reply);

        Assert.StartsWith("A, the man in the charcoal coat", script.Setting);
        Assert.EndsWith("under sodium light.", script.Setting);
        Assert.DoesNotContain("think", script.Setting);
        Assert.Equal(new[] { "The Grab", "Between The Cars" }, script.Beats.Select(b => b.Title));
        Assert.StartsWith("[0.0-1.2s] A catches B's left wrist", script.Beats[0].Text);
        Assert.EndsWith("They face each other.", script.Beats[0].Text);
        Assert.DoesNotContain("\n", script.Beats[0].Text);
    }

    [Fact]
    public void ParseScript_BeatsSurviveTheBeatBox()
    {
        // What 🎬 Direct writes into the beat box must be read back as the same beats, timecodes and all.
        var script = ActionDirector.ParseScript(Reply);
        var box = ActionDirector.ToBeatsText(script.Beats);

        Assert.Equal(script.Beats, FflfGraph.ParseBeats(box));
    }

    [Fact]
    public void ParseScript_ReplyWithoutHeadingsIsReadAsPlainBeats()
    {
        var script = ActionDirector.ParseScript("1. He lunges.\n2. She sidesteps and trips him.");

        Assert.Equal(string.Empty, script.Setting);
        Assert.Equal(new[] { "He lunges.", "She sidesteps and trips him." }, script.Beats.Select(b => b.Text));
        Assert.Empty(ActionDirector.ParseScript("  ").Beats);
    }

    [Fact]
    public void BuildRequest_NumbersThePicturesAndStatesThePlan()
    {
        var request = ActionDirector.BuildRequest(H3CombatPreset.ByNumber(4), 3, 5, "A duel on the bridge.", hasOpeningImage: true, new[] { "Fighter A" });

        Assert.Contains("Picture 1 is the opening frame", request);
        Assert.Contains("Picture 2 is a photo of Fighter A", request);
        Assert.Contains("A4 — Blades and weapons", request);
        Assert.Contains("exactly 3 beats, 5 seconds each (15 seconds in all)", request);
        Assert.EndsWith("A duel on the bridge.", request);
        Assert.Contains("No picture is attached", ActionDirector.BuildRequest(H3CombatPreset.ByNumber(2), 1, 5, null, false, Array.Empty<string>()));
    }

    [Fact]
    public void ComposeBeatPrompt_WrapsTheBeatInThePresetsDirection()
    {
        var a2 = H3CombatPreset.ByNumber(2);
        var prompt = ActionDirector.ComposeBeatPrompt("[0.0-5.0s] A jabs; B parries.", "A garage at night.", a2, a2.FirstPass, 5,
            hasIdentityImage: true, isFirstBeat: false, isLastBeat: false, new[] { "Fighter A", "Fighter B" });

        Assert.StartsWith("<Picture 1> is the first frame of this shot", prompt);
        Assert.Contains("<Picture 2> shows how the characters look", prompt);
        Assert.Contains("<Picture 3> is Fighter A", prompt);
        Assert.Contains("<Picture 4> is Fighter B", prompt);
        // Setting, then the lead phrase, the beat, the rules and the sound — in that order.
        var order = new[] { "A garage at night.", "Unarmed combat interaction.", "[0.0-5.0s] A jabs", "Every strike has a target", "ready for what comes next", "Audio:" }
            .Select(s => prompt.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void ComposeBeatPrompt_FirstBeatHasRoomForTheCastAndTheLastBeatEnds()
    {
        var a5 = H3CombatPreset.ByNumber(5);
        var prompt = ActionDirector.ComposeBeatPrompt("A fires twice. Audio: two flat cracks.", null, a5, a5.FirstPass, 5,
            hasIdentityImage: false, isFirstBeat: true, isLastBeat: true, new[] { "Fighter A", "Fighter B", "Fighter C" });

        Assert.Contains("<Picture 2> is Fighter A", prompt);
        Assert.Contains("<Picture 3> is Fighter B", prompt);
        Assert.Contains("<Picture 4> is Fighter C", prompt);
        Assert.Contains("clear, held final moment", prompt);
        // A beat that writes its own sound keeps it.
        Assert.Equal(1, Regex.Matches(prompt, "Audio:").Count);
        Assert.Contains("gunshots", ActionDirector.AudioLine(a5.FirstPass));
        Assert.Contains("steel", ActionDirector.AudioLine(H3CombatPreset.ByNumber(4).FirstPass));
    }
}

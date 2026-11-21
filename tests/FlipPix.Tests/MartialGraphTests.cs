using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.Core.Models;
using FlipPix.UI.Services;
using Xunit;

namespace FlipPix.Tests;

/// <summary>The 🥷 Martial tab: its graph against the authored martial workflow, and the numbers it computes.</summary>
public class MartialGraphTests
{
    private static string WorkflowPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax", "martial-h3-wushu-keyframes.json");

    private static JsonArray AuthoredNodes() => JsonNode.Parse(File.ReadAllText(WorkflowPath))!["nodes"]!.AsArray();

    private static IEnumerable<JsonNode> OfType(JsonArray nodes, string type) =>
        nodes.Where(n => n!["type"]!.GetValue<string>() == type).Select(n => n!);

    private static MartialBeatRender Request(bool draft = false) => new()
    {
        Pictures = new[] { "start.png", "identity.png", "ref1.png" },
        FirstFrame = "start.png",
        Keyframes = new[] { new MartialKeyframe("key1.png", 60), new MartialKeyframe("key2.png", 999) },
        Prompt = "wushu_action, camera motion,\nfight",
        Seconds = 10,
        Seed = 42,
        DiffusionModel = H3MartialSettings.DefaultMartialModel,
        TextEncoder = H3MartialSettings.DefaultTextEncoder,
        Loras = MartialCatalog.AuthoredStack.Select(l => new MartialLora(l.Name, l.Strength)).ToList(),
        RefineLoras = MartialCatalog.AuthoredStack.Select(l => new MartialLora(l.Name, l.Strength)).ToList(),
        Draft = draft,
        OutputPrefix = "video/martial/test/beat_01",
    };

    [Theory]
    [InlineData(5.0, 124)]
    [InlineData(10.0, 243)]
    [InlineData(15.0, 362)]
    [InlineData(0.1, 5)]
    [InlineData(2.0, 56)]
    public void FrameCount_IsTheWorkflowsExpression(double seconds, int frames)
    {
        // max(5, round(a * 24)) + (5 - (max(5, round(a * 24)) % 17)) % 17
        Assert.Equal(frames, MartialGraph.FrameCount(seconds));
        Assert.Equal(5, MartialGraph.FrameCount(seconds) % 17);
    }

    [Fact]
    public void FrameCount_Expression_IsTheAuthoredOne()
    {
        var expression = OfType(AuthoredNodes(), "ComfyMathExpression").Select(n => n["widgets_values"]![0]!.GetValue<string>());
        Assert.Contains("max(5, round(a * 24)) + (5 - (max(5, round(a * 24)) % 17)) % 17", expression);
    }

    [Fact]
    public void AuthoredStack_MatchesTheWorkflowsActiveLoraStrengths()
    {
        var nodes = AuthoredNodes();
        var active = OfType(nodes, "LoraLoader").Concat(OfType(nodes, "LoraLoaderModelOnly"))
            .Where(n => n["mode"]!.GetValue<int>() == 0)
            .Select(n => Math.Round(n["widgets_values"]![1]!.GetValue<double>(), 2))
            .OrderBy(s => s).ToList();
        Assert.Equal(active, MartialCatalog.AuthoredStack.Select(l => l.Strength).OrderBy(s => s).ToList());
        Assert.All(MartialCatalog.AuthoredStack, l => Assert.NotNull(MartialCatalog.Find(l.Name)));
    }

    [Fact]
    public void Build_LinksOnlyToNodesInTheGraph()
    {
        foreach (var draft in new[] { false, true })
        {
            var root = MartialGraph.Build(Request(draft));
            foreach (var (id, node) in root)
            {
                Assert.False(string.IsNullOrEmpty(node!["class_type"]?.GetValue<string>()), id);
                foreach (var (name, value) in node["inputs"]!.AsObject())
                    if (value is JsonArray link && link.Count == 2 && link[0] is JsonValue v && v.TryGetValue<string>(out var target))
                        Assert.True(root.ContainsKey(target), $"{id}.{name} → {target} is missing");
            }
        }
    }

    [Fact]
    public void Build_AsAuthored()
    {
        var root = MartialGraph.Build(Request());
        var nodes = root.Select(kv => kv.Value!).ToList();
        string Cls(JsonNode n) => n["class_type"]!.GetValue<string>();

        var r2v = nodes.Single(n => Cls(n) == "MiniMaxH3ReferenceToVideo")["inputs"]!;
        Assert.Equal(243, r2v["length"]!.GetValue<int>());
        // Three pictures and a refine: full-size references (see Build_RefinedBeatWithSeveralPictures_SendsFullSizeReferences).
        Assert.Equal("max", r2v["ref_image_size"]!.GetValue<string>());
        Assert.NotNull(r2v["ref_images.ref_image_2"]);
        Assert.Null(r2v["ref_images.ref_image_3"]);

        // First frame + two keyframes, the second clamped to the last frame.
        var frames = nodes.Where(n => Cls(n) == "MiniMaxH3AddGuide").Select(n => n["inputs"]!["frame_idx"]!.GetValue<int>()).ToList();
        Assert.Equal(new[] { 0, 60, 242 }, frames);

        var ks = nodes.Single(n => Cls(n) == "KSampler")["inputs"]!;
        Assert.Equal("euler", ks["sampler_name"]!.GetValue<string>());
        Assert.Equal("simple", ks["scheduler"]!.GetValue<string>());
        Assert.Equal(1, ks["cfg"]!.GetValue<int>());

        var stack = nodes.Single(n => Cls(n) == "H3PowerLoraStack")["inputs"]!;
        Assert.Equal(0.8, stack["lora_1"]!["strength"]!.GetValue<double>());
        Assert.Equal(4, stack.AsObject().Count(kv => kv.Key.StartsWith("lora_")));

        Assert.Equal(2, nodes.Count(n => Cls(n) == "SamplerCustomAdvanced"));
        Assert.Single(nodes, n => Cls(n) == "MinimaxH3LatentUpscaler3D");
        // Same model and LoRAs on both passes: one model wire.
        Assert.Single(nodes, n => Cls(n) == "UNETLoader");
        Assert.Single(nodes, n => Cls(n) == "H3PowerLoraStack");
        Assert.True(root.ContainsKey(MartialGraph.VideoSaveNode));
        Assert.True(root.ContainsKey(MartialGraph.LastFrameSaveNode));
    }

    [Fact]
    public void Build_UsesTheAuthoredVaesAndUpscaler()
    {
        var authored = AuthoredNodes();
        var vaes = OfType(authored, "VAELoader").Select(n => n["widgets_values"]![0]!.GetValue<string>()).ToList();
        Assert.Contains(MartialGraph.VideoVae, vaes);
        Assert.Contains(MartialGraph.AudioVae, vaes);
        Assert.Equal(MartialGraph.Upscaler, OfType(authored, "MinimaxH3LatentUpscaler3D").Single()["widgets_values"]![0]!.GetValue<string>());
    }

    private static string RefSize(MartialBeatRender r) =>
        MartialGraph.Build(r)["m_r2v"]!["inputs"]!["ref_image_size"]!.GetValue<string>();

    /// <summary>Beats 2+ send the start frame and the opening image. With 'match' the references are sized
    /// for the pass-1 canvas and the upscaled finish pass turned every later beat into a block pattern on the
    /// server; beat 1, with one picture, rendered fine.</summary>
    [Fact]
    public void Build_RefinedBeatWithSeveralPictures_SendsFullSizeReferences()
    {
        var one = Request() with { Pictures = new[] { "start.png" } };
        var two = Request() with { Pictures = new[] { "start.png", "identity.png" } };

        Assert.Equal("match", RefSize(one));
        Assert.Equal("max", RefSize(two));
        Assert.Equal("match", RefSize(two with { Draft = true }));            // no upscale, the setting stands
        Assert.Equal("max", RefSize(one with { MaxFidelityReferences = true }));
        Assert.Equal("max", RefSize(two with { Draft = true, MaxFidelityReferences = true }));
    }

    [Fact]
    public void Build_Draft_StopsAfterPassOne()
    {
        var root = MartialGraph.Build(Request(draft: true));
        Assert.DoesNotContain(root, kv => kv.Value!["class_type"]!.GetValue<string>() is "SamplerCustomAdvanced" or "MinimaxH3LatentUpscaler3D");
        Assert.Equal("m_pass1", root["m_decode"]!["inputs"]!["samples"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Build_RefineModelOrMix_GetsItsOwnWire()
    {
        var r = Request() with
        {
            RefineModel = "h3-minimax/minimax_h3_ref2va_pruned_int8_convrot.safetensors",
            RefineLoras = new[] { new MartialLora("H3/x.safetensors", 0.5) },
        };
        var root = MartialGraph.Build(r);
        Assert.Equal(2, root.Count(kv => kv.Value!["class_type"]!.GetValue<string>() == "UNETLoader"));
        Assert.Equal(2, root.Count(kv => kv.Value!["class_type"]!.GetValue<string>() == "H3PowerLoraStack"));
        Assert.Equal("m_p2_preview", root["m_guider_finish"]!["inputs"]!["model"]![0]!.GetValue<string>());
        Assert.Equal("m_p1_preview", root["m_pass1"]!["inputs"]!["model"]![0]!.GetValue<string>());
    }

    [Fact]
    public void RefineSigmas_SplitAroundTheUpscale()
    {
        var (high, low) = MartialGraph.RefineSigmas(0.6, 10);
        Assert.Equal(6, high.Length);
        Assert.Equal(6, low.Length);
        Assert.Equal(0.6, high[0], 4);
        Assert.Equal(high[^1], low[0]);
        Assert.Equal(0, low[^1]);
        Assert.True(high.Zip(high.Skip(1)).All(p => p.First > p.Second));
    }

    [Fact]
    public void ComposePrompt_PutsTheTriggerFirst()
    {
        var prompt = MartialGraph.ComposePrompt("wushu_action, camera motion,", "He strikes.", "A dojo.", new[] { "is the first frame.", "is the sword." });
        Assert.StartsWith("wushu_action, camera motion,\n<Picture 1> is the first frame.\n<Picture 2> is the sword.", prompt);
        Assert.EndsWith("A dojo.\n\nHe strikes.", prompt);
    }
}

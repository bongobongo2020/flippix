using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Xunit;

namespace FlipPix.Tests;

public class FflfGraphTests
{
    private static string TwoPassJson() =>
        File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax", "h3-erosmax-2pass.json"));

    private const string Story = """
        The Low Entry: The fight begins with the woman, Kira, feinting high with her right hand, only to sweep the man's (Toru’s) leg as he attempts a forward step.

        1. The Hip Pivot: Seeing his balance compromised, Toru instantly reacts by gripping Kira’s sleeve and driving her forward.
        2. The Spin Release: As Toru executes the hip throw, Kira twists her body to absorb the impact and release him.
        3. The Standing Grapple: From the spin, Toru uses his momentum to hook Kira’s leg
        from the bottom position, pulling her down.
        4. The Final Lock: Kira lands heavily but doesn’t collapse; instead, she catches Toru’s landing shoulder.
        """;

    [Fact]
    public void ParseBeats_SplitsParagraphsAndNumberedLines()
    {
        var beats = FflfGraph.ParseBeats(Story);

        Assert.Equal(5, beats.Count);
        Assert.Equal(new[] { "The Low Entry", "The Hip Pivot", "The Spin Release", "The Standing Grapple", "The Final Lock" },
                     beats.Select(b => b.Title));
        Assert.StartsWith("The fight begins", beats[0].Text);
        // A wrapped line carries on the beat above it.
        Assert.EndsWith("from the bottom position, pulling her down.", beats[3].Text);
        Assert.DoesNotContain(beats, b => b.Text.StartsWith("1.") || b.Text.Contains("  "));
    }

    [Fact]
    public void ParseBeats_PlainParagraphsHaveNoTitle()
    {
        var beats = FflfGraph.ParseBeats("He walks in. The room is dark.\n\nShe turns on the light, smiling.");
        Assert.Equal(2, beats.Count);
        Assert.Equal(string.Empty, beats[0].Title);
        Assert.Equal("He walks in. The room is dark.", beats[0].Text);
    }

    private static FflfBeatRender Request(bool preview = false, bool identity = true) => new()
    {
        StartImage = "last_frame.png",
        IdentityImage = identity ? "opening.png" : null,
        Prompt = FflfGraph.ComposePrompt("Kira sweeps Toru's leg.", "A judo dojo.", identity, isFirstBeat: false),
        Seconds = 5,
        Seed = 1234,
        AspectRatio = "16:9 (Widescreen)",
        PreviewOnly = preview,
        OutputPrefix = "video/fflf/run/beat_02",
    };

    private static string[] Link(JsonObject root, string node, string input) =>
        root[node]!["inputs"]![input]!.AsArray().Select(n => n!.ToString()).ToArray();

    [Fact]
    public void Build_UsesTheTabsModelAndLora()
    {
        var root = FflfGraph.Build(TwoPassJson(), Request(), out _);

        Assert.Equal(H3FflfSettings.DefaultModel, root["30"]!["inputs"]!["unet_name"]!.GetValue<string>());
        var lora = root["101"]!["inputs"]!["lora_1"]!.AsObject();
        Assert.Equal(H3FflfSettings.DefaultLora, lora["lora"]!.GetValue<string>());
        Assert.Equal(0.25, lora["strength"]!.GetValue<double>());
        Assert.Equal(1234, root["31"]!["inputs"]!["noise_seed"]!.GetValue<long>());
    }

    [Fact]
    public void Build_SendsStartFrameAsPictureOneAndOpeningAsPictureTwo()
    {
        var root = FflfGraph.Build(TwoPassJson(), Request(), out _);
        var refs = root["151:11"]!["inputs"]!.AsObject();

        var first = refs["ref_images.ref_image_0"]!.AsArray()[0]!.ToString();
        var second = refs["ref_images.ref_image_1"]!.AsArray()[0]!.ToString();
        Assert.Equal("last_frame.png", root[first]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal("opening.png", root[second]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Contains("<Picture 2>", root["2"]!["inputs"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Build_AnchorsTheStartFrameOnBothPasses()
    {
        var root = FflfGraph.Build(TwoPassJson(), Request(), out _);

        Assert.Equal(new[] { FflfGraph.GuidePass1Node, "0" }, Link(root, "151:29", "conditioning"));
        Assert.Equal(new[] { FflfGraph.GuidePass2Node, "0" }, Link(root, "151:32", "conditioning"));
        Assert.Equal(new[] { "151:11", "1" }, Link(root, FflfGraph.GuidePass1Node, "latent"));
        Assert.Equal(new[] { "151:10", "0" }, Link(root, FflfGraph.GuidePass2Node, "latent"));
        Assert.Equal(new[] { FflfGraph.StartImageNode, "0" }, Link(root, FflfGraph.GuidePass1Node, "image"));
        Assert.Equal("last_frame.png", root[FflfGraph.StartImageNode]!["inputs"]!["image"]!.GetValue<string>());
        // Every link points at a node that is in the graph.
        AssertLinksResolve(root);
    }

    [Fact]
    public void Build_DraftGuidesPassOneOnly()
    {
        var root = FflfGraph.Build(TwoPassJson(), Request(preview: true), out _);

        Assert.True(root.ContainsKey(FflfGraph.GuidePass1Node));
        Assert.False(root.ContainsKey(FflfGraph.GuidePass2Node));
        Assert.False(root.ContainsKey("151:32"));
        AssertLinksResolve(root);
    }

    [Fact]
    public void Build_SavesTheLastFrameTheEncoderReceives()
    {
        var root = FflfGraph.Build(TwoPassJson(), Request(), out _);

        var encoderFrames = root["152:135"]!["inputs"]!["images"]!.ToJsonString();
        Assert.Equal(encoderFrames, root[FflfGraph.LastFrameNode]!["inputs"]!["image"]!.ToJsonString());
        Assert.Equal(-1, root[FflfGraph.LastFrameNode]!["inputs"]!["batch_index"]!.GetValue<int>());
        Assert.Equal("video/fflf/run/beat_02_last", root[FflfGraph.LastFrameSaveNode]!["inputs"]!["filename_prefix"]!.GetValue<string>());
        Assert.Equal("video/fflf/run/beat_02", root["73"]!["inputs"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void JoinArguments_DropsTheRepeatedJoinFrame()
    {
        var args = FflfGraph.JoinArguments(new[] { "a.mp4", "b.mp4", "c.mp4" }, "out.mp4", dropFirstFrame: true, withAudio: true);

        Assert.Contains("[0:v]setpts=PTS-STARTPTS", args);
        Assert.Contains("[1:v]trim=start_frame=1,", args);
        Assert.Contains("[2:a]atrim=start=", args);
        Assert.Contains("concat=n=3:v=1:a=1[v][a]", args);
    }

    [Fact]
    public void Story_SurvivesSaveAndReload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fflf-story-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new ScenePromptLibrary(null, folder);
            var entry = new ScenePrompt
            {
                Name = "The Low Entry",
                Prompt = Story,
                Fflf = new FflfStory
                {
                    BeatsText = Story,
                    InitialImagePath = @"C:\pics\open.png",
                    LoraStrength = 0.25,
                    RunFolder = "fflf_20261109_120000",
                    Beats = { new FflfStoryBeat { Text = "beat one", Seed = 42, Done = true, VideoPath = @"Z:\video\a.mp4", LastFramePath = @"C:\frames\f.png" } },
                },
            };
            library.Save(new[] { entry, new ScenePrompt { Name = "an I2V draft", Prompt = "no story here" } });

            var loaded = new ScenePromptLibrary(null, folder).Load();
            var story = loaded.Single(e => e.Id == entry.Id).Fflf!;
            Assert.Equal(Story, story.BeatsText);
            Assert.Equal("fflf_20261109_120000", story.RunFolder);
            Assert.Equal(0.25, story.LoraStrength);
            Assert.Equal(42, story.Beats[0].Seed);
            Assert.True(story.Beats[0].Done);
            Assert.Null(loaded.Single(e => e.Name == "an I2V draft").Fflf);
            Assert.DoesNotContain("\"Fflf\": null", File.ReadAllText(Path.Combine(folder, "index.json")));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    private static void AssertLinksResolve(JsonObject root)
    {
        foreach (var (id, node) in root)
            foreach (var (name, value) in node!["inputs"]!.AsObject())
                if (value is JsonArray { Count: 2 } link && link[0] is JsonValue v && v.TryGetValue<string>(out var target)
                    && link[1] is JsonValue slot && slot.TryGetValue<int>(out _))
                    Assert.True(root.ContainsKey(target), $"{id}.{name} links to missing node {target}");
    }
}

using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// 🎬 H3 Video Editor's 🌹 ErosMax 2-Pass preset: the graphs its two stages submit, built by the I2V tab's
/// builder. A preview is pass 1 alone (pass 2 and the upscaler pruned, not merely switched off); the final is the
/// full graph on the same seed; a chained beat runs the graph's Continue mode but exports only its new segment,
/// picture and sound trimmed together, under the editor's own file prefix.
/// </summary>
public sealed class H3EditorTwoPassTests
{
    private static string ApiJson() =>
        File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax", "h3-erosmax-2pass.json"));

    private static readonly MethodInfo Graph =
        typeof(MiniMaxI2VViewModel).GetMethod("BuildTwoPassGraph", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MiniMaxI2VViewModel.BuildTwoPassGraph is gone or no longer static.");

    private static JsonObject Beat(bool preview, string? guide = null, long seed = 777, string prefix = "video/proj/clip_00002",
                                   MiniMaxI2VTwoPassOptions? options = null)
    {
        var o = options ?? new MiniMaxI2VTwoPassOptions();
        o.PreviewOnly = preview;
        o.FrameGenMultiplier = 1;
        o.ContinueMode = guide != null;
        var item = new MiniMaxI2VQueueItem
        {
            Workflow = I2VWorkflow.TwoPass, TwoPass = o, Prompt = "a beat", AspectRatio = "16:9 (Widescreen)",
        };
        var args = new object?[]
        {
            ApiJson(), item, new List<string> { "pic0.png" }, new List<string>(), new List<string>(), new List<bool>(),
            guide, seed, prefix, true, 0,
        };
        return JsonNode.Parse((string)Graph.Invoke(null, args)!)!.AsObject();
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

    private static void NoDanglingLinks(JsonObject root)
    {
        foreach (var (id, node) in root)
            foreach (var (name, value) in node!["inputs"]!.AsObject())
                if (value is JsonArray { Count: 2 } link && link[0] is JsonValue v && v.TryGetValue<string>(out var src))
                    Assert.True(root.ContainsKey(src), $"{id}.{name} links to missing node {src}");
    }

    [Fact]
    public void A_preview_is_the_first_pass_alone()
    {
        var built = Beat(preview: true);
        // Pass 1's video and sound are decoded; pass 2, the upscaler and the switches are gone.
        Assert.Equal("151:35", LinkSource(built, "151:25", "samples"));
        Assert.Equal("151:35", LinkSource(built, "151:86", "input"));
        foreach (var gone in new[] { "151:16", "151:26", "151:10", "102", "151:95", "151:88", "tp_preview2" })
            Assert.False(built.ContainsKey(gone), $"{gone} should be pruned from a preview");
        Assert.True(Inputs(built, "94")["value"]!.GetValue<bool>());
        Assert.Equal("video/proj/clip_00002", Inputs(built, "74")["value"]!.GetValue<string>());
        Assert.Equal(777L, Inputs(built, "31")["noise_seed"]!.GetValue<long>());
        NoDanglingLinks(built);
    }

    [Fact]
    public void The_final_reruns_the_same_first_pass_then_refines()
    {
        var preview = Beat(preview: true);
        var final = Beat(preview: false);
        Assert.Equal("151:16", LinkSource(final, "151:95", "on_false"));
        Assert.True(final.ContainsKey("151:26"));
        // Everything pass 1 reads is identical, so the approved take is what gets refined.
        foreach (var id in new[] { "30", "15", "2", "17", "20", "31", "18", "24", "151:11", "151:21", "151:29" })
            Assert.True(JsonNode.DeepEquals(preview[id], final[id]), $"pass-1 node {id} differs between preview and final");
        Assert.Equal("video/proj/clip_00002", Inputs(final, "73")["value"]!.GetValue<string>());
        NoDanglingLinks(final);
    }

    [Fact]
    public void A_chained_beat_exports_only_its_new_segment()
    {
        foreach (var preview in new[] { true, false })
        {
            var built = Beat(preview, guide: "prev_preview.mp4");
            Assert.True(Inputs(built, "103")["value"]!.GetValue<bool>());
            Assert.Equal("prev_preview.mp4", Inputs(built, "151:105")["video"]!.GetValue<string>());
            // Guided on both passes' conditioning (pass 1 only for a preview).
            Assert.Equal("151:110", LinkSource(built, "151:29", "conditioning"));
            // The Normal container, fed the trimmed segment and the trimmed sound, under the take's own prefix.
            Assert.Equal("152:137", LinkSource(built, "40", "video"));
            Assert.Equal("152:123", LinkSource(built, "152:137", "complete_audio"));
            Assert.Equal("152:135", LinkSource(built, "152:137", "videos.video0"));
            Assert.Equal("152:115", LinkSource(built, "152:135", "images"));
            Assert.Equal("152:127", LinkSource(built, "152:115", "on_true"));
            Assert.Equal("152:75", LinkSource(built, "40", "filename_prefix"));
            foreach (var gone in new[] { "152:114", "152:136", "152:138", "152:150", "155" })
                Assert.False(built.ContainsKey(gone), $"{gone} should not be in a segment export");
            NoDanglingLinks(built);
        }
    }

    [Fact]
    public void The_I2V_tab_still_builds_the_stitched_continue_export()
    {
        var item = new MiniMaxI2VQueueItem
        {
            Workflow = I2VWorkflow.TwoPass, TwoPass = new MiniMaxI2VTwoPassOptions(), Prompt = "p", AspectRatio = "4:3 (Standard)",
        };
        var build = typeof(MiniMaxI2VViewModel).GetMethod("BuildTwoPassWorkflow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var json = (string)build.Invoke(null, new object?[]
        {
            ApiJson(), item, new List<string>(), new List<string>(), new List<string>(), new List<bool>(),
            "src.mp4", 1L, "tok", 0,
        })!;
        var built = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("152:138", LinkSource(built, "40", "video"));
        Assert.Equal("minimax_i2v/tok", Inputs(built, "73")["value"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(10.0, 56)]
    [InlineData(5.0, 22)]
    [InlineData(7.5, 39)]
    public void A_chained_beat_still_delivers_its_length_after_the_cut(double seconds, int guide)
    {
        var chained = typeof(H3VideoEditorViewModel).GetMethod("TwoPassChainedSeconds", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rendered = (double)chained.Invoke(null, new object[] { seconds, guide })!;
        // The graph's own frame count: max(5, round(a*24)) rounded up onto 17k+5.
        var f = Math.Max(5, (int)Math.Round(rendered * 24));
        var frames = f + (5 - f % 17 + 17) % 17;
        Assert.InRange(frames - guide, (int)Math.Round(seconds * 24), (int)Math.Round(seconds * 24) + 17);
    }

    [Fact]
    public void Clip_badges_follow_the_review()
    {
        var path = Path.GetTempFileName();
        try
        {
            var clip = new H3TimelineClip { Prompt = "a" };
            Assert.Equal(string.Empty, clip.TwoPassBadge);
            clip.PreviewPath = path;
            clip.PreviewedPrompt = "a";
            Assert.True(clip.IsPreviewCurrent);
            Assert.Equal("PREVIEW", clip.TwoPassBadge);
            Assert.Equal(path, clip.PreviewVideoPath);

            clip.Prompt = "b";
            Assert.False(clip.IsPreviewCurrent);
            Assert.Equal("EDITED", clip.TwoPassBadge);

            clip.PreviewedPrompt = "b";
            clip.GuideChanged = true;
            Assert.Equal("RE-JOIN", clip.TwoPassBadge);

            clip.GuideChanged = false;
            clip.OutputPath = path;
            Assert.Equal("FINAL", clip.TwoPassBadge);
        }
        finally { File.Delete(path); }
    }
}

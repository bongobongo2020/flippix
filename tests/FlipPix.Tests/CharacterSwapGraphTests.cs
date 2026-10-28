using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// The 🔁 Character Swap tab prunes Openjutsu's API exports per request (<see cref="CharacterSwapGraph"/>,
/// a port of its lib/simple.ts, lib/advanced.ts and lib/model.ts). These are the same checks upstream's
/// <c>npm test</c> makes against the exports: every option combination leaves a graph with no link to a
/// removed node, and the values the tab sets land where H3 reads them.
/// </summary>
public sealed class CharacterSwapGraphTests
{
    private static JsonObject Load(string file)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "workflow", "video", "h3-minimax", "character-swap", file);
        Assert.True(File.Exists(path), $"the Character Swap graph is not in the output: {path}");
        var graph = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        CharacterSwapGraph.SilenceWhenSoundless(graph);
        return graph;
    }

    private static string Class(JsonObject g, string id) => g[id]!["class_type"]!.GetValue<string>();

    public static TheoryData<bool, bool, int> Options()
    {
        var data = new TheoryData<bool, bool, int>();
        foreach (var hat in new[] { false, true })
            foreach (var lip in new[] { false, true })
                foreach (var motion in CharacterSwapGraph.MotionSizes)
                    data.Add(hat, lip, motion);
        return data;
    }

    [Theory]
    [MemberData(nameof(Options))]
    public void Simple_prunes_cleanly_for_every_option(bool hatFix, bool noLipSync, int motion)
    {
        var s = new SwapSettings { HatFix = hatFix, NoLipSync = noLipSync, MotionSize = motion, Roles = { "  the   singer " }, Seed = 42, Resolution = "480p" };
        var g = CharacterSwapGraph.PruneSimple(Load(CharacterSwapGraph.SimpleFile), s);
        CharacterSwapGraph.ApplyModel(g, new SwapModel());
        CharacterSwapGraph.AssertNoDanglingLinks(g);

        var plan = g[CharacterSwapGraph.SimpleNodes.Plan]!["inputs"]!;
        Assert.Equal("the singer", plan["roles"]!.GetValue<string>());
        Assert.Equal(42, plan["seed"]!.GetValue<long>());
        Assert.Equal("480p", plan["resolution"]!.GetValue<string>());
        Assert.Equal(hatFix, g.ContainsKey(CharacterSwapGraph.SimpleNodes.Hat[0]));
        Assert.Equal(noLipSync, g.Any(kv => CharacterSwapGraph.TitleOf(kv.Value) == "No lip-sync in the prompt"));
        Assert.Equal(motion != 512, g.ContainsKey("motion_run_name"));
        Assert.Equal("MiniMaxH3ChainAssemble", Class(g, CharacterSwapGraph.NodeByTitle(g, CharacterSwapGraph.FinalVideoTitle)));
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, true, true)]
    [InlineData(3, false, true)]
    [InlineData(4, true, false)]
    public void Advanced_keeps_only_the_pictures_it_uses(int pictures, bool hatFix, bool noLipSync)
    {
        var s = new SwapSettings { HatFix = hatFix, NoLipSync = noLipSync, Roles = { "the drummer", "" , "the bassist", "" } };
        var g = CharacterSwapGraph.PruneAdvanced(Load(CharacterSwapGraph.AdvancedFile), pictures, s);
        CharacterSwapGraph.ApplyModel(g, new SwapModel());
        CharacterSwapGraph.AssertNoDanglingLinks(g);

        Assert.Equal(pictures, CharacterSwapGraph.PictureCount(g));
        var roles = g[CharacterSwapGraph.NodeByTitle(g, CharacterSwapGraph.AdvancedPlanTitle)]!["inputs"]!["roles"]!.GetValue<string>();
        Assert.Equal(pictures, roles.Split('\n').Length);
        Assert.StartsWith("the drummer", roles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mask_check_saves_its_preview(bool hatFix)
    {
        var g = CharacterSwapGraph.PruneMask(Load(CharacterSwapGraph.MaskFile), hatFix, SwapMargin.Loose, "cat");
        CharacterSwapGraph.AssertNoDanglingLinks(g);
        Assert.Equal("cat", g[CharacterSwapGraph.MaskNodes.Target]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal(60, g[CharacterSwapGraph.MaskNodes.GrowMask]!["inputs"]!["expand"]!.GetValue<int>());
        Assert.True(g[CharacterSwapGraph.MaskNodes.Preview]!["inputs"]!["save_output"]!.GetValue<bool>());
    }

    [Fact]
    public void Lora_chain_replaces_the_DMAD_node_or_links_straight_through()
    {
        var none = CharacterSwapGraph.PruneSimple(Load(CharacterSwapGraph.SimpleFile), new SwapSettings());
        CharacterSwapGraph.ApplyModel(none, new SwapModel { Loras = new(), Steps = 20 });
        CharacterSwapGraph.AssertNoDanglingLinks(none);
        Assert.DoesNotContain(none, kv => Class(none, kv.Key) == "LoraLoaderModelOnly");

        var two = CharacterSwapGraph.PruneSimple(Load(CharacterSwapGraph.SimpleFile), new SwapSettings());
        CharacterSwapGraph.ApplyModel(two, new SwapModel { Loras = new() { new("a.safetensors", 1), new("b.safetensors", 0.5) } });
        CharacterSwapGraph.AssertNoDanglingLinks(two);
        Assert.Equal(2, two.Count(kv => Class(two, kv.Key) == "LoraLoaderModelOnly"));
        Assert.True(two.ContainsKey("model_run_name"));
    }

    [Fact]
    public void Soundless_videos_read_silence()
    {
        var g = Load(CharacterSwapGraph.SimpleFile);
        var video = CharacterSwapGraph.SimpleNodes.Video;
        Assert.Equal("H3SoundtrackOrSilence", Class(g, $"{video}_soundtrack"));
        Assert.Equal("AudioConcat", Class(g, $"{video}_soundtrack_padded"));
        // Nothing reads the raw audio output any more; everything reads the padded soundtrack.
        Assert.DoesNotContain(g, kv => kv.Key != $"{video}_soundtrack"
            && kv.Value!["inputs"]!.AsObject().Any(i => CharacterSwapGraph.IsLink(i.Value, out var src, out var slot) && src == video && slot == 2));
        CharacterSwapGraph.AssertNoDanglingLinks(g);
    }

    [Fact]
    public void Installed_names_win_over_upstream_names()
    {
        var installed = new[] { "h3-minimax/minimax_h3_ref2va_pruned_int8_convrot.safetensors", "qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors" };
        Assert.Equal(installed[0], CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultModel, installed));
        Assert.Equal(installed[1], CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultTextEncoder, installed, "qwen3vl_32b_minimax_h3"));
    }

    [Theory]
    [InlineData(6, 6, 1)]
    [InlineData(18, 6, 4)]
    [InlineData(0.1, 6, 0)]
    public void Clip_estimate_matches_upstream(double seconds, double clip, int expected) =>
        Assert.Equal(expected, CharacterSwapGraph.ClipCount(seconds, clip));
}

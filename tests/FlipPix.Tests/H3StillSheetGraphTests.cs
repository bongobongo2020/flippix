using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// Story mode's character sheets are rendered by MiniMax H3 as a single-frame still through the Fizgig H3 Still
/// nodes (<see cref="H3StillSheetGraph"/>). These check the wiring the pack's README prescribes.
/// </summary>
public sealed class H3StillSheetGraphTests
{
    private static JsonObject Graph(H3StillSheetGraph.Models? m = null) =>
        H3StillSheetGraph.Build(m ?? H3StillSheetGraph.DefaultModels, "cast.png", "prompt", 7, "h3_story/sheet");

    private static string Cls(JsonObject g, string id) => g[id]!["class_type"]!.GetValue<string>();
    private static JsonObject In(JsonObject g, string id) => g[id]!["inputs"]!.AsObject();
    private static string Src(JsonObject g, string id, string input) => In(g, id)[input]!.AsArray()[0]!.GetValue<string>();

    [Fact]
    public void Sampler_runs_on_the_fizgig_still_latent_and_decodes_with_fizgig()
    {
        var g = Graph();
        Assert.Equal(H3StillSheetGraph.NodeLatent, Cls(g, Src(g, "sample", "latent_image")));
        Assert.Equal(H3StillSheetGraph.NodeDecode, Cls(g, Src(g, H3StillSheetGraph.SaveNode, "images")));
        Assert.Equal("sample", Src(g, "decode", "samples"));
    }

    [Fact]
    public void Photo_is_picture_one_of_the_reference_node_and_sizes_match()
    {
        var g = Graph();
        Assert.Equal("MiniMaxH3ReferenceToVideo", Cls(g, "ref"));
        Assert.Equal("img1", Src(g, "ref", "ref_images.ref_image_0"));
        Assert.Equal("cast.png", In(g, "img1")["image"]!.GetValue<string>());
        Assert.Equal(In(g, "ref")["width"]!.GetValue<int>(), In(g, "latent")["width"]!.GetValue<int>());
        Assert.Equal(In(g, "ref")["height"]!.GetValue<int>(), In(g, "latent")["height"]!.GetValue<int>());
        Assert.Equal(0, In(g, "latent")["width"]!.GetValue<int>() % 32);
        Assert.True(In(g, "latent")["width"]!.GetValue<int>() * In(g, "latent")["height"]!.GetValue<int>() >= 3_000_000);
    }

    [Fact]
    public void Without_a_turbo_lora_the_model_feeds_the_sampler_directly_at_more_steps()
    {
        var m = H3StillSheetGraph.DefaultModels with { Lora = null };
        var g = Graph(m);
        Assert.False(g.ContainsKey("lora"));
        Assert.Equal("unet", Src(g, "guider", "model"));
        Assert.True(In(g, "sched")["steps"]!.GetValue<int>() >= 50);
    }

    [Fact]
    public void Every_link_points_at_a_node_in_the_graph()
    {
        var g = Graph();
        foreach (var (_, node) in g)
            foreach (var (_, v) in node!["inputs"]!.AsObject())
                if (v is JsonArray a && a.Count == 2)
                    Assert.True(g.ContainsKey(a[0]!.GetValue<string>()));
    }

    [Fact]
    public void Resolve_prefers_the_installed_files()
    {
        var m = H3StillSheetGraph.Resolve(
            new[] { "h3-minimax/minimax_h3_fl2va_pruned_int8_convrot.safetensors" },
            new[] { "H3/larryminimax_h3_turbo_v4_step600_ema.safetensors" },
            new[] { "qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors" },
            new[] { "minimax_h3_video_vae_fp16.safetensors" });
        Assert.Equal("h3-minimax/minimax_h3_fl2va_pruned_int8_convrot.safetensors", m.Model);
        Assert.Equal("H3/larryminimax_h3_turbo_v4_step600_ema.safetensors", m.Lora);
        Assert.Equal("qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors", m.TextEncoder);
        Assert.Equal("minimax_h3_video_vae_fp16.safetensors", m.VideoVae);
    }

    [Fact]
    public void Prompt_names_picture_one_and_every_view()
    {
        var p = H3StillSheetGraph.BuildPrompt("a woman", "auburn hair", "a green jacket.");
        Assert.Contains("<Picture 1>", p);
        Assert.Contains("wearing a green jacket", p);
        Assert.All(H3StillSheetGraph.Views, v => Assert.Contains(v, p));
    }
}

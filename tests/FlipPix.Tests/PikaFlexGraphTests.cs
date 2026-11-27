using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.Core.Models;
using FlipPix.UI.Services;
using Xunit;

namespace FlipPix.Tests;

public class PikaFlexGraphTests
{
    private static FflfBeatRender Beat(string? dialogue = null) => new()
    {
        StartImage = "start.png",
        Prompt = "A runs across the roof.",
        Seconds = 5,
        Seed = 43,
        AspectRatio = "16:9 (Widescreen)",
        TextEncoder = "qwen3vl_32b_minimax_h3_int8_convrot.safetensors",
        ExtraLoras = new List<MiniMaxI2VLora> { new() { Name = "H3/extra.safetensors", Strength = 0.5 } },
        DialogueAudio = dialogue,
        OutputPrefix = "video/fflf/run/beat_01",
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

    [Fact]
    public void Build_FollowsTheAuthoredWorkflow()
    {
        var files = new PikaFlexFiles();
        var g = PikaFlexGraph.Build(Beat(), files, 0.6);
        AssertLinksResolve(g);

        Assert.Equal(PikaFlexGraph.AuthoredModel, Inputs(g, "pf_unet")["unet_name"]!.GetValue<string>());
        Assert.Equal(PikaFlexGraph.AuthoredPikaLora, Inputs(g, "pf_pika")["lora_name"]!.GetValue<string>());
        // Extra LoRA after Pika, before attention; ReDiverse on top of attention for the first step only.
        Assert.Equal("pf_pika", Source(g, "pf_extra_1", "model"));
        Assert.Equal("pf_extra_1", Source(g, "pf_attn", "model"));
        Assert.Equal("pf_attn", Source(g, "pf_rediverse", "model"));
        Assert.Equal("pf_rediverse", Source(g, "pf_preview1", "model"));
        Assert.Equal("pf_attn", Source(g, "pf_preview2", "model"));

        var i2v = Inputs(g, "pf_i2v");
        Assert.Equal(124, i2v["length"]!.GetValue<int>());
        Assert.Equal("pf_start", i2v["first_frame"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("start.png", Inputs(g, "pf_start")["image"]!.GetValue<string>());
        Assert.Equal(0.6, Inputs(g, "pf_size")["megapixels"]!.GetValue<double>());

        Assert.Equal("lcm", Inputs(g, "pf_sampler")["sampler_name"]!.GetValue<string>());
        Assert.Equal("1, 0.972973, 0.923077, 0.8, 0.571429, 0.333333, 0", Inputs(g, "pf_sigmas")["sigmas"]!.GetValue<string>());
        Assert.Equal(1, Inputs(g, "pf_split")["step"]!.GetValue<int>());
        Assert.Equal("pf_pass1", Source(g, "pf_pass2", "latent_image"));
        Assert.Equal(1, Inputs(g, "pf_pass2")["sigmas"]!.AsArray()[1]!.GetValue<int>());

        Assert.Equal("video/fflf/run/beat_01", Inputs(g, PikaFlexGraph.VideoSaveNode)["filename_prefix"]!.GetValue<string>());
        Assert.Equal("video/fflf/run/beat_01_last", Inputs(g, PikaFlexGraph.LastFrameSaveNode)["filename_prefix"]!.GetValue<string>());
        Assert.DoesNotContain(g, kv => kv.Key.StartsWith("pf_dlg"));
    }

    [Fact]
    public void Build_WithDialogue_LocksBothPassesAndExportsTheExactAudio()
    {
        var g = PikaFlexGraph.Build(Beat("line.wav"), new PikaFlexFiles(), 0.6);
        AssertLinksResolve(g);

        Assert.Equal(PikaFlexGraph.DialogueLockPass1Node, Source(g, "pf_guider1", "model"));
        Assert.Equal(PikaFlexGraph.DialogueLockPass1Node, Source(g, "pf_pass1", "latent_image"));
        Assert.Equal(PikaFlexGraph.DialogueLockPass2Node, Source(g, "pf_guider2", "model"));
        Assert.Equal(PikaFlexGraph.DialogueLockPass2Node, Source(g, "pf_pass2", "latent_image"));
        Assert.Equal("pf_pass1", Source(g, PikaFlexGraph.DialogueLockPass2Node, "av_latent"));
        Assert.Equal(PikaFlexGraph.DialogueLockPass1Node, Source(g, "pf_video", "audio"));
        Assert.Equal(2, Inputs(g, "pf_video")["audio"]!.AsArray()[1]!.GetValue<int>());
    }

    [Fact]
    public void Build_UsesThePickedFilesAndStrengths()
    {
        var files = new PikaFlexFiles
        {
            DiffusionModel = "h3-minimax/minimax_h3_fl2va_bf16.safetensors",
            PikaLora = "H3/pika_h3_flex_fl2va_comfy_bf16.safetensors", PikaStrength = 0.8,
            RediverseLora = "H3/rediverse_ft_v2.safetensors", RediverseStrength = 0.6,
        };
        var g = PikaFlexGraph.Build(Beat(), files, 0.6);
        Assert.Equal(files.DiffusionModel, Inputs(g, "pf_unet")["unet_name"]!.GetValue<string>());
        Assert.Equal(files.PikaLora, Inputs(g, "pf_pika")["lora_name"]!.GetValue<string>());
        Assert.Equal(0.8, Inputs(g, "pf_pika")["strength_model"]!.GetValue<double>());
        Assert.Equal(files.RediverseLora, Inputs(g, "pf_rediverse")["lora_name"]!.GetValue<string>());
        Assert.Equal(0.6, Inputs(g, "pf_rediverse")["strength_model"]!.GetValue<double>());
    }

    [Fact]
    public void Resolve_FindsTheServerFolder()
    {
        var server = new[] { "H3/other.safetensors", "H3/pika/pika_h3_flex_fl2va_v1_bf16.safetensors" };
        Assert.Equal("H3/pika/pika_h3_flex_fl2va_v1_bf16.safetensors", PikaFlexGraph.Resolve(server, PikaFlexGraph.AuthoredPikaLora));
        Assert.Equal(PikaFlexGraph.AuthoredRediverseLora, PikaFlexGraph.Resolve(server, PikaFlexGraph.AuthoredRediverseLora));
    }

    [Fact]
    public void Resolve_TakesTheSameLoraUnderAnotherName()
    {
        var server = new[] { "H3/pika_h3_flex_fl2va_comfy_bf16.safetensors", "H3/rediverse_ft_v2.safetensors", "H3/other.safetensors" };
        Assert.Equal("H3/pika_h3_flex_fl2va_comfy_bf16.safetensors", PikaFlexGraph.Resolve(server, PikaFlexGraph.AuthoredPikaLora));
        Assert.Equal("H3/rediverse_ft_v2.safetensors", PikaFlexGraph.Resolve(server, PikaFlexGraph.AuthoredRediverseLora));

        var models = new[] { "h3-minimax/minimax_h3_fl2va_bf16.safetensors", "h3-minimax/minimax_h3_fl2va_pruned_int8.safetensors" };
        Assert.Equal("h3-minimax/minimax_h3_fl2va_pruned_int8.safetensors", PikaFlexGraph.Resolve(models, PikaFlexGraph.AuthoredModel));
    }
}

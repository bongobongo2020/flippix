using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.UI.Services;
using Xunit;

namespace FlipPix.Tests;

/// <summary>✨ Face refine: the H3 FaceRefine Plus graph run on a finished beat.</summary>
public class FaceRefinePlusGraphTests
{
    private static FaceRefineRequest Request(bool audio = true) => new()
    {
        Video = "beat_03.mp4",
        HasAudio = audio,
        FrameCap = 243,
        IdentityImage = "opening.png",
        Prompt = FaceRefinePlusGraph.ComposePrompt("She turns to the window.", "Film still."),
        Models = new FaceRefineModels
        {
            DiffusionModel = "h3-minimax\\model.safetensors",
            TextEncoder = "qwen3vl.safetensors",
            Loras = new[] { new FaceRefineLora("H3/turbo.safetensors", 0.25) },
        },
        Strength = 0.30,
        Seed = 7,
        OutputPrefix = "video/fflf/run/beat_03_face",
    };

    private static string Type(JsonObject g, string id) => g[id]!["class_type"]!.GetValue<string>();

    private static JsonArray Link(JsonObject g, string id, string input) => g[id]!["inputs"]![input]!.AsArray();

    private static string? NodeOfType(JsonObject g, string type) =>
        g.Where(kv => kv.Value!["class_type"]!.GetValue<string>() == type).Select(kv => kv.Key).FirstOrDefault();

    [Theory]
    [InlineData(243, 243)]
    [InlineData(250, 243)]
    [InlineData(260, 260)]
    [InlineData(22, 22)]
    [InlineData(10, 5)]
    public void H3Length_IsTheLargest17kPlus5ThatFits(int frames, int expected)
    {
        Assert.Equal(expected, FaceRefinePlusGraph.H3Length(frames));
        Assert.Equal(0, (expected - 5) % 17);
    }

    [Fact]
    public void FrameCap_FromDuration()
    {
        Assert.Equal(243, FaceRefinePlusGraph.FrameCapFor(243 / 24.0));
        Assert.Equal(0, FaceRefinePlusGraph.FrameCapFor(0));
    }

    [Fact]
    public void EveryLink_PointsAtANodeInTheGraph()
    {
        foreach (var audio in new[] { true, false })
        {
            var g = FaceRefinePlusGraph.Build(Request(audio));
            foreach (var (id, node) in g)
                foreach (var (input, value) in node!["inputs"]!.AsObject())
                    if (value is JsonArray link && link.Count == 2 && link[0] is JsonValue v && v.TryGetValue<string>(out var target))
                        Assert.True(g.ContainsKey(target), $"{id}.{input} → missing node {target}");
        }
    }

    [Fact]
    public void Pipeline_IsTheFaceRefinePlusRecipe()
    {
        var g = FaceRefinePlusGraph.Build(Request());
        var track = NodeOfType(g, "H3FaceTrackCrop")!;
        var load = NodeOfType(g, "VHS_LoadVideo")!;
        Assert.Equal("beat_03.mp4", g[load]!["inputs"]!["video"]!.GetValue<string>());
        Assert.Equal(243, g[load]!["inputs"]!["frame_load_cap"]!.GetValue<int>());

        // The H3 latent follows the crop canvas and the tracked length.
        var r2v = NodeOfType(g, "MiniMaxH3ReferenceToVideo")!;
        Assert.Equal(track, Link(g, r2v, "width")[0]!.GetValue<string>());
        Assert.Equal(6, Link(g, r2v, "length")[1]!.GetValue<int>());
        Assert.Equal("LoadImage", Type(g, Link(g, r2v, "ref_images.ref_image_0")[0]!.GetValue<string>()));
        Assert.StartsWith("<Picture 1>", g[r2v]!["inputs"]!["prompt"]!.GetValue<string>());

        // Per-frame denoise is in the model path: its MODEL output feeds the guider and scheduler.
        var perFrame = NodeOfType(g, "H3PerFrameDenoise")!;
        var guider = NodeOfType(g, "BasicGuider")!;
        var scheduler = NodeOfType(g, "BasicScheduler")!;
        Assert.Equal(perFrame, Link(g, guider, "model")[0]!.GetValue<string>());
        Assert.Equal(2, Link(g, guider, "model")[1]!.GetValue<int>());
        Assert.Equal(2, Link(g, scheduler, "model")[1]!.GetValue<int>());
        Assert.Equal(0.30, g[scheduler]!["inputs"]!["denoise"]!.GetValue<double>(), 3);
        Assert.Equal("MiniMaxH3NativeAudioLock", Type(g, Link(g, perFrame, "model")[0]!.GetValue<string>()));

        // Stitch Plus → Flow Lock → Tone Match → the video; base frames are the take.
        var stitch = NodeOfType(g, "H3FaceStitchPlus")!;
        var flow = NodeOfType(g, "H3FaceFlowLock")!;
        var tone = NodeOfType(g, "H3FaceToneMatch")!;
        Assert.Equal(load, Link(g, stitch, "base_images")[0]!.GetValue<string>());
        Assert.Equal(stitch, Link(g, flow, "refined_images")[0]!.GetValue<string>());
        Assert.Equal(flow, Link(g, tone, "processed_images")[0]!.GetValue<string>());
        var video = NodeOfType(g, "CreateVideo")!;
        Assert.Equal(tone, Link(g, video, "images")[0]!.GetValue<string>());
        Assert.Equal(load, Link(g, video, "audio")[0]!.GetValue<string>());
        Assert.Equal("SaveVideo", Type(g, FaceRefinePlusGraph.VideoSaveNode));
        Assert.Equal("SaveImage", Type(g, FaceRefinePlusGraph.LastFrameSaveNode));

        // Model names are sent with forward slashes, the LoRA on a Power LoRA stack.
        Assert.Equal("h3-minimax/model.safetensors", g[NodeOfType(g, "UNETLoader")!]!["inputs"]!["unet_name"]!.GetValue<string>());
        Assert.NotNull(NodeOfType(g, "H3PowerLoraStack"));
    }

    [Fact]
    public void SilentTake_HasNoAudioLockAndNoAudioWire()
    {
        var g = FaceRefinePlusGraph.Build(Request(audio: false));
        Assert.Null(NodeOfType(g, "MiniMaxH3NativeAudioLock"));
        var r2v = NodeOfType(g, "MiniMaxH3ReferenceToVideo")!;
        Assert.False(g[r2v]!["inputs"]!.AsObject().ContainsKey("ref_audios.ref_audio_0"));
        Assert.False(g[NodeOfType(g, "CreateVideo")!]!["inputs"]!.AsObject().ContainsKey("audio"));
        var perFrame = NodeOfType(g, "H3PerFrameDenoise")!;
        Assert.Equal("H3PowerLoraStack", Type(g, Link(g, perFrame, "model")[0]!.GetValue<string>()));
    }

    [Fact]
    public void TwoPassTemplate_SuppliesTheTextEncoderAndVaes()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax", "h3-erosmax-2pass.json");
        if (!File.Exists(path)) return;
        var m = FaceRefinePlusGraph.FromTwoPassTemplate(File.ReadAllText(path), "model.safetensors", Array.Empty<FaceRefineLora>());
        Assert.Contains("qwen3vl", m.TextEncoder);
        Assert.Contains("video_vae", m.VideoVae);
        Assert.Contains("audio_vae", m.AudioVae);
    }
}

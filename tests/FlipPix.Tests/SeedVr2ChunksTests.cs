using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

public class SeedVr2ChunksTests
{
    [Fact]
    public void ShortClipIsOnePieceWithNoOverlap()
    {
        var plan = SeedVr2Chunks.Plan(200, 240);
        Assert.False(plan.IsChunked);
        Assert.Equal(new SeedVr2Chunk(0, 0, 200), Assert.Single(plan.Chunks));
        Assert.Equal(0, plan.OverlapFrames);
    }

    [Theory]
    [InlineData(2160, 240)]
    [InlineData(2161, 114)]
    [InlineData(241, 240)]
    [InlineData(1000, 61)]
    [InlineData(30000, 480)]
    public void PiecesCoverTheClipExactlyAndOverlapByTheSameAmount(int total, int max)
    {
        var plan = SeedVr2Chunks.Plan(total, max);
        Assert.True(plan.IsChunked);
        Assert.Equal(0, plan.Chunks[0].Start);
        Assert.Equal(total, plan.Chunks[^1].End);
        for (var i = 1; i < plan.Chunks.Count; i++)
            Assert.Equal(SeedVr2Chunks.OverlapFrames, plan.Chunks[i - 1].End - plan.Chunks[i].Start);
        Assert.All(plan.Chunks, c => Assert.InRange(c.Length, SeedVr2Chunks.OverlapFrames + 1, max));
        // Even pieces: no runt at the end.
        Assert.True(plan.Chunks.Max(c => c.Length) - plan.Chunks.Min(c => c.Length) <= 1);
        // Fewest pieces that fit.
        var fewer = plan.Chunks.Count - 1;
        Assert.True(fewer * max - (fewer - 1) * SeedVr2Chunks.OverlapFrames < total);
    }

    [Fact]
    public void RamBudgetShrinksPiecesAtHigherScaleAndStaysInBounds()
    {
        const long gb = 1024L * 1024 * 1024;
        var at2x = SeedVr2Chunks.MaxFramesForRam(960, 544, 2.0, 28 * gb);
        var at3x = SeedVr2Chunks.MaxFramesForRam(960, 544, 3.0, 28 * gb);
        Assert.True(at3x < at2x);
        Assert.InRange(at2x, SeedVr2Chunks.MinChunkFrames, SeedVr2Chunks.MaxChunkFrames);
        Assert.Equal(SeedVr2Chunks.MinChunkFrames, SeedVr2Chunks.MaxFramesForRam(3840, 2160, 3.0, 4 * gb));
        Assert.Equal(SeedVr2Chunks.MaxChunkFrames, SeedVr2Chunks.MaxFramesForRam(320, 180, 1.25, 500 * gb));
        Assert.Equal(SeedVr2Chunks.MaxFramesForRam(960, 544, 2.0, SeedVr2Chunks.FallbackFreeRamBytes),
            SeedVr2Chunks.MaxFramesForRam(960, 544, 2.0, 0));
    }

    [Fact]
    public void CutReadsExactlyThePieceFramesWithoutAudio()
    {
        var args = SeedVr2Chunks.CutArguments("in.mp4", new SeedVr2Chunk(2, 448, 240), 24, 1, "c2.mp4");
        Assert.Contains("trim=start_frame=448:end_frame=688", args);
        Assert.Contains("setpts=N/(24)/TB", args);
        Assert.Contains("-an", args);
        Assert.Contains("-qp 0", args);
    }

    [Fact]
    public void JoinCrossfadesAtEachSeamAndKeepsTheSourceAudio()
    {
        var plan = SeedVr2Chunks.Plan(500, 200);   // 3 pieces of 177/177/178... sharing 16 frames
        var pieces = plan.Chunks.Select(c => $"u{c.Index}.mp4").ToList();
        var args = SeedVr2Chunks.JoinArguments(pieces, plan, 24, 1, "src.mp4", true, "out.mp4");

        Assert.Equal(plan.Chunks.Count - 1, CountOf(args, "xfade="));
        var firstOffset = (plan.Chunks[0].Length - 16) / 24.0;
        Assert.Contains($"offset={firstOffset.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}", args);
        Assert.Contains($"-map {pieces.Count}:a:0?", args);
        Assert.Contains("-frames:v 500", args);
        Assert.Contains("-c:a copy", args);
        Assert.Contains("[v]", args);
    }

    [Fact]
    public void NtscRateIsKeptRational()
    {
        Assert.Equal("30000/1001", SeedVr2Chunks.RateExpression(30000, 1001));
        Assert.Equal("24", SeedVr2Chunks.RateExpression(24, 1));
    }

    [Fact]
    public void ChunkPatchMatchesColourDropsAudioAndNamesThePiece()
    {
        var g = JsonNode.Parse("""
        {
          "125": {"class_type": "VHS_LoadVideoFFmpeg", "inputs": {"video": "x.mp4"}},
          "111": {"class_type": "ResizeImageMaskNode", "inputs": {"resize_type.multiplier": 1.5}},
          "109": {"class_type": "KSampler", "inputs": {"seed": 1}},
          "113": {"class_type": "SeedVR2PostProcessing", "inputs": {"color_correction_method": "none"}},
          "124": {"class_type": "VHS_VideoCombine", "inputs": {"crf": 19, "filename_prefix": "AnimateDiff", "audio": ["125", 2]}}
        }
        """)!.AsObject();
        SeedVr2Chunks.PatchForChunk(g, "125", "111", "resize_type.multiplier", "109", "113", "124",
            "piece_03.mp4", 2.0, 42, "upscale/SVR2_abc_c03");

        Assert.Equal("piece_03.mp4", (string)g["125"]!["inputs"]!["video"]!);
        Assert.Equal(2.0, (double)g["111"]!["inputs"]!["resize_type.multiplier"]!);
        Assert.Equal(42L, (long)g["109"]!["inputs"]!["seed"]!);
        Assert.Equal("lab", (string)g["113"]!["inputs"]!["color_correction_method"]!);
        Assert.Equal("upscale/SVR2_abc_c03", (string)g["124"]!["inputs"]!["filename_prefix"]!);
        Assert.Equal(SeedVr2Chunks.ChunkCrf, (int)g["124"]!["inputs"]!["crf"]!);
        Assert.False(g["124"]!["inputs"]!.AsObject().ContainsKey("audio"));
    }

    private static int CountOf(string s, string sub) => (s.Length - s.Replace(sub, "").Length) / sub.Length;
}

using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.UI.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// 🌀 MiniMax I2V's location plate: the Qwen Image graph it renders on, and the reference slot that remembers a
/// plate is one — so the next Analyze reuses it and the prompt writer is told it is the setting.
/// </summary>
public class LocationPlateTests
{
    [Fact]
    public async Task The_plate_graph_carries_the_prompt_the_canvas_and_the_prefix()
    {
        var (json, save) = await CastPhotoWorkflows.BuildLocationPlateAsync(
            "minimax_i2v/plate_x", 42, "A photorealistic wide establishing photograph of an alley.", 1701, 957);
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("SaveImage", root[save]!["class_type"]!.GetValue<string>());
        Assert.Equal("minimax_i2v/plate_x", root[save]!["inputs"]!["filename_prefix"]!.GetValue<string>());

        var latent = root.Select(kv => kv.Value!).First(n => n["class_type"]!.GetValue<string>() == "EmptySD3LatentImage");
        Assert.Equal(1696, latent["inputs"]!["width"]!.GetValue<int>());    // floored to 16
        Assert.Equal(944, latent["inputs"]!["height"]!.GetValue<int>());

        Assert.Contains(root.Select(kv => kv.Value!),
            n => n["class_type"]!.GetValue<string>() == "CLIPTextEncode" &&
                 n["inputs"]!["text"]?.GetValue<string>() == "A photorealistic wide establishing photograph of an alley.");
    }

    [Fact]
    public void A_slot_given_a_plate_says_so_and_forgets_it_when_the_picture_changes()
    {
        var slot = new MiniMaxI2VReference(3);
        Assert.False(slot.IsLocationPlate);

        slot.SetLocationPlate(@"C:\nowhere\plate.png");
        Assert.True(slot.IsLocationPlate);
        Assert.Contains("location plate", slot.Label);

        slot.Path = @"C:\nowhere\my_own_picture.png";
        Assert.False(slot.IsLocationPlate);
        Assert.Equal("Picture 3", slot.Label);
    }

    [Fact]
    public void The_plate_prompt_ships_with_the_app()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "h3-location-plate.md");
        Assert.True(File.Exists(path), path);
        Assert.Contains("no people", File.ReadAllText(path));
    }
}

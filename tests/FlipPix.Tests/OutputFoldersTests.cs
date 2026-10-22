using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;

namespace FlipPix.Tests;

/// <summary>
/// OutputFolders.RouteSaves moves a graph's saves out of the top of ComfyUI's output folder into a tab's
/// own folder, without touching anything that already names one or is wired from another node.
/// </summary>
public sealed class OutputFoldersTests
{
    private static JsonElement Graph(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string? PrefixOf(JsonElement graph, string node) =>
        JsonNode.Parse(graph.GetRawText())![node]!["inputs"]!["filename_prefix"] is JsonValue v &&
        v.TryGetValue<string>(out var s) ? s : null;

    [Fact]
    public void PlainPrefix_GoesIntoTheFolder()
    {
        var routed = OutputFolders.RouteSaves(Graph("""
            {"9":{"class_type":"SaveImage","inputs":{"filename_prefix":"Krea2","images":["8",0]}}}
            """), "Krea2");

        Assert.Equal("Krea2/Krea2", PrefixOf(routed, "9"));
    }

    [Fact]
    public void PrefixThatNamesAFolder_IsLeftAlone()
    {
        var routed = OutputFolders.RouteSaves(Graph("""
            {"9":{"class_type":"SaveImage","inputs":{"filename_prefix":"ZImage/2026/ZI"}},
             "10":{"class_type":"SaveImage","inputs":{"filename_prefix":"win\\style"}}}
            """), "Moss");

        Assert.Equal("ZImage/2026/ZI", PrefixOf(routed, "9"));
        Assert.Equal("win\\style", PrefixOf(routed, "10"));
    }

    [Fact]
    public void WiredPrefix_AndNodesWithoutOne_AreLeftAlone()
    {
        var routed = OutputFolders.RouteSaves(Graph("""
            {"48":{"class_type":"VHS_VideoCombine","inputs":{"filename_prefix":["49",0]}},
             "49":{"class_type":"StringConstant","inputs":{"string":"wan_vace/wan_vace"}},
             "3":{"class_type":"KSampler","inputs":{"seed":1234567890123456789}}}
            """), "video");

        var root = JsonNode.Parse(routed.GetRawText())!;
        Assert.IsType<JsonArray>(root["48"]!["inputs"]!["filename_prefix"]);
        Assert.Equal("wan_vace/wan_vace", root["49"]!["inputs"]!["string"]!.GetValue<string>());
        // Large integers (seeds) must come through unchanged.
        Assert.Equal(1234567890123456789L, root["3"]!["inputs"]!["seed"]!.GetValue<long>());
    }

    [Fact]
    public void NoFolder_ReturnsTheGraphUnchanged()
    {
        var graph = Graph("""{"9":{"class_type":"SaveImage","inputs":{"filename_prefix":"ZI"}}}""");

        Assert.Equal(graph.GetRawText(), OutputFolders.RouteSaves(graph, null).GetRawText());
    }

    [Fact]
    public void ObjectModelGraph_IsRoutedToo()
    {
        var graph = new Dictionary<string, object>
        {
            ["12"] = new Dictionary<string, object>
            {
                ["class_type"] = "SaveImage",
                ["inputs"] = new Dictionary<string, object> { ["filename_prefix"] = "Moss" },
            },
        };

        var routed = (JsonElement)OutputFolders.RouteSaves((object)graph, "Moss");

        Assert.Equal("Moss/Moss", PrefixOf(routed, "12"));
    }

    [Theory]
    [InlineData(TextGeneratorWorkflow.Moss, "Moss")]
    [InlineData(TextGeneratorWorkflow.Qwen21Enhancer, "Qwen21")]
    [InlineData(TextGeneratorWorkflow.Klien, "F2K_txt2img")]
    [InlineData(TextGeneratorWorkflow.Zimage, null)]
    public void TextWorkflows_HaveTheFolderTheirFilesWereTidiedInto(TextGeneratorWorkflow workflow, string? folder)
    {
        Assert.Equal(folder, OutputFolders.ForTextWorkflow(workflow));
    }
}

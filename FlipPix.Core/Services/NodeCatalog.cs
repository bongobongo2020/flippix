using System;
using System.Collections.Generic;

namespace FlipPix.Core.Services;

/// <summary>
/// Maps a ComfyUI node class to the git repo of the custom-node pack that provides it, so the
/// missing-node resolver can offer an automatic install without the user hunting for the pack.
///
/// This is a curated offline fallback covering the packs the FlipPix workflows use (plus a few
/// common ones). The resolver's primary source is the running ComfyUI-Manager's node map
/// (/customnode/getmappings), which covers thousands of packs; this catalog answers when Manager
/// isn't reachable or doesn't know the node.
/// </summary>
public static class NodeCatalog
{
    // Node class (case-insensitive) -> providing pack's git URL. Seeded from
    // scripts/flippix-custom-nodes.txt (whose inline comments name each pack's node classes),
    // extended with node packs the shipped workflows reference that aren't Manager-resolvable.
    private static readonly Dictionary<string, string> _repoByClass =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // --- ControlAltAI-Nodes (Flux Resolution Calc / Flux Sampler) ---
        ["FluxResolutionNode"] = "https://github.com/gseth/ControlAltAI-Nodes",
        ["FluxSamplerNode"] = "https://github.com/gseth/ControlAltAI-Nodes",

        // --- kijai stack ---
        ["ImageResizeKJv2"] = "https://github.com/kijai/ComfyUI-KJNodes",
        ["GetImageSizeAndCount"] = "https://github.com/kijai/ComfyUI-KJNodes",
        ["GrowMaskWithBlur"] = "https://github.com/kijai/ComfyUI-KJNodes",
        ["PointsEditor"] = "https://github.com/kijai/ComfyUI-KJNodes",
        ["ModelPatchTorchSettings"] = "https://github.com/kijai/ComfyUI-KJNodes",
        ["Florence2Run"] = "https://github.com/kijai/ComfyUI-Florence2",
        ["DownloadAndLoadFlorence2Model"] = "https://github.com/kijai/ComfyUI-Florence2",
        ["Sam2Segmentation"] = "https://github.com/kijai/ComfyUI-segment-anything-2",
        ["DownloadAndLoadSAM2Model"] = "https://github.com/kijai/ComfyUI-segment-anything-2",

        // --- video / frames / depth ---
        ["VHS_LoadVideo"] = "https://github.com/Kosinkadink/ComfyUI-VideoHelperSuite",
        ["VHS_VideoCombine"] = "https://github.com/Kosinkadink/ComfyUI-VideoHelperSuite",
        ["VHS_LoadAudio"] = "https://github.com/Kosinkadink/ComfyUI-VideoHelperSuite",
        ["CannyEdgePreprocessor"] = "https://github.com/Fannovel16/comfyui_controlnet_aux",
        ["DWPreprocessor"] = "https://github.com/Fannovel16/comfyui_controlnet_aux",
        ["AIO_Preprocessor"] = "https://github.com/Fannovel16/comfyui_controlnet_aux",

        // --- general utility packs ---
        ["ShowText|pysssss"] = "https://github.com/pythongosssss/ComfyUI-Custom-Scripts",
        ["MathExpression|pysssss"] = "https://github.com/pythongosssss/ComfyUI-Custom-Scripts",
        ["GetImageSize+"] = "https://github.com/cubiq/ComfyUI_essentials",
        ["ImageResize+"] = "https://github.com/cubiq/ComfyUI_essentials",
        ["SimpleMath+"] = "https://github.com/cubiq/ComfyUI_essentials",

        // --- model loaders ---
        ["UnetLoaderGGUF"] = "https://github.com/city96/ComfyUI-GGUF",
        ["CLIPLoaderGGUF"] = "https://github.com/city96/ComfyUI-GGUF",
        ["DualCLIPLoaderGGUF"] = "https://github.com/city96/ComfyUI-GGUF",
        ["UnetLoaderGGUFDisTorchMultiGPU"] = "https://github.com/pollockjj/ComfyUI-MultiGPU",

        // --- samplers / advanced ---
        ["ClownsharKSampler_Beta"] = "https://github.com/ClownsharkBatwing/RES4LYF",

        // --- metadata / saving ---
        ["Image Saver"] = "https://github.com/alexopus/ComfyUI-Image-Saver",
    };

    /// <summary>
    /// Returns the git URL of the pack that provides <paramref name="classType"/>, or null if the
    /// node isn't in this offline catalog.
    /// </summary>
    public static string? GetRepoUrl(string classType)
    {
        if (string.IsNullOrWhiteSpace(classType)) return null;
        return _repoByClass.TryGetValue(classType, out var url) ? url : null;
    }

    /// <summary>The pack name (repo leaf, minus a trailing ".git") for a git URL, e.g. "ComfyUI-KJNodes".</summary>
    public static string PackNameFromRepo(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return string.Empty;
        var trimmed = repoUrl.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var leaf = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        return leaf.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? leaf.Substring(0, leaf.Length - 4)
            : leaf;
    }
}

using System.IO;

namespace FlipPix.Core.Models;

/// <summary>
/// One model file a workflow needs but the connected ComfyUI does not expose. Carries enough
/// context (the ComfyUI combo value plus the resolved models sub-folder) for the resolver to
/// download or copy the file into the right place.
/// </summary>
public class MissingModelInfo
{
    /// <summary>
    /// The value exactly as ComfyUI expects it in the combo (relative to its category folder).
    /// For most models this is just the filename; for LoRAs it may include a subfolder
    /// (e.g. "qwen/mult-angles.safetensors").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The ComfyUI models sub-folder this file belongs in ("loras", "vae", "text_encoders",
    /// "diffusion_models", "checkpoints", "controlnet", "upscale_models", "clip_vision",
    /// "style_models"). Empty when it could not be inferred.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The ComfyUI node class that referenced this model (for diagnostics).</summary>
    public string ClassType { get; set; } = string.Empty;

    /// <summary>The bare filename, e.g. "z_image_turbo_bf16.safetensors".</summary>
    public string FileName => Path.GetFileName(Name.Replace('\\', '/'));

    /// <summary>
    /// The path of this model relative to the ComfyUI <c>models</c> root, using forward slashes,
    /// e.g. "text_encoders/gemma_2_2b_it_elm_bf16.safetensors" or "loras/qwen/foo.safetensors".
    /// </summary>
    public string RelativePath
    {
        get
        {
            var name = Name.Replace('\\', '/').TrimStart('/');
            return string.IsNullOrEmpty(Category) ? name : $"{Category}/{name}";
        }
    }
}

namespace FlipPix.UI.Models
{
    /// <summary>
    /// An optional character LoRA for Seedhunt, discovered by scanning ComfyUI's
    /// models/loras/LTX-23/characters folder for .safetensors files. When selected, it is
    /// injected into the Stage-1 Power Lora Loader (node 5078) so the hunted samples (and the
    /// finished video, which reuses the Stage-1 latent) carry the character.
    /// </summary>
    public sealed class SeedHuntCharacterLora
    {
        public SeedHuntCharacterLora(string displayName, string? relativePath)
        {
            DisplayName = displayName;
            RelativePath = relativePath;
        }

        /// <summary>Label shown in the picker (file name without extension, or "(none)").</summary>
        public string DisplayName { get; }

        /// <summary>
        /// LoRA path relative to the loras root, forward-slashed for ComfyUI
        /// (e.g. "LTX-23/characters/MyChar.safetensors"). Null = no LoRA (disabled).
        /// </summary>
        public string? RelativePath { get; }

        /// <summary>The "no character LoRA" sentinel (optional → default).</summary>
        public static SeedHuntCharacterLora None { get; } = new("(none)", null);

        public override string ToString() => DisplayName;
    }
}

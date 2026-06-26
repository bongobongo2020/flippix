namespace FlipPix.UI.Services
{
    /// <summary>
    /// Process-wide VRAM tier state, resolved once at startup from the user's setting plus the
    /// GPU VRAM ComfyUI reports via /system_stats. <see cref="WorkflowLocator"/> reads
    /// <see cref="IsLowVram"/> to decide whether to route workflow loads to the memory-optimized
    /// <c>workflow/16gb</c> folder, so 16 GB cards get workflows that fit instead of OOM-crashing.
    /// </summary>
    public static class VramContext
    {
        // Cards at or below this many GB are treated as "low VRAM" in auto mode. 16 GB GPUs sit
        // right at the line; the margin catches reported totals that fall a little short of 16.
        public const double LowVramThresholdGb = 17.0;

        /// <summary>GPU VRAM (GB) detected from /system_stats, or 0 if not yet known.</summary>
        public static double DetectedVramGb { get; private set; }

        /// <summary>The active tier: "full" or "16gb".</summary>
        public static string EffectiveTier { get; private set; } = "full";

        /// <summary>True when memory-optimized (16 GB) workflows should be preferred.</summary>
        public static bool IsLowVram => EffectiveTier == "16gb";

        /// <summary>
        /// Resolve the active tier from the user's <paramref name="vramTier"/> override
        /// ("auto" | "full" | "16gb") and the detected VRAM. In "auto" mode a card at or below
        /// <see cref="LowVramThresholdGb"/> selects the 16 GB tier.
        /// </summary>
        public static void Configure(string? vramTier, double detectedVramGb)
        {
            DetectedVramGb = detectedVramGb;

            var tier = (vramTier ?? "auto").Trim().ToLowerInvariant();
            EffectiveTier = tier switch
            {
                "16gb" => "16gb",
                "full" => "full",
                _ => (detectedVramGb > 0 && detectedVramGb <= LowVramThresholdGb) ? "16gb" : "full"
            };
        }
    }
}

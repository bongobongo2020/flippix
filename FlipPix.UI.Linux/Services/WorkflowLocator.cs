using System;
using System.IO;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>
    /// Central resolver for FlipPix workflow JSON files. ViewModels pass the normal relative path
    /// (e.g. <c>"workflow/video/ltx/seed-hunter-api.json"</c>) and get back an absolute path. When
    /// the app is in low-VRAM mode (<see cref="VramContext.IsLowVram"/>) and a same-named file
    /// exists under <c>workflow/16gb/...</c>, that memory-optimized variant is returned instead;
    /// otherwise the default full-size workflow is used. The fallback is graceful, so a call site
    /// can be migrated to this helper even before its 16 GB variant exists.
    /// </summary>
    public static class WorkflowLocator
    {
        /// <summary>
        /// Resolve a workflow path that begins with the <c>workflow</c> segment. Accepts either
        /// forward or back slashes.
        /// </summary>
        public static string Resolve(string relativeWorkflowPath)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            if (VramContext.IsLowVram)
            {
                var lowVramRelative = InsertSixteenGbSegment(relativeWorkflowPath);
                if (lowVramRelative != null)
                {
                    var candidate = Path.Combine(baseDir, lowVramRelative);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return Path.Combine(baseDir, relativeWorkflowPath);
        }

        /// <summary>
        /// Resolve from path segments, e.g. <c>Resolve("workflow", "image", "klein", "x.json")</c>.
        /// </summary>
        public static string Resolve(params string[] segments)
            => Resolve(string.Join("/", segments));

        // Turn "workflow/<rest>" into "workflow/16gb/<rest>". Returns null if the path doesn't
        // start with a "workflow" segment (so the caller falls back to the default location).
        private static string? InsertSixteenGbSegment(string relativeWorkflowPath)
        {
            var normalized = relativeWorkflowPath.Replace('\\', '/');
            const string prefix = "workflow/";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var rest = normalized.Substring(prefix.Length);
            return Path.Combine("workflow", "16gb", rest.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}

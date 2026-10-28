using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// Turns KJNodes' <c>kj_preview_override</c> messages (one per sampler step, from a graph carrying
    /// <c>ModelPreviewOverrideKJ</c>) into files the view can play: a JPEG of the starting noise first, then an MP4
    /// of the whole clip as it stands. Each is written to a small rotating set of temp files, so the player can
    /// still hold the previous one open while the next lands. Shared by 🌀 MiniMax I2V and 🎬 H3 Video Editor.
    /// </summary>
    internal sealed class LivePreviewFeed
    {
        private const int Slots = 6;

        private readonly string _dir;
        private int _counter;

        /// <summary>Which sampler of the run is previewing: each starts with a step-0 noise frame. Counted rather
        /// than read from the payload, because the node reports its own id for every pass.</summary>
        private int _pass;

        public LivePreviewFeed(string folderName)
        {
            _dir = Path.Combine(Path.GetTempPath(), "FlipPix", folderName);
            try
            {
                if (Directory.Exists(_dir))
                    foreach (var stale in Directory.EnumerateFiles(_dir)) TryDelete(stale);
            }
            catch { /* another instance may hold one open; it is overwritten on rotation anyway */ }
        }

        /// <summary>Starts counting passes again — call at the start of each run.</summary>
        public void Reset() => Interlocked.Exchange(ref _pass, 0);

        /// <summary>Writes one step's preview and says where it went, or null for a message that is not one.
        /// Runs on the socket's thread.</summary>
        public (string Path, int Step, int Total, int Pass)? Write(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
                if (!data.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String) return null;

                var mime = data.TryGetProperty("mime", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? "image/jpeg"
                    : "image/jpeg";
                var ext = mime switch
                {
                    "video/mp4" => ".mp4",
                    "image/webp" => ".webp",
                    "image/png" => ".png",
                    _ => ".jpg",
                };

                var bytes = Convert.FromBase64String(image.GetString()!);
                Directory.CreateDirectory(_dir);
                var slot = Interlocked.Increment(ref _counter) % Slots;
                var path = Path.Combine(_dir, $"preview_{slot}{ext}");
                File.WriteAllBytes(path, bytes);

                var step = data.TryGetProperty("step", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
                var total = data.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
                var pass = step == 0 ? Interlocked.Increment(ref _pass) : Math.Max(1, _pass);
                return (path, step, total, pass);
            }
            catch (Exception ex)
            {
                // A file still held by the player, or a truncated message: skip this step, the next replaces it.
                System.Diagnostics.Debug.WriteLine($"Live preview skipped: {ex.Message}");
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* in use — overwritten on the next rotation */ }
        }
    }
}

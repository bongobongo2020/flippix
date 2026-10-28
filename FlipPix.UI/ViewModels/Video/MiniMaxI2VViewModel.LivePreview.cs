using System;
using System.IO;
using System.Text.Json;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The live sampler preview — what ComfyUI shows on a graph with KJNodes' <c>ModelPreviewOverrideKJ</c>:
    /// the clip coming out of the noise, a little clearer at every step.
    ///
    /// <para>The node sends a <c>kj_preview_override</c> message per sampler step with the whole clip as it
    /// stands, decoded by the tiny VAE: a JPEG of the starting noise first, then an MP4 (608×352 at 12 fps on
    /// the 2-Pass first pass). Each one is written to a small rotating set of temp files and handed to the
    /// view, which loops the newest and crossfades to the next — see Controls/LivePreviewPlayer. Only shown
    /// while this tab is rendering; the 🌹 2-Pass graph carries the node, the others do not.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel
    {
        private const int LivePreviewSlots = 6;

        private static readonly string LivePreviewDir =
            Path.Combine(Path.GetTempPath(), "FlipPix", "live_preview");

        private string _livePreviewPath = string.Empty;
        private string _livePreviewCaption = string.Empty;
        private int _livePreviewCounter;

        /// <summary>Which sampler of the run is previewing: each starts with a step-0 noise frame. Counted
        /// rather than read from the payload, because the node reports its own id for every pass.</summary>
        private int _livePreviewPass;

        /// <summary>The newest preview on disk: an .mp4 to loop, or a .jpg still. Empty when there is none.</summary>
        public string LivePreviewPath
        {
            get => _livePreviewPath;
            private set
            {
                if (_livePreviewPath == value) return;
                _livePreviewPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLivePreview));
            }
        }

        public bool HasLivePreview => _livePreviewPath.Length > 0;

        /// <summary>"Live preview · pass 1 · step 3/8".</summary>
        public string LivePreviewCaption
        {
            get => _livePreviewCaption;
            private set { if (_livePreviewCaption != value) { _livePreviewCaption = value; OnPropertyChanged(); } }
        }

        private void InitLivePreview()
        {
            try
            {
                if (Directory.Exists(LivePreviewDir))
                    foreach (var stale in Directory.EnumerateFiles(LivePreviewDir)) TryDelete(stale);
            }
            catch { /* another instance may hold one open; it is overwritten on rotation anyway */ }

            _comfyUIService.LivePreviewReceived += OnLivePreviewReceived;
        }

        /// <summary>Takes the preview down at the start and end of each item, so a take never opens on the
        /// last one's clip.</summary>
        private void ClearLivePreview() => OnUiThread(() =>
        {
            _livePreviewPass = 0;
            LivePreviewPath = string.Empty;
            LivePreviewCaption = string.Empty;
        });

        /// <summary>Runs on the socket's thread: decode and write here, hand the path to the UI thread.</summary>
        private void OnLivePreviewReceived(object? sender, string raw)
        {
            if (!IsProcessing) return;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return;
                if (!data.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String) return;

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
                Directory.CreateDirectory(LivePreviewDir);
                var slot = System.Threading.Interlocked.Increment(ref _livePreviewCounter) % LivePreviewSlots;
                var path = Path.Combine(LivePreviewDir, $"preview_{slot}{ext}");
                File.WriteAllBytes(path, bytes);

                var step = data.TryGetProperty("step", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
                var total = data.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
                var pass = step == 0
                    ? System.Threading.Interlocked.Increment(ref _livePreviewPass)
                    : Math.Max(1, _livePreviewPass);
                var label = pass == 1 ? "pass 1" : $"pass {pass} · refining";
                var caption = step == 0
                    ? $"Live preview · {label} · starting noise"
                    : $"Live preview · {label} · step {step}/{total}";

                OnUiThread(() =>
                {
                    if (!IsProcessing) return;
                    LivePreviewPath = path;
                    LivePreviewCaption = caption;
                });
            }
            catch (Exception ex)
            {
                // A file still held by the player, or a truncated message: skip this step, the next replaces it.
                System.Diagnostics.Debug.WriteLine($"Live preview skipped: {ex.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* in use — overwritten on the next rotation */ }
        }
    }
}

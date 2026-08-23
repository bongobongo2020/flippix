using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using FlipPix.UI.Services;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A serializable snapshot of one 🎬🎭 H3 Ensemble job: up to <b>five</b> character sheets, one
    /// <b>environment</b> photograph, the keyframe stills the clip opens on, and the finished six-section
    /// hybrid prompt that ties them together — rendered through
    /// <c>workflow/video/h3-minimax/h3-cast-hybrid.json</c>.
    ///
    /// <para><b>Why the cast is a list rather than numbered fields.</b> The 🪪👥⚡ H3 Cast Hybrid item carries
    /// <c>Character1…</c>/<c>Character2…</c> in eight parallel properties, which is bearable for two and
    /// absurd for five. Here each character is one <see cref="EnsembleCastMember"/> and the wiring order is
    /// simply the list order — which is also the order <c>&lt;Picture n&gt;</c> was numbered in.</para>
    ///
    /// <para><b>Picture order is load-bearing and is frozen here:</b> keyframe stills first
    /// (<c>&lt;Picture 1&gt;</c>…<c>&lt;Picture K&gt;</c>), then every cast panel in cast order, then — last
    /// — the environment. Getting that order wrong does not fail; it renders a studio photograph as the
    /// opening frame. The environment is last on purpose: dropping a character a clip never names then moves
    /// nothing except the numbers after them, and the location is named in three of the code-written
    /// sections.</para>
    /// </summary>
    public class H3EnsembleQueueItem : BaseQueueItem
    {
        /// <summary>The timeline stills, in timestamp order. Empty = a pure reference-generation clip with no
        /// frame lock anywhere.</summary>
        public List<string> KeyframePaths { get; set; } = new();

        /// <summary>Each keyframe's lock timestamp in seconds, index-aligned with
        /// <see cref="KeyframePaths"/>.</summary>
        public List<double> KeyframeSeconds { get; set; } = new();

        /// <summary>The cast, in wiring order — the order their pictures are numbered in.</summary>
        public List<EnsembleCastMember> Cast { get; set; } = new();

        /// <summary>
        /// The photograph of the location, uploaded and wired as the <b>last</b> reference picture. Empty
        /// when the run has no environment reference, in which case the setting comes from the prose alone.
        /// </summary>
        public string EnvironmentPath { get; set; } = string.Empty;

        [JsonIgnore]
        public bool HasEnvironment =>
            !string.IsNullOrEmpty(EnvironmentPath) && File.Exists(EnvironmentPath);

        /// <summary>The assembled six-section hybrid prompt, wardrobe lock and all.</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// One face-refine prompt per character that has a pass, keyed by that character's subject index —
        /// the clip re-assembled as pure reference generation for that character alone, with their panels
        /// numbered from <c>&lt;Picture 1&gt;</c> and no keyframes and no environment.
        ///
        /// <para><c>H3FaceTrackCrop</c> holds a single subject through a clip, so a five-hander needs five
        /// passes, each tracked by its own face close-up and shown nobody else's photographs. Written at
        /// queue time, where the keyframes, the cast and the wardrobe are all still in hand. Empty for an
        /// item queued with the refine pass off.</para>
        /// </summary>
        public Dictionary<int, string> RefinePrompts { get; set; } = new();

        public string AspectRatio { get; set; } = "16:9 (Widescreen)";
        public double Megapixels { get; set; } = 1.0;
        public double LengthSeconds { get; set; } = 8;

        /// <summary>"live-action and cinematic", "anime, cinematic", … — opens the prompt's global rules.</summary>
        public string Medium { get; set; } = "live-action and cinematic";

        /// <summary>-1 = pick a fresh random seed when the item runs.</summary>
        public long Seed { get; set; } = -1;

        /// <summary>The second H3 pass over the tracked face crops — one per character in the clip. Default
        /// false, so an item restored from an older queue file never refines against a prompt it does not
        /// carry.</summary>
        public bool FaceRefine { get; set; }

        /// <summary>Denoise of the face-refine passes — how far a cropped face may move away from what the
        /// base pass rendered.</summary>
        public double RefineDenoise { get; set; } = 0.35;

        /// <summary>FILM ×2 frame interpolation, muxed at double the render rate.</summary>
        public bool Interpolate { get; set; } = true;

        /// <summary>RTX ×2 super-resolution. Off by default — the graph's largest single allocation.</summary>
        public bool RtxUpscale { get; set; }

        /// <summary>Groups the clips of one story so they render in order and can be joined when the last one
        /// lands. Empty for a standalone clip.</summary>
        public string StoryId { get; set; } = string.Empty;

        /// <summary>1-based position of this clip within its story. 1 for a standalone clip.</summary>
        public int ClipIndex { get; set; } = 1;

        /// <summary>How many clips the story was split into. 1 for a standalone clip.</summary>
        public int ClipCount { get; set; } = 1;

        [JsonIgnore]
        public bool IsStoryClip => ClipCount > 1;

        public string? OutputVideoPath { get; set; }

        [JsonIgnore]
        public int KeyframeCount => KeyframePaths.Count;

        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                var cast = Cast.Count == 0
                    ? "(no cast)"
                    : $"{Cast.Count} character{(Cast.Count == 1 ? "" : "s")}";
                var set = HasEnvironment ? " + set" : string.Empty;
                var keys = KeyframeCount == 0
                    ? " · no keyframe"
                    : $" · {KeyframeCount} keyframe{(KeyframeCount == 1 ? "" : "s")} @ " +
                      string.Join("/", KeyframeSeconds.Select(s => $"{s:0.#}s"));
                var refine = FaceRefine
                    ? $" · face refine {RefineDenoise:0.00}" +
                      (RefinePrompts.Count > 1 ? $" ×{RefinePrompts.Count}" : string.Empty)
                    : string.Empty;
                var finish = refine +
                             (Interpolate ? " · FILM ×2" : string.Empty) +
                             (RtxUpscale ? " · RTX ×2" : string.Empty);
                var clip = IsStoryClip ? $" · clip {ClipIndex}/{ClipCount}" : string.Empty;
                return $"{cast}{set}{keys} → {AspectRatio} · {LengthSeconds:0.#}s{clip}{finish}";
            }
        }

        /// <summary>One line saying what this clip does — the model's own summary sentence, not the thirty
        /// lines of wardrobe lock and subject definitions that open every row of a chain identically.</summary>
        [JsonIgnore]
        public string PromptPreview => HybridCastPrompt.ActionSummary(Prompt);

        private BitmapImage? _thumbnail;
        private bool _thumbnailTried;

        /// <summary>
        /// Small preview for the queue row — the opening keyframe if there is one (it is literally the first
        /// frame of the result), otherwise the environment, otherwise the first character's sheet. Decoded on
        /// first bind rather than on deserialize.
        /// </summary>
        [JsonIgnore]
        public BitmapImage? QueueThumbnail
        {
            get
            {
                if (_thumbnailTried) return _thumbnail;
                _thumbnailTried = true;

                var source = new[]
                    {
                        KeyframePaths.FirstOrDefault() ?? string.Empty,
                        EnvironmentPath,
                        Cast.FirstOrDefault()?.SheetPath ?? string.Empty,
                    }
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
                if (string.IsNullOrEmpty(source)) return null;

                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(source, UriKind.Absolute);
                    bitmap.DecodePixelHeight = 40;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _thumbnail = bitmap;
                }
                catch { _thumbnail = null; }

                return _thumbnail;
            }
        }
    }

    /// <summary>
    /// One member of a queued ensemble: which subject they are, the sheet they came from, the panels that
    /// sheet was cut into, and which of those panels this job actually sends.
    ///
    /// <para>Everything here is frozen at queue time rather than resolved at submit time, because the
    /// prompt's <c>&lt;Picture n&gt;</c> numbering was written from exactly this list. A sheet that has since
    /// been rebuilt, or a reference budget the user has since changed, must not silently renumber a job that
    /// is already queued.</para>
    /// </summary>
    public class EnsembleCastMember
    {
        /// <summary>The subject index the prompt calls them by — <c>&lt;Subject n&gt;</c>, and the character
        /// number their wardrobe line carries. It is the slot they occupy in the tab, so it is <b>not</b>
        /// necessarily their position in <see cref="H3EnsembleQueueItem.Cast"/>.</summary>
        public int Index { get; set; } = 1;

        /// <summary>"man" / "woman" / "creature" / "group" — the word the prompts use for them.</summary>
        public string Noun { get; set; } = "man";

        /// <summary>Who they are in the story ("the detective", "Nimbus, a fluffy little cloud"), or empty.
        /// Never sent to H3 as identity — it is what let the language model cast them in the first place,
        /// and for a non-person it is the only description of what they are.</summary>
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// Whether this cast member is a human being. <b>Defaults true</b>, which is what a queue item
        /// written before non-person characters existed deserializes as — and what it was.
        ///
        /// <para>False means every sentence about their face, hair, skin and build is wrong for them, and —
        /// the expensive part — that they get <b>no face-refine pass</b>. <c>H3FaceTrackCrop</c> tracks human
        /// faces; aimed at a mountain it finds nothing, or finds somebody else and redraws them.</para>
        /// </summary>
        public bool IsPerson { get; set; } = true;

        /// <summary>Several of them acting as one character — a herd, a village, a flock. Orthogonal to
        /// <see cref="IsPerson"/>: a village of travellers is both, and gets no refine pass either, because
        /// the tracker holds one subject and would refine whichever of them happened to be largest.</summary>
        public bool IsGroup { get; set; }

        /// <summary>What to call them when a tag is not enough — "a man", or their own description.</summary>
        [JsonIgnore]
        public string Describe =>
            IsPerson || string.IsNullOrWhiteSpace(Role) ? $"a {Noun}" : Role.Trim();

        /// <summary>The photo the sheet was built from. Never uploaded — kept for the row and the log.</summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>The multi-view sheet. What is uploaded is <see cref="PanelPaths"/>.</summary>
        public string SheetPath { get; set; } = string.Empty;

        /// <summary>The sheet cut into single-view panels, left to right.</summary>
        public List<string> PanelPaths { get; set; } = new();

        /// <summary>Which of <see cref="PanelPaths"/> this job uploads — the reference budget frozen as
        /// indices, so the re-split fallback keeps working against the full sheet. Empty = every panel.</summary>
        public List<int> PanelIndices { get; set; } = new();

        /// <summary>What each uploaded panel shows ("full-body front", "face close-up", …), index-aligned
        /// with <see cref="PanelIndices"/>. The prompt was written from these words, and the face one is the
        /// identity reference this character's refine pass tracks by.</summary>
        public List<string> PanelViews { get; set; } = new();
    }
}

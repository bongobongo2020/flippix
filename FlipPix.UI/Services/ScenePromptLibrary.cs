using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using FlipPix.UI.Models;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Persistent store for a tab's analyzed prompts. One instance is one library: the Character tab keeps
    /// its scenes, the I2V tab keeps its takes, and they do not mix — a Character scene has had its
    /// reference line stripped for rewriting, while an I2V take carries a segment list and a picture
    /// order, so an entry from one tab would be wrong in the other's boxes. The separation is just the
    /// root folder (see <see cref="FolderFor"/>).
    ///
    /// <para>Everything lives under <c>%APPDATA%\FlipPix\prompts\&lt;library&gt;\</c>: a single
    /// <c>index.json</c> plus one small JPEG per entry in <c>thumbs\</c>. Thumbnails are deliberately
    /// <b>not</b> inlined into the index — a few hundred base64 images would make the index expensive to
    /// read, and this tab is on the Video Generator's startup path.</para>
    ///
    /// <para>All file access is synchronous and guarded by one lock; callers are expected to reach it from a
    /// background thread (see <see cref="LoadAsync"/> / <see cref="SaveAsync"/>).</para>
    /// </summary>
    public sealed class ScenePromptLibrary
    {
        /// <summary>Entries kept before the least recently used ones are dropped (with their thumbnails).</summary>
        private const int MaxEntries = 300;

        /// <summary>Thumbnail width in pixels. Height follows the source aspect.</summary>
        private const int ThumbnailWidth = 240;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly object _fileLock = new();
        private readonly Action<string>? _log;

        /// <summary>Where a named library lives. Pass the result as the constructor's
        /// <c>rootFolder</c> to give a tab a store of its own.</summary>
        public static string FolderFor(string libraryName) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "prompts", libraryName);

        /// <param name="log">Optional sink for the non-fatal problems this class swallows.</param>
        /// <param name="rootFolder">Which library to open; defaults to the Character tab's scenes, which
        /// is where this class started and where those entries still live.</param>
        public ScenePromptLibrary(Action<string>? log = null, string? rootFolder = null)
        {
            _log = log;
            RootFolder = rootFolder ?? FolderFor("scenes");
        }

        public string RootFolder { get; }

        private string IndexPath => Path.Combine(RootFolder, "index.json");
        private string ThumbFolder => Path.Combine(RootFolder, "thumbs");

        /// <summary>Absolute path of an entry's thumbnail, or null when it has none / it was deleted.</summary>
        public string? ResolveThumbnail(ScenePrompt entry)
        {
            if (string.IsNullOrEmpty(entry.ThumbnailFile)) return null;
            var path = Path.Combine(ThumbFolder, entry.ThumbnailFile);
            return File.Exists(path) ? path : null;
        }

        /// <summary>All saved scenes, most recently used first. Never throws — a broken index reads as empty.</summary>
        public List<ScenePrompt> Load()
        {
            lock (_fileLock)
            {
                try
                {
                    if (!File.Exists(IndexPath)) return new List<ScenePrompt>();
                    var json = File.ReadAllText(IndexPath);
                    var entries = JsonSerializer.Deserialize<List<ScenePrompt>>(json);
                    if (entries == null) return new List<ScenePrompt>();
                    var loaded = entries
                        .Where(e => !string.IsNullOrWhiteSpace(e.Prompt))
                        .OrderByDescending(e => e.LastUsed)
                        .ToList();

                    // An index written before the list fields existed - or hand-edited to null - must not
                    // hand callers a null collection to enumerate.
                    foreach (var entry in loaded)
                    {
                        entry.ReferenceImagePaths ??= new List<string>();
                        entry.ContinuationPrompts ??= new List<string>();
                        entry.ContinuationSeconds ??= new List<int>();
                    }

                    return loaded;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Scene library: could not read the index ({ex.Message}) — starting empty.");
                    return new List<ScenePrompt>();
                }
            }
        }

        public Task<List<ScenePrompt>> LoadAsync(CancellationToken token = default) =>
            Task.Run(Load, token);

        /// <summary>
        /// Writes the index, trimming to <see cref="MaxEntries"/> and deleting the thumbnails of whatever
        /// gets trimmed or was removed by the caller, so the thumbs folder cannot grow without bound.
        /// </summary>
        public void Save(IEnumerable<ScenePrompt> entries)
        {
            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(RootFolder);

                    var kept = entries
                        .Where(e => !string.IsNullOrWhiteSpace(e.Prompt))
                        .OrderByDescending(e => e.LastUsed)
                        .Take(MaxEntries)
                        .ToList();

                    File.WriteAllText(IndexPath, JsonSerializer.Serialize(kept, JsonOptions));
                    PruneOrphanThumbnails(kept);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Scene library: save failed — {ex.Message}");
                }
            }
        }

        public Task SaveAsync(IEnumerable<ScenePrompt> entries)
        {
            var snapshot = entries.ToList();
            return Task.Run(() => Save(snapshot));
        }

        /// <summary>
        /// Adds <paramref name="entry"/> to <paramref name="entries"/> and renders its thumbnail, or — when
        /// an entry with the same prompt body already exists — refreshes that one instead. Returns whichever
        /// entry the list now holds, and whether it was new.
        ///
        /// <para>Analyze saves on every run, so without the de-duplication a user re-analyzing the same
        /// scene would collect near-identical rows.</para>
        /// </summary>
        public (ScenePrompt Entry, bool IsNew) AddOrRefresh(List<ScenePrompt> entries, ScenePrompt entry)
        {
            var key = PromptKey(entry);
            var existing = entries.FirstOrDefault(e => PromptKey(e) == key);
            var target = existing ?? entry;

            if (existing != null)
            {
                existing.LastUsed = DateTime.Now;
                existing.UseCount++;
                // Keep the newest context: the same text may have been re-analyzed at a different length or
                // against a different scene image.
                if (!string.IsNullOrEmpty(entry.SceneImagePath)) existing.SceneImagePath = entry.SceneImagePath;
                if (!string.IsNullOrEmpty(entry.AspectRatio)) existing.AspectRatio = entry.AspectRatio;
                if (entry.LengthSeconds > 0) existing.LengthSeconds = entry.LengthSeconds;
                if (entry.StoryDurationSeconds > 0) existing.StoryDurationSeconds = entry.StoryDurationSeconds;
                if (entry.ReferenceImagePaths.Count > 0) existing.ReferenceImagePaths = entry.ReferenceImagePaths;
                if (entry.ContinuationSeconds.Count > 0) existing.ContinuationSeconds = entry.ContinuationSeconds;
                // Not conditional: the continuations are part of the key, so they already match - this only
                // takes the identically-keyed newest wording.
                existing.ContinuationPrompts = entry.ContinuationPrompts;
            }
            else
            {
                entries.Insert(0, entry);
            }

            // Written last and keyed on the surviving entry's id, so a refresh overwrites its own thumbnail
            // in place instead of leaving the previous file orphaned.
            var thumb = CreateThumbnail(target.SceneImagePath, target.Id);
            if (!string.IsNullOrEmpty(thumb)) target.ThumbnailFile = thumb;

            return (target, existing == null);
        }

        /// <summary>
        /// Renders <paramref name="sourceImagePath"/> down to a small JPEG in the thumbs folder and returns
        /// its file name, or an empty string when there is nothing to render. Decoding is capped at
        /// <see cref="ThumbnailWidth"/> so a 4K scene image is never fully decoded.
        /// </summary>
        public string CreateThumbnail(string sourceImagePath, string entryId)
        {
            if (string.IsNullOrEmpty(sourceImagePath) || !File.Exists(sourceImagePath)) return string.Empty;

            try
            {
                Directory.CreateDirectory(ThumbFolder);

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = ThumbnailWidth;
                bitmap.UriSource = new Uri(sourceImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                var fileName = $"{entryId}.jpg";
                using var stream = File.Create(Path.Combine(ThumbFolder, fileName));
                encoder.Save(stream);
                return fileName;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Scene library: thumbnail failed — {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>Loads a saved thumbnail as a frozen bitmap, or null when it is missing or unreadable.</summary>
        public BitmapImage? LoadThumbnail(ScenePrompt entry)
        {
            var path = ResolveThumbnail(entry);
            if (path == null) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = ThumbnailWidth;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public void DeleteThumbnail(string thumbnailFile)
        {
            if (string.IsNullOrEmpty(thumbnailFile)) return;
            try
            {
                var path = Path.Combine(ThumbFolder, thumbnailFile);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* a thumbnail we cannot delete is cosmetic — the next PruneOrphanThumbnails retries */ }
        }

        /// <summary>Deletes every thumbnail no surviving entry points at.</summary>
        private void PruneOrphanThumbnails(List<ScenePrompt> kept)
        {
            try
            {
                if (!Directory.Exists(ThumbFolder)) return;
                var live = new HashSet<string>(
                    kept.Select(e => e.ThumbnailFile).Where(f => !string.IsNullOrEmpty(f)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var file in Directory.GetFiles(ThumbFolder, "*.jpg"))
                {
                    if (!live.Contains(Path.GetFileName(file)))
                        File.Delete(file);
                }
            }
            catch { /* cosmetic */ }
        }

        /// <summary>
        /// Whitespace-insensitive comparison key, so a reflowed copy of a prompt is not a new row.
        ///
        /// <para>It spans the continuations as well as the base pass. Two takes that open identically and
        /// then go different ways are different takes, and folding them together would lose whichever was
        /// saved second.</para>
        /// </summary>
        private static string PromptKey(ScenePrompt entry) =>
            Normalize(string.Join("\n",
                new[] { entry.Prompt }.Concat(entry.ContinuationPrompts ?? new List<string>())));

        private static string Normalize(string prompt) =>
            string.Join(' ', (prompt ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>
        /// A short, human-recognisable label: the scene image's file name when there is one, otherwise the
        /// prompt's first sentence. Suffixed when the name is already taken so the picker has no two
        /// identical rows.
        /// </summary>
        public static string SuggestName(string sceneImagePath, string prompt, IEnumerable<ScenePrompt> existing)
        {
            var name = string.Empty;

            if (!string.IsNullOrEmpty(sceneImagePath))
                name = Path.GetFileNameWithoutExtension(sceneImagePath).Replace('_', ' ').Replace('-', ' ').Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                // Fall back to the prompt's own opening, past the H3 field label if one leads it.
                var body = (prompt ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
                var label = body.IndexOf("description:", StringComparison.OrdinalIgnoreCase);
                if (label >= 0 && label < 60) body = body[(label + "description:".Length)..].TrimStart();
                name = body;
            }

            if (string.IsNullOrWhiteSpace(name)) name = "Untitled scene";
            if (name.Length > 48) name = name[..48].TrimEnd() + "…";
            name = char.ToUpperInvariant(name[0]) + name[1..];

            var taken = new HashSet<string>(existing.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name)) return name;

            for (var i = 2; ; i++)
            {
                var candidate = $"{name} ({i})";
                if (!taken.Contains(candidate)) return candidate;
            }
        }
    }
}

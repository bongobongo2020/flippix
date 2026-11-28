using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace FlipPix.UI.Services
{
    /// <summary>One piece of a chunked SeedVR2 upscale: the source frames it reads, <c>[Start, Start + Length)</c>.</summary>
    public sealed record SeedVr2Chunk(int Index, int Start, int Length)
    {
        public int End => Start + Length;
    }

    /// <summary>How a clip is cut for SeedVR2: the pieces, and how many frames neighbours share (and crossfade over).</summary>
    public sealed record SeedVr2ChunkPlan(IReadOnlyList<SeedVr2Chunk> Chunks, int OverlapFrames, int TotalFrames)
    {
        public bool IsChunked => Chunks.Count > 1;
    }

    /// <summary>
    /// Long-video SeedVR2 on the 🎞 Video Enhance tab, without running ComfyUI out of system RAM.
    ///
    /// <para><b>Why.</b> The SeedVR2 graph already splits the diffusion and the VAE in time (61-frame chunks, tiled
    /// encode/decode), so VRAM is not the limit. RAM is: <c>VHS_LoadVideoFFmpeg</c> loads the whole clip, and the graph
    /// holds the pre-resized frames, the decoded frames and the post-processing reference at full output size, as
    /// float32, all at once. A 90 s Long Form story at 2× is ~2,160 frames × 25 MB per copy — far past the server.</para>
    ///
    /// <para><b>How.</b> The clip is cut on this PC, frame-exact and losslessly, into pieces sized from the server's free
    /// RAM. Neighbouring pieces share <see cref="OverlapFrames"/> frames. Each piece is upscaled as its own prompt with
    /// no audio, the same seed and LAB colour matching (so each piece is tied to its own input rather than drifting its
    /// own way), then the pieces are crossfaded over the shared frames and the original soundtrack is muxed back once,
    /// in a single final encode.</para>
    /// </summary>
    public static class SeedVr2Chunks
    {
        /// <summary>Frames neighbouring pieces share; the join crossfades across all of them. Twice the graph's own
        /// 8-frame temporal overlap, so each piece's weakest edge frames carry the least weight.</summary>
        public const int OverlapFrames = 16;

        /// <summary>Never cut finer than one of the graph's own 61-frame temporal chunks.</summary>
        public const int MinChunkFrames = 61;

        /// <summary>~20 s at 24 fps: past this, a bigger piece only saves a seam and costs a long redo on a crash.</summary>
        public const int MaxChunkFrames = 480;

        /// <summary>Full-size float32 RGB copies of each frame the graph holds at once (resized input, preprocess,
        /// decode, post-process, its reference) — the RAM cost per output frame, with headroom.</summary>
        public const int CopiesPerFrame = 6;

        /// <summary>Share of the server's currently free RAM a piece may use; the rest is for models and everyone else.</summary>
        public const double RamShare = 0.6;

        /// <summary>Used when the server does not report its free RAM.</summary>
        public const long FallbackFreeRamBytes = 24L * 1024 * 1024 * 1024;

        public const string ColorCorrection = "lab";

        /// <summary>Near-lossless for the pieces: they are re-encoded once more at the join.</summary>
        public const int ChunkCrf = 12;

        public const int FinalCrf = 17;

        /// <summary>The longest piece the server's free RAM allows at this output size.</summary>
        public static int MaxFramesForRam(int sourceWidth, int sourceHeight, double scale, long freeRamBytes)
        {
            if (freeRamBytes <= 0) freeRamBytes = FallbackFreeRamBytes;
            var outPixels = Math.Max(1.0, sourceWidth * scale) * Math.Max(1.0, sourceHeight * scale);
            var bytesPerFrame = outPixels * 3 * sizeof(float) * CopiesPerFrame;
            var frames = (int)Math.Floor(freeRamBytes * RamShare / bytesPerFrame);
            return Math.Clamp(frames, MinChunkFrames, MaxChunkFrames);
        }

        /// <summary>
        /// Cuts <paramref name="totalFrames"/> into the fewest pieces of at most <paramref name="maxChunkFrames"/>
        /// that overlap by <see cref="OverlapFrames"/>, all the same length (±1) so no tiny last piece is left over.
        /// A clip that fits in one piece is one piece, and runs unchunked.
        /// </summary>
        public static SeedVr2ChunkPlan Plan(int totalFrames, int maxChunkFrames, int overlap = OverlapFrames)
        {
            if (totalFrames <= 0) throw new ArgumentOutOfRangeException(nameof(totalFrames));
            maxChunkFrames = Math.Max(maxChunkFrames, overlap * 2 + 1);
            if (totalFrames <= maxChunkFrames)
                return new SeedVr2ChunkPlan(new[] { new SeedVr2Chunk(0, 0, totalFrames) }, 0, totalFrames);

            // n pieces of length L overlapping by O cover n·L − (n−1)·O frames.
            var count = (int)Math.Ceiling((double)(totalFrames - overlap) / (maxChunkFrames - overlap));
            var newFrames = totalFrames - overlap;            // frames each piece adds beyond the shared lead-in
            var chunks = new List<SeedVr2Chunk>(count);
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                var stride = newFrames / count + (i < newFrames % count ? 1 : 0);
                chunks.Add(new SeedVr2Chunk(i, start, stride + overlap));
                start += stride;
            }
            return new SeedVr2ChunkPlan(chunks, overlap, totalFrames);
        }

        /// <summary>The frame rate as ffmpeg reads it back in an expression: "24", "30000/1001".</summary>
        public static string RateExpression(int fpsNum, int fpsDen) =>
            fpsDen == 1 ? fpsNum.ToString(CultureInfo.InvariantCulture)
                        : $"{fpsNum.ToString(CultureInfo.InvariantCulture)}/{fpsDen.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>ffmpeg arguments that write one piece: exactly its frames, video only, lossless, timestamps from 0.</summary>
        public static string CutArguments(string source, SeedVr2Chunk chunk, int fpsNum, int fpsDen, string output)
        {
            var rate = RateExpression(fpsNum, fpsDen);
            return $"-y -v error -i \"{source}\" -map 0:v:0 -an -sn -dn " +
                   $"-vf \"trim=start_frame={chunk.Start}:end_frame={chunk.End},setpts=N/({rate})/TB\" " +
                   $"-r {rate} -c:v libx264 -preset veryfast -qp 0 -pix_fmt yuv420p \"{output}\"";
        }

        /// <summary>
        /// ffmpeg arguments that crossfade the upscaled pieces back into one clip and mux the source's audio (if any)
        /// in its original form. Each piece is first trimmed to its planned length and re-timed, so a piece the
        /// graph returned a frame long, or with odd timestamps, still lines up.
        /// </summary>
        public static string JoinArguments(IReadOnlyList<string> pieces, SeedVr2ChunkPlan plan, int fpsNum, int fpsDen,
            string audioSource, bool copyAudio, string output)
        {
            if (pieces.Count != plan.Chunks.Count) throw new ArgumentException("One upscaled file per piece.", nameof(pieces));
            var rate = RateExpression(fpsNum, fpsDen);
            var fps = (double)fpsNum / fpsDen;
            var args = new StringBuilder("-y -v error ");
            foreach (var p in pieces) args.Append($"-i \"{p}\" ");
            args.Append($"-i \"{audioSource}\" ");

            var graph = new StringBuilder();
            for (var i = 0; i < pieces.Count; i++)
                graph.Append($"[{i}:v:0]trim=end_frame={plan.Chunks[i].Length},setpts=N/({rate})/TB,format=yuv420p[p{i}];");

            var label = "p0";
            if (pieces.Count == 1)
            {
                graph.Append("[p0]null[v]");
            }
            else
            {
                var joined = plan.Chunks[0].Length;               // frames in the result so far
                var seconds = (plan.OverlapFrames / fps).ToString("0.######", CultureInfo.InvariantCulture);
                for (var i = 1; i < pieces.Count; i++)
                {
                    var offset = ((joined - plan.OverlapFrames) / fps).ToString("0.######", CultureInfo.InvariantCulture);
                    var next = i == pieces.Count - 1 ? "v" : $"x{i}";
                    graph.Append($"[{label}][p{i}]xfade=transition=fade:duration={seconds}:offset={offset}[{next}]");
                    if (i < pieces.Count - 1) graph.Append(';');
                    label = next;
                    joined += plan.Chunks[i].Length - plan.OverlapFrames;
                }
            }

            args.Append($"-filter_complex \"{graph}\" -map \"[v]\" -map {pieces.Count}:a:0? ");
            args.Append($"-frames:v {plan.TotalFrames} -r {rate} -c:v libx264 -preset medium -crf {FinalCrf} -pix_fmt yuv420p ");
            args.Append(copyAudio ? "-c:a copy " : "-c:a aac -b:a 256k ");
            args.Append($"-movflags +faststart \"{output}\"");
            return args.ToString();
        }

        /// <summary>
        /// Patches the SeedVR2 graph for one piece: its upload, the multiplier, the shared seed, a prefix only this
        /// piece writes, LAB colour matching, a near-lossless encode, and no audio (the join puts the original back).
        /// </summary>
        public static void PatchForChunk(JsonObject graph, string videoNode, string resizeNode, string resizeInput,
            string samplerNode, string postNode, string combineNode, string uploadedName, double scale, long seed, string prefix)
        {
            Inputs(graph, videoNode)["video"] = uploadedName;
            Inputs(graph, resizeNode)[resizeInput] = scale;
            Inputs(graph, samplerNode)["seed"] = seed;
            Inputs(graph, postNode)["color_correction_method"] = ColorCorrection;
            var combine = Inputs(graph, combineNode);
            combine["filename_prefix"] = prefix;
            combine["crf"] = ChunkCrf;
            combine.Remove("audio");
        }

        private static JsonObject Inputs(JsonObject graph, string id) =>
            graph[id]?["inputs"]?.AsObject() ?? throw new InvalidOperationException($"SeedVR2 graph has no node {id}.");
    }
}

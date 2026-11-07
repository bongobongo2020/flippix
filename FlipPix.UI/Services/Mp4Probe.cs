using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Reads the track list and duration of an MP4/MOV (ISO base media) file without ffmpeg, by walking
    /// the box tree and seeking past media data. Used when ffprobe is not installed: without it the
    /// audio check had to guess "has audio", and a video-only clip then failed on the server
    /// ("VHS failed to extract audio … Output file does not contain any stream").
    /// <para>Only reads box headers, <c>mvhd</c> and each track's <c>hdlr</c>, so a 200 MB file whose
    /// <c>moov</c> sits after <c>mdat</c> costs a handful of small reads.</para>
    /// </summary>
    public static class Mp4Probe
    {
        public sealed record Result(bool HasVideo, bool HasAudio, double DurationSeconds);

        /// <summary>The file's tracks and duration, or null when it is not an MP4/MOV this can read.</summary>
        public static Result? TryRead(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
                return TryRead(fs);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static Result? TryRead(Stream stream)
        {
            if (!stream.CanSeek) return null;
            var state = new State();
            var sawMoov = false;
            foreach (var (type, start, end) in Boxes(stream, 0, stream.Length))
            {
                if (type == "moov")
                {
                    sawMoov = true;
                    ReadMoov(stream, start, end, state);
                }
            }
            return sawMoov ? new Result(state.HasVideo, state.HasAudio, state.Duration) : null;
        }

        private sealed class State
        {
            public bool HasVideo, HasAudio;
            public double Duration;
        }

        private static void ReadMoov(Stream s, long start, long end, State state)
        {
            foreach (var (type, bStart, bEnd) in Boxes(s, start, end))
            {
                if (type == "mvhd") state.Duration = ReadMvhdDuration(s, bStart, bEnd);
                else if (type == "trak") ReadContainer(s, bStart, bEnd, state);
            }
        }

        // trak → mdia → hdlr; the handler type says what the track carries.
        private static void ReadContainer(Stream s, long start, long end, State state)
        {
            foreach (var (type, bStart, bEnd) in Boxes(s, start, end))
            {
                if (type == "mdia") ReadContainer(s, bStart, bEnd, state);
                else if (type == "hdlr" && bEnd - bStart >= 12)
                {
                    // version/flags (4), pre_defined / component type (4), handler_type (4)
                    var buf = ReadAt(s, bStart + 8, 4);
                    if (buf == null) continue;
                    var handler = Encoding.ASCII.GetString(buf);
                    if (handler == "soun") state.HasAudio = true;
                    else if (handler == "vide") state.HasVideo = true;
                }
            }
        }

        private static double ReadMvhdDuration(Stream s, long start, long end)
        {
            var head = ReadAt(s, start, 1);
            if (head == null) return 0;
            if (head[0] == 1)
            {
                var b = ReadAt(s, start + 20, 12);
                if (b == null) return 0;
                var scale = BinaryPrimitives.ReadUInt32BigEndian(b);
                var dur = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(4));
                return scale == 0 ? 0 : dur / (double)scale;
            }
            else
            {
                var b = ReadAt(s, start + 12, 8);
                if (b == null) return 0;
                var scale = BinaryPrimitives.ReadUInt32BigEndian(b);
                var dur = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
                return scale == 0 ? 0 : dur / (double)scale;
            }
        }

        /// <summary>The boxes directly inside [start, end): type and payload range.</summary>
        private static System.Collections.Generic.IEnumerable<(string Type, long Start, long End)> Boxes(Stream s, long start, long end)
        {
            var off = start;
            while (off + 8 <= end)
            {
                var hdr = ReadAt(s, off, 8);
                if (hdr == null) yield break;
                long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
                var type = Encoding.ASCII.GetString(hdr, 4, 4);
                long headerLen = 8;
                if (size == 1)
                {
                    var big = ReadAt(s, off + 8, 8);
                    if (big == null) yield break;
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(big);
                    headerLen = 16;
                }
                else if (size == 0) size = end - off;
                if (size < headerLen || off + size > end) yield break;
                yield return (type, off + headerLen, off + size);
                off += size;
            }
        }

        private static byte[]? ReadAt(Stream s, long offset, int count)
        {
            if (offset < 0 || offset + count > s.Length) return null;
            s.Position = offset;
            var buf = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = s.Read(buf, read, count - read);
                if (n <= 0) return null;
                read += n;
            }
            return buf;
        }
    }
}

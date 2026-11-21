using System.IO;
using System.Text;
using FlipPix.UI.ViewModels.Video;
using Xunit;

namespace FlipPix.Tests
{
    public class YakkinatorVoiceTests
    {
        /// <summary>A 16-bit mono WAV of <paramref name="samples"/> samples, with a LIST chunk ahead of the data
        /// the way many encoders write one.</summary>
        private static string WriteWav(int sampleRate, int samples, bool withList)
        {
            var path = Path.Combine(Path.GetTempPath(), $"yak_{Path.GetRandomFileName()}.wav");
            using var w = new BinaryWriter(File.Create(path));
            var list = withList ? Encoding.ASCII.GetBytes("INFOISFT\x05\0\0\0test\0\0") : new byte[0];
            var data = samples * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(4 + 24 + (withList ? 8 + list.Length : 0) + 8 + data);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
            if (withList) { w.Write(Encoding.ASCII.GetBytes("LIST")); w.Write(list.Length); w.Write(list); }
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(data);
            w.Write(new byte[data]);
            return path;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WavSeconds_reads_length_from_header(bool withList)
        {
            var path = WriteWav(24000, 24000 * 5 / 2, withList);
            try { Assert.Equal(2.5, H3FflfViewModel.WavSeconds(path), 3); }
            finally { File.Delete(path); }
        }

        [Fact]
        public void WavSeconds_is_zero_for_non_wav()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "not audio");
            try { Assert.Equal(0, H3FflfViewModel.WavSeconds(path)); }
            finally { File.Delete(path); }
        }
    }
}

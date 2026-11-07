using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// <see cref="Mp4Probe"/> tells a video-only clip from one with sound without ffmpeg, so Character Swap
/// feeds H3 silence instead of letting VHS_LoadVideo fail on the missing audio stream.
/// </summary>
public sealed class Mp4ProbeTests
{
    private static byte[] Box(string type, params byte[][] payload)
    {
        var body = payload.SelectMany(p => p).ToArray();
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        body.CopyTo(b, 8);
        return b;
    }

    private static byte[] Mvhd(uint timescale, uint duration)
    {
        var p = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), duration);
        return Box("mvhd", p);
    }

    private static byte[] Trak(string handler)
    {
        var hdlr = new byte[24];
        Encoding.ASCII.GetBytes(handler).CopyTo(hdlr, 8);
        return Box("trak", Box("mdia", Box("mdhd", new byte[24]), Box("hdlr", hdlr)));
    }

    private static Mp4Probe.Result? Probe(params byte[][] boxes) =>
        Mp4Probe.TryRead(new MemoryStream(boxes.SelectMany(b => b).ToArray()));

    [Fact]
    public void VideoOnly_MoovAfterMdat_HasNoAudio()
    {
        var r = Probe(Box("ftyp", new byte[16]), Box("mdat", new byte[5000]),
                      Box("moov", Mvhd(1000, 196_500), Trak("vide")));
        Assert.NotNull(r);
        Assert.True(r!.HasVideo);
        Assert.False(r.HasAudio);
        Assert.Equal(196.5, r.DurationSeconds, 3);
    }

    [Fact]
    public void VideoAndSound_HasAudio()
    {
        var r = Probe(Box("ftyp", new byte[16]), Box("moov", Mvhd(600, 3000), Trak("vide"), Trak("soun")),
                      Box("mdat", new byte[100]));
        Assert.NotNull(r);
        Assert.True(r!.HasAudio);
        Assert.Equal(5.0, r.DurationSeconds, 3);
    }

    [Fact]
    public void NotAnMp4_ReturnsNull()
    {
        Assert.Null(Mp4Probe.TryRead(new MemoryStream(Encoding.ASCII.GetBytes("\x1A\x45\xDF\xA3 matroska webm data"))));
    }
}

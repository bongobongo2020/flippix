using System.Text.Json;
using FlipPix.ComfyUI.Models;

namespace FlipPix.Tests;

/// <summary>
/// The /history poll is how FlipPix learns a run ended when a remote server drops the WebSocket
/// messages, so it must tell a failed run from a finished one.
/// </summary>
public sealed class HistoryOutcomeTests
{
    private static HistoryOutcome Read(string json) => HistoryOutcome.FromEntry(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Success_is_not_a_failure()
    {
        var o = Read("""{"status":{"status_str":"success","completed":true,"messages":[]},"outputs":{}}""");
        Assert.False(o.Failed);
    }

    [Fact]
    public void Entry_without_status_counts_as_finished()
    {
        Assert.False(Read("""{"outputs":{}}""").Failed);
    }

    [Fact]
    public void Error_reports_node_and_cause_not_the_ffmpeg_banner()
    {
        var message = "VHS failed to extract audio from /in/akkas-small.mp4:\n" +
                      "ffmpeg version 6.1.1\n  built with gcc 13\n  configuration: --enable-gpl\n" +
                      "  WARNING: library configuration mismatch\n" +
                      "[out#0/f32le @ 0x5a] Output file does not contain any stream\n" +
                      "Error opening output file -.\nError opening output files: Invalid argument\n";
        var json = JsonSerializer.Serialize(new
        {
            status = new
            {
                status_str = "error",
                completed = false,
                messages = new object[]
                {
                    new object[] { "execution_start", new { prompt_id = "p" } },
                    new object[] { "execution_error", new { prompt_id = "p", node_id = "14", node_type = "VHS_LoadVideo", exception_message = message } },
                },
            },
            outputs = new { },
        });

        var o = Read(json);

        Assert.True(o.Failed);
        Assert.StartsWith("node 14 (VHS_LoadVideo): VHS failed to extract audio from /in/akkas-small.mp4:", o.Error);
        Assert.Contains("Output file does not contain any stream", o.Error);
        Assert.DoesNotContain("configuration", o.Error);
    }

    [Fact]
    public void Error_without_details_still_fails()
    {
        Assert.True(Read("""{"status":{"status_str":"error","messages":[]}}""").Failed);
    }
}

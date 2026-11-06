using System.Text.Json;

namespace FlipPix.ComfyUI.Models;

/// <summary>
/// How a prompt ended, read from its /history entry. <see cref="Error"/> is null for a run that
/// succeeded (or whose entry carries no status, as older servers write it).
/// </summary>
public sealed record HistoryOutcome(string? Error)
{
    public bool Failed => Error != null;

    /// <summary>Reads one /history/{id} entry: <c>{"status": {"status_str": ..., "messages": [[type, data], ...]}, "outputs": ...}</c>.</summary>
    public static HistoryOutcome FromEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object ||
            !status.TryGetProperty("status_str", out var str) || str.GetString() != "error")
            return new HistoryOutcome((string?)null);

        if (status.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in messages.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() < 2 ||
                    m[0].GetString() != "execution_error" || m[1].ValueKind != JsonValueKind.Object)
                    continue;
                var data = m[1];
                var node = data.TryGetProperty("node_id", out var n) ? n.ToString() : "unknown";
                var type = data.TryGetProperty("node_type", out var t) ? t.GetString() : null;
                var msg = data.TryGetProperty("exception_message", out var e) ? e.GetString() : null;
                return new HistoryOutcome($"node {node}{(type != null ? $" ({type})" : "")}: {Summarize(msg)}");
            }
        }
        return new HistoryOutcome("ComfyUI marked the run as failed (no error details in its history).");
    }

    /// <summary>
    /// The exception's first line, plus the line that names the cause when the message is a tool dump
    /// (VHS appends ffmpeg's whole banner, hundreds of lines, after "failed to extract audio from …:").
    /// </summary>
    private static string Summarize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "unknown error";
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = lines[0];
        if (lines.Length <= 3) return string.Join(" ", lines);
        var cause = lines.Skip(1).FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                             l.Contains("does not contain", StringComparison.OrdinalIgnoreCase) ||
                                             l.Contains("invalid", StringComparison.OrdinalIgnoreCase));
        return cause != null ? $"{first} … {cause}" : first;
    }
}

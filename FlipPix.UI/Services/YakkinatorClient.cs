using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlipPix.UI.Services
{
    /// <summary>A Yakkinator TTS engine as offered in FlipPix's lists.</summary>
    public sealed record YakEngine(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>
    /// Talks to the Yakkinator (IndexTTS Studio) running on this PC through its Remote Access API
    /// (<c>/remote/v1</c>). The port and access token are read from Yakkinator's own settings file, so nothing
    /// has to be copied across — Remote Access only has to be switched on in Yakkinator's Settings.
    /// </summary>
    public sealed class YakkinatorClient
    {
        public const string IndexTts = "indextts";
        public const string Echo = "echo";
        public const string Indic = "indic";

        public static IReadOnlyList<YakEngine> Engines { get; } = new[]
        {
            new YakEngine(IndexTts, "IndexTTS-2"),
            new YakEngine(Echo, "Echo-TTS"),
            new YakEngine(Indic, "Indic-Speak"),
        };

        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>%LOCALAPPDATA%\IndexTTSStudio\settings.json — where Yakkinator keeps its settings.</summary>
        public static string SettingsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndexTTSStudio", "settings.json");

        private sealed record Connection(string BaseUrl, string Token);

        /// <summary>Reads the port and token Yakkinator's Remote Access listens with.</summary>
        private static Connection Connect()
        {
            if (!File.Exists(SettingsFile))
                throw new YakkinatorException("Yakkinator isn't installed on this PC (no settings found).");

            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
            var root = doc.RootElement;
            var enabled = root.TryGetProperty("RemoteEnabled", out var e) && e.ValueKind == JsonValueKind.True;
            var port = root.TryGetProperty("RemotePort", out var p) && p.TryGetInt32(out var n) ? n : 5301;
            var token = root.TryGetProperty("RemoteToken", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (!enabled || token.Length == 0)
                throw new YakkinatorException("Turn on Remote Access in Yakkinator (Settings → Remote Access), then try again.");
            return new Connection($"http://127.0.0.1:{port}/remote/v1", token);
        }

        private static HttpRequestMessage Request(HttpMethod method, Connection c, string path)
        {
            var msg = new HttpRequestMessage(method, c.BaseUrl + path);
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Token);
            return msg;
        }

        private static async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
        {
            var c = Connect();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(Request(HttpMethod.Get, c, path), cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                throw new YakkinatorException("Yakkinator isn't answering — is it running with Remote Access on?");
            }
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode) throw new YakkinatorException(ErrorOf(body, response.StatusCode));
                return JsonDocument.Parse(body);
            }
        }

        /// <summary>The voices <paramref name="engine"/> can speak with: the voice library for IndexTTS-2 and
        /// Echo-TTS (they clone a sample), the built-in speakers for Indic-Speak.</summary>
        public async Task<IReadOnlyList<string>> GetVoicesAsync(string engine, CancellationToken ct = default)
        {
            if (engine == Indic)
            {
                using var doc = await GetJsonAsync("/indic/voices", ct);
                var speakers = new List<string>();
                foreach (var lang in doc.RootElement.GetProperty("languages").EnumerateArray())
                    foreach (var s in lang.GetProperty("speakers").EnumerateArray())
                        if (s.GetString() is { Length: > 0 } name && !speakers.Contains(name)) speakers.Add(name);
                return speakers;
            }

            using var voices = await GetJsonAsync("/voices", ct);
            return voices.RootElement.GetProperty("voices").EnumerateArray()
                .Select(v => v.GetProperty("name").GetString() ?? string.Empty)
                .Where(n => n.Length > 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Speaks <paramref name="text"/> with <paramref name="voice"/> and writes the WAV to
        /// <paramref name="outputPath"/>. Waits its turn while Yakkinator is busy with another request.</summary>
        public async Task GenerateAsync(string engine, string voice, string text, string outputPath,
            Action<string>? onStatus = null, CancellationToken ct = default)
        {
            var c = Connect();
            for (var attempt = 0; ; attempt++)
            {
                using var form = new MultipartFormDataContent
                {
                    { new StringContent(text), "text" },
                    { new StringContent(engine), "engine" },
                    { new StringContent(voice), engine == Indic ? "speaker" : "voice_name" },
                };
                using var request = Request(HttpMethod.Post, c, "/generate");
                request.Content = form;

                HttpResponseMessage response;
                try
                {
                    response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                catch (HttpRequestException)
                {
                    throw new YakkinatorException("Yakkinator isn't answering — is it running with Remote Access on?");
                }

                using (response)
                {
                    // One remote job at a time on the Yakkinator side: wait for the one before to finish.
                    if (response.StatusCode == HttpStatusCode.Conflict && attempt < 200)
                    {
                        onStatus?.Invoke("Yakkinator is busy — waiting…");
                        await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new YakkinatorException(ErrorOf(await response.Content.ReadAsStringAsync(ct), response.StatusCode));

                    outputPath = Path.GetFullPath(outputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    var tmp = outputPath + ".part";
                    await using (var file = File.Create(tmp))
                        await response.Content.CopyToAsync(file, ct);
                    File.Move(tmp, outputPath, overwrite: true);
                    return;
                }
            }
        }

        /// <summary>Asks Yakkinator to unload its TTS engine and free the GPU memory it holds; it loads again on
        /// the next line. Never throws (except for the caller's cancellation): unavailable or busy is an answer.</summary>
        public async Task<YakUnload> UnloadAsync(CancellationToken ct = default)
        {
            Connection c;
            try { c = Connect(); }
            catch (Exception ex) when (ex is YakkinatorException or IOException or JsonException) { return YakUnload.NotAvailable; }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var request = Request(HttpMethod.Post, c, "/unload");
                using var response = await Http.SendAsync(request, cts.Token);
                if (response.StatusCode == HttpStatusCode.Conflict) return YakUnload.Busy;
                if (!response.IsSuccessStatusCode) return YakUnload.NotAvailable; // e.g. an older Yakkinator: 404
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                return doc.RootElement.TryGetProperty("freed", out var f) && f.ValueKind == JsonValueKind.True
                    ? YakUnload.Unloaded
                    : YakUnload.NothingLoaded;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return YakUnload.NotAvailable; }
        }

        private static string ErrorOf(string body, HttpStatusCode status)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } msg) return msg;
            }
            catch (JsonException) { }
            return status == HttpStatusCode.Unauthorized
                ? "Yakkinator refused the access token — restart Remote Access in Yakkinator and try again."
                : $"Yakkinator answered {(int)status} {status}.";
        }
    }

    public enum YakUnload { Unloaded, NothingLoaded, Busy, NotAvailable }

    public sealed class YakkinatorException : Exception
    {
        public YakkinatorException(string message) : base(message) { }
    }
}

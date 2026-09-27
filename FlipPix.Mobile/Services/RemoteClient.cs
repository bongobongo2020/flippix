using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.Services;

/// <summary>A problem worth showing as is: the computer's own sentence, or ours when it can't be reached.</summary>
public sealed class RemoteException : Exception
{
    public RemoteException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
    public HttpStatusCode? Status { get; }
}

/// <summary>
/// The phone's line to the FlipPix desktop. Every call has its own time limit, sized to what it
/// does: a status check gives up in seconds, a long poll waits half a minute, a writing request
/// allows the LLM a few minutes.
/// </summary>
public sealed class RemoteClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private string _baseUrl = "";
    private string _token = "";
    private string _name = "the computer";

    /// <summary>Raised when the computer no longer knows this phone's token (it was removed there).</summary>
    public event Action? Unpaired;

    public string BaseUrl => _baseUrl;
    public bool IsConfigured => _baseUrl.Length > 0 && _token.Length > 0;

    public void Configure(string baseUrl, string token, string name)
    {
        _baseUrl = MobileSettings.NormalizeUrl(baseUrl);
        _token = token;
        _name = string.IsNullOrWhiteSpace(name) ? "the computer" : name;
    }

    /// <summary>An absolute URL carrying the token, for the video player, which can't send headers.</summary>
    public string MediaUrl(string relative) =>
        _baseUrl + relative + (relative.Contains('?') ? "&" : "?") + RemoteApi.TokenQuery + "=" + Uri.EscapeDataString(_token);

    // ── Before pairing ─────────────────────────────────────────────────────────────────────────

    public static async Task<HelloDto> HelloAsync(string baseUrl, CancellationToken ct = default)
    {
        var url = MobileSettings.NormalizeUrl(baseUrl);
        if (url.Length == 0) throw new RemoteException("That doesn't look like an address. Try something like 192.168.1.20.");
        using var cts = Limit(ct, TimeSpan.FromSeconds(6));
        try
        {
            using var resp = await Http.GetAsync(url + RemoteApi.Root + "/hello", cts.Token);
            if (!resp.IsSuccessStatusCode) throw new RemoteException("Something answered there, but it isn't FlipPix.");
            var hello = await resp.Content.ReadFromJsonAsync<HelloDto>(cts.Token);
            if (hello?.App != RemoteApi.AppName) throw new RemoteException("Something answered there, but it isn't FlipPix.");
            return hello;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteException("No answer. Check that FlipPix is open on the computer, the phone remote is on, and both are on the same Wi-Fi.");
        }
        catch (HttpRequestException)
        {
            throw new RemoteException("Couldn't reach it. Check that FlipPix is open on the computer, the phone remote is on, and both are on the same Wi-Fi.");
        }
        catch (JsonException)
        {
            throw new RemoteException("Something answered there, but it isn't FlipPix.");
        }
    }

    public static async Task<PairResponse> PairAsync(string baseUrl, string code, string deviceName, CancellationToken ct = default)
    {
        var url = MobileSettings.NormalizeUrl(baseUrl);
        using var cts = Limit(ct, TimeSpan.FromSeconds(10));
        try
        {
            using var resp = await Http.PostAsync(url + RemoteApi.Root + "/pair",
                JsonBody(new PairRequest { Code = code, DeviceName = deviceName }), cts.Token);
            if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, cts.Token);
            return (await resp.Content.ReadFromJsonAsync<PairResponse>(cts.Token))!;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteException("The computer stopped answering. Try again.");
        }
        catch (HttpRequestException)
        {
            throw new RemoteException("Couldn't reach the computer. Is the phone remote still on?");
        }
    }

    // ── Paired ─────────────────────────────────────────────────────────────────────────────────

    public Task<StatusDto> StatusAsync(CancellationToken ct = default) =>
        GetAsync<StatusDto>(RemoteApi.Root + "/status", TimeSpan.FromSeconds(8), ct);

    public Task<JobsPage> JobsAsync(long since, CancellationToken ct) =>
        GetAsync<JobsPage>($"{RemoteApi.Root}/jobs?since={since}&wait=25", TimeSpan.FromSeconds(45), ct);

    public Task<JobDto> CreateJobAsync(JobRequest request, CancellationToken ct = default) =>
        SendAsync<JobDto>(HttpMethod.Post, RemoteApi.Root + "/jobs", JsonBody(request), TimeSpan.FromSeconds(30), ct);

    public Task<JobDto> CancelAsync(string id, CancellationToken ct = default) =>
        SendAsync<JobDto>(HttpMethod.Post, $"{RemoteApi.Root}/jobs/{id}/cancel", null, TimeSpan.FromSeconds(15), ct);

    public Task<JobDto> RetryAsync(string id, CancellationToken ct = default) =>
        SendAsync<JobDto>(HttpMethod.Post, $"{RemoteApi.Root}/jobs/{id}/retry", null, TimeSpan.FromSeconds(15), ct);

    public Task DeleteJobAsync(string id, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{RemoteApi.Root}/jobs/{id}", null, TimeSpan.FromSeconds(15), ct);

    public Task<UploadResponse> UploadAsync(byte[] jpeg, CancellationToken ct = default)
    {
        var body = new ByteArrayContent(jpeg);
        body.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        // Mobile uplinks can be slow; a 1536 px JPEG is under a megabyte.
        return SendAsync<UploadResponse>(HttpMethod.Post, RemoteApi.Root + "/uploads", body, TimeSpan.FromMinutes(2), ct);
    }

    public Task<LibraryPage> LibraryAsync(string? kind, string? folder, int offset, int limit, CancellationToken ct = default)
    {
        var q = new StringBuilder($"{RemoteApi.Root}/library?offset={offset}&limit={limit}");
        if (!string.IsNullOrEmpty(kind)) q.Append("&kind=").Append(Uri.EscapeDataString(kind));
        if (folder != null) q.Append("&folder=").Append(Uri.EscapeDataString(folder));
        return GetAsync<LibraryPage>(q.ToString(), TimeSpan.FromSeconds(20), ct);
    }

    public Task<LibraryDetailDto> LibraryDetailAsync(string id, CancellationToken ct = default) =>
        GetAsync<LibraryDetailDto>($"{RemoteApi.Root}/library/{id}", TimeSpan.FromSeconds(10), ct);

    /// <summary>Writing help from the computer's LLM. A vision model reading a picture can take a minute.</summary>
    public async Task<string> AssistAsync(AssistRequest request, CancellationToken ct = default) =>
        (await SendAsync<AssistResponse>(HttpMethod.Post, RemoteApi.Root + "/assist", JsonBody(request),
            TimeSpan.FromMinutes(4), ct)).Text;

    public async Task<byte[]> GetBytesAsync(string relative, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Get, relative, null, TimeSpan.FromMinutes(2), ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Copies a whole file (a full-size picture or a video) into <paramref name="target"/>.</summary>
    public async Task DownloadToAsync(string relative, Stream target, CancellationToken ct = default)
    {
        using var cts = Limit(ct, TimeSpan.FromMinutes(20));
        using var request = Request(HttpMethod.Get, relative, null);
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw Unreachable();
        }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, cts.Token);
            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
            await body.CopyToAsync(target, cts.Token);
        }
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────────────────

    private async Task<T> GetAsync<T>(string relative, TimeSpan limit, CancellationToken ct) =>
        await SendAsync<T>(HttpMethod.Get, relative, null, limit, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string relative, HttpContent? body, TimeSpan limit, CancellationToken ct)
    {
        using var resp = await SendRawAsync(method, relative, body, limit, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default!;
        using var cts = Limit(ct, limit);
        return (await resp.Content.ReadFromJsonAsync<T>(cts.Token))!;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string relative, HttpContent? body,
        TimeSpan limit, CancellationToken ct)
    {
        if (!IsConfigured) throw new RemoteException("This phone isn't connected to a computer yet.");
        using var cts = Limit(ct, limit);
        using var request = Request(method, relative, body);
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unreachable();
        }
        catch (HttpRequestException)
        {
            throw Unreachable();
        }

        if (resp.IsSuccessStatusCode) return resp;
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized) Unpaired?.Invoke();
            throw await ErrorAsync(resp, ct);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string relative, HttpContent? body)
    {
        var request = new HttpRequestMessage(method, _baseUrl + relative) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private RemoteException Unreachable() =>
        new($"Can't reach {_name}. Is FlipPix open there, with the phone remote on, and is this phone on the same Wi-Fi?");

    private static async Task<RemoteException> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var error = await resp.Content.ReadFromJsonAsync<ErrorDto>(ct);
            if (!string.IsNullOrWhiteSpace(error?.Error)) return new RemoteException(error.Error, resp.StatusCode);
        }
        catch (Exception) { /* not our JSON: fall through */ }
        return new RemoteException($"The computer answered {(int)resp.StatusCode}.", resp.StatusCode);
    }

    private static StringContent JsonBody<T>(T value) =>
        new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

    private static CancellationTokenSource Limit(CancellationToken ct, TimeSpan limit)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        return cts;
    }
}

internal static class HttpContentJson
{
    public static async Task<T?> ReadFromJsonAsync<T>(this HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, RemoteClient.Json, ct);
    }
}

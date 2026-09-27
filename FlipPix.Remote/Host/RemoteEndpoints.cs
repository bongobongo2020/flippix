using System.Text.Json;
using FlipPix.Core.Models;
using FlipPix.Remote.Contracts;
using FlipPix.Remote.Engine;
using FlipPix.Remote.Jobs;
using FlipPix.Remote.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlipPix.Remote.Host;

/// <summary>
/// The routes the phone calls. All under <see cref="RemoteApi.Root"/>; all but /hello and /pair need a
/// paired token. Errors come back as <see cref="ErrorDto"/> with a sentence the phone shows as is.
/// </summary>
internal static class RemoteEndpoints
{
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);
    private static (DateTime At, string Url, bool Online) _probe;

    public static void Map(WebApplication app, RemoteHost host)
    {
        var root = RemoteApi.Root;
        var engine = host.Engine;

        // Errors first, so a throw anywhere below still answers in the phone's shape.
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                // The phone went away mid-request (screen off, Wi-Fi change). Nothing to answer.
            }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                var status = ex is ArgumentException or InvalidOperationException or InvalidDataException or JsonException
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status500InternalServerError;
                if (status == StatusCodes.Status500InternalServerError)
                    engine.Logger.LogError(ex, "Phone request {Path} failed", ctx.Request.Path.Value ?? "");
                ctx.Response.StatusCode = status;
                await ctx.Response.WriteAsJsonAsync(new ErrorDto { Error = JobManager.FirstLine(ex.Message) });
            }
        });

        // Only a paired phone gets past here. Players that can't send headers put the token in the URL.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            var open = path.Equals(root + "/hello", StringComparison.OrdinalIgnoreCase)
                       || path.Equals(root + "/pair", StringComparison.OrdinalIgnoreCase);
            if (!open)
            {
                var header = ctx.Request.Headers.Authorization.ToString();
                var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? header[7..].Trim()
                    : ctx.Request.Query[RemoteApi.TokenQuery].ToString();
                if (host.Authenticate(token) == null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new ErrorDto
                    {
                        Error = "This phone isn't paired with the computer any more. Connect again.",
                    });
                    return;
                }
            }
            await next(ctx);
        });

        app.MapGet(root + "/hello", () => host.Hello());

        app.MapPost(root + "/pair", (PairRequest request) =>
            host.TryPair(request.Code, request.DeviceName) is { } paired
                ? Results.Ok(paired)
                : Results.Json(new ErrorDto
                {
                    Error = "That code didn't match. Check the code in FlipPix on the computer; it changes after a few wrong tries.",
                }, statusCode: StatusCodes.Status403Forbidden));

        app.MapGet(root + "/status", async (CancellationToken ct) =>
        {
            var settings = engine.Settings;
            var (queued, running) = host.Jobs.Counts();
            var folder = engine.OutputRoot();
            return new StatusDto
            {
                Name = host.Name,
                ComfyUrl = RemoteUrls.Normalize(settings.BaseUrl),
                ComfyOnline = await ComfyOnlineAsync(settings.BaseUrl, ct),
                HasLlm = engine.Llm.Target.IsSet,
                LlmLabel = engine.LlmLabel(),
                LibraryFolder = folder,
                LibraryReachable = ComfyUISettings.IsReachableFolder(folder),
                Queued = queued,
                Running = running,
            };
        });

        // ── Photos from the phone ──────────────────────────────────────────────────────────────

        app.MapPost(root + "/uploads", async (HttpRequest request, CancellationToken ct) =>
        {
            var (id, width, height) = await engine.Uploads.SaveAsync(request.Body, ct);
            return new UploadResponse { Id = id, Width = width, Height = height, ThumbUrl = $"{root}/uploads/{id}/thumb" };
        });

        app.MapGet(root + "/uploads/{id}/thumb", (string id) =>
            engine.Uploads.ThumbFor(id) is { } path ? Cached(Results.File(path, "image/jpeg")) : Results.NotFound());

        // ── Jobs ───────────────────────────────────────────────────────────────────────────────

        app.MapGet(root + "/jobs", async (long? since, int? wait, CancellationToken ct) =>
        {
            if (since is null or <= 0) return host.Jobs.Snapshot();
            return await host.Jobs.WaitAsync(since.Value, TimeSpan.FromSeconds(Math.Clamp(wait ?? 25, 0, 30)), ct);
        });

        app.MapPost(root + "/jobs", async (JobRequest request, CancellationToken ct) =>
            await host.Jobs.EnqueueAsync(request, ct));

        app.MapPost(root + "/jobs/{id}/cancel", (string id) =>
            host.Jobs.Cancel(id) ? Results.Ok(host.Jobs.Get(id)) : NotFound("That job has already finished."));

        app.MapPost(root + "/jobs/{id}/retry", (string id) =>
            host.Jobs.Retry(id) is { } job ? Results.Ok(job) : NotFound("There's nothing left to make in that job."));

        app.MapDelete(root + "/jobs/{id}", (string id) =>
            host.Jobs.Remove(id) ? Results.NoContent() : NotFound("That job is still running, or already gone."));

        app.MapGet(root + "/jobs/{id}/items/{index:int}/{what}", async (HttpContext ctx, string id, int index, string what) =>
        {
            var item = host.Jobs.Item(id, index, out _);
            if (item?.Output is not { } output) return Results.NotFound();

            // Served from the output folder when this desktop can see it (fast, seekable); otherwise
            // through ComfyUI's /view.
            if (engine.LocalEntry(item) is { } local) return await ServeEntryAsync(engine, local, what, ctx.RequestAborted);
            switch (what)
            {
                case "file":
                    engine.SyncComfy();
                    await ProxyAsync(ctx, engine.Comfy, output);
                    return Results.Empty;
                case "thumb" or "preview":
                    var side = what == "thumb" ? Thumbnailer.ThumbSide : Thumbnailer.PreviewSide;
                    var path = await engine.ThumbForItemAsync(item, side, ctx.RequestAborted);
                    return path == null ? Results.NotFound() : Cached(Results.File(path, "image/jpeg"));
                default:
                    return Results.NotFound();
            }
        });

        // ── The library: everything in the output folder ───────────────────────────────────────

        app.MapGet(root + "/library", async (string? kind, string? folder, int? offset, int? limit) =>
        {
            var library = engine.Library;
            library.EnsureFresh();
            await library.WaitForScanAsync(TimeSpan.FromSeconds(3));
            var (items, total, folders, problem) = library.Query(kind, folder, offset ?? 0, limit ?? 60);
            return new LibraryPage
            {
                Items = items.Select(ItemDto).ToList(),
                Total = total,
                Offset = offset ?? 0,
                Folders = folders.ToList(),
                Scanning = library.Scanning,
                Problem = total == 0 ? problem : null,
            };
        });

        app.MapGet(root + "/library/{id}", (string id) =>
        {
            var entry = engine.Library.Find(id);
            if (entry == null) return NotFound("That file is no longer in the output folder.");
            var made = host.Jobs.MadeFor(entry.RelativePath);
            return Results.Ok(new LibraryDetailDto
            {
                Item = ItemDto(entry),
                Prompt = made?.Prompt,
                Look = made?.Look,
                Shape = made?.Shape,
                Idea = made?.Idea,
            });
        });

        app.MapGet(root + "/library/{id}/{what}", async (string id, string what, CancellationToken ct) =>
        {
            var entry = engine.Library.Find(id);
            return entry == null ? Results.NotFound() : await ServeEntryAsync(engine, entry, what, ct);
        });

        // ── Writing help: the desktop's LLM ────────────────────────────────────────────────────

        app.MapPost(root + "/assist", async (AssistRequest request, CancellationToken ct) =>
        {
            if (!engine.Llm.Target.IsSet)
                throw new InvalidOperationException("The computer has no writing assistant. Set an LLM server in FlipPix's Settings.");
            engine.SyncComfy(); // an out-of-memory LLM asks ComfyUI to free the GPU
            var jpegs = new List<byte[]>();
            if (!string.IsNullOrEmpty(request.Picture))
                jpegs.Add((await engine.Uploads.ResolveAsync(request.Picture, ct)).Jpeg);

            var (system, user, tokens) = Assist.Prompt(request, jpegs.Count > 0);
            var reply = await engine.Llm.ChatAsync(system, user, jpegs, tokens, 0.7, ct);
            return new AssistResponse { Text = Assist.Clean(reply) };
        });
    }

    private static LibraryItemDto ItemDto(LibraryEntry e)
    {
        var baseUrl = $"{RemoteApi.Root}/library/{e.Id}";
        return new LibraryItemDto
        {
            Id = e.Id,
            Name = e.Name,
            Folder = e.Folder,
            Kind = e.Kind,
            Size = e.Size,
            Modified = new DateTimeOffset(e.ModifiedUtc, TimeSpan.Zero),
            ThumbUrl = baseUrl + "/thumb",
            PreviewUrl = baseUrl + (e.Kind == MediaKinds.Image ? "/preview" : "/thumb"),
            FileUrl = baseUrl + "/file",
        };
    }

    private static async Task<IResult> ServeEntryAsync(RemoteEngine engine, LibraryEntry entry, string what, CancellationToken ct)
    {
        var isVideo = entry.Kind == MediaKinds.Video;
        switch (what)
        {
            case "file":
                return Results.File(entry.FullPath, ContentType(entry.FullPath), enableRangeProcessing: true);
            case "thumb":
            case "preview":
                var side = what == "thumb" ? Thumbnailer.ThumbSide : Thumbnailer.PreviewSide;
                var path = await engine.Thumbs.ForFileAsync(entry.FullPath, entry.PosterPath, isVideo, side, ct);
                return path == null ? Results.NotFound() : Cached(Results.File(path, "image/jpeg"));
            default:
                return Results.NotFound();
        }
    }

    /// <summary>
    /// Streams a ComfyUI output through, forwarding the player's Range header both ways so a video
    /// can be seeked without downloading it first.
    /// </summary>
    private static async Task ProxyAsync(HttpContext ctx, ComfyGateway comfy, ComfyOutput output)
    {
        var ct = ctx.RequestAborted;
        using var upstream = await comfy.OpenViewAsync(output, ctx.Request.Headers.Range.ToString(), ct);
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        var headers = upstream.Content.Headers;
        ctx.Response.ContentType = headers.ContentType?.ToString() ?? ContentType(output.FileName);
        if (headers.ContentLength is { } length) ctx.Response.ContentLength = length;
        if (headers.ContentRange != null) ctx.Response.Headers.ContentRange = headers.ContentRange.ToString();
        ctx.Response.Headers.AcceptRanges = "bytes";
        await using var body = await upstream.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(ctx.Response.Body, ct);
    }

    private static async Task<bool> ComfyOnlineAsync(string url, CancellationToken ct)
    {
        await ProbeGate.WaitAsync(ct);
        try
        {
            // The phone asks on every page change; one probe per five seconds is plenty.
            if (_probe.Url == url && DateTime.UtcNow - _probe.At < TimeSpan.FromSeconds(5)) return _probe.Online;
            var online = await ComfyGateway.ProbeAsync(url, ct) == null;
            _probe = (DateTime.UtcNow, url, online);
            return online;
        }
        finally
        {
            ProbeGate.Release();
        }
    }

    private static IResult NotFound(string message) =>
        Results.Json(new ErrorDto { Error = message }, statusCode: StatusCodes.Status404NotFound);

    private static IResult Cached(IResult result) => new CachedResult(result);

    /// <summary>Thumbnails never change under the same URL's file, so the phone may keep them for a day.</summary>
    private sealed class CachedResult : IResult
    {
        private readonly IResult _inner;
        public CachedResult(IResult inner) => _inner = inner;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "private, max-age=86400";
            return _inner.ExecuteAsync(httpContext);
        }
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        _ => "application/octet-stream",
    };
}

/// <summary>The writing help the phone offers, each a short instruction to the desktop's LLM.</summary>
internal static class Assist
{
    private const string PolishSystem =
        "You turn a short picture idea into one vivid image prompt for a photorealistic image model. " +
        "Describe the subject, what they are doing, the setting, the light, the lens and the mood in " +
        "plain concrete language, 60 to 110 words, one paragraph. Keep every detail the user gave and " +
        "invent nothing that contradicts it. Reply with the prompt only: no title, no quotes, no preamble.";

    private const string ImagePromptSystem =
        "You look at a picture and write one prompt a photorealistic image model could use to make a picture " +
        "like it: the subject and what they are doing, what they wear, the setting, the light, the lens and the " +
        "mood, in plain concrete language, 60 to 110 words, one paragraph. No names, no guesses about identity. " +
        "Reply with the prompt only: no title, no quotes, no preamble.";

    private const string DescribeSystem =
        "You describe a picture for someone who can't see it: who or what is in it, what is happening, the " +
        "setting, the light and the mood. Two to four sentences, plain and warm. No guesses about identity, " +
        "ethnicity or occupation. Reply with the description only.";

    private const string VideoIdeaSystem =
        "You look at a picture and suggest what happens next in it, as the idea for a short video clip: one or " +
        "two sentences of concrete action that fits the people and the place, and, if it suits the moment, a " +
        "short line someone says aloud in quotes. Reply with the idea only: no title, no preamble.";

    public static (string System, string User, int MaxTokens) Prompt(AssistRequest r, bool hasPicture)
    {
        var text = (r.Text ?? "").Trim();
        return r.Task switch
        {
            AssistTasks.Polish when text.Length > 0 => (PolishSystem, text, 400),
            AssistTasks.Polish => throw new ArgumentException("Write a few words first."),
            _ when !hasPicture => throw new ArgumentException("Pick a picture first."),
            AssistTasks.ImagePrompt => (ImagePromptSystem, Also("Write the prompt for this picture.", text), 400),
            AssistTasks.Describe => (DescribeSystem, "Describe this picture.", 300),
            AssistTasks.VideoIdea => (VideoIdeaSystem, Also("What happens next in this picture?", text), 200),
            _ => throw new ArgumentException("Unknown kind of help: " + r.Task),
        };
    }

    private static string Also(string ask, string text) =>
        text.Length == 0 ? ask : ask + " The user adds: " + text;

    public static string Clean(string reply)
    {
        var s = reply.Replace("**", "").Trim();
        foreach (var lead in new[] { "Prompt:", "Description:", "Idea:" })
            if (s.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) s = s[lead.Length..].TrimStart();
        return s.Trim().Trim('"', '“', '”').Trim();
    }
}

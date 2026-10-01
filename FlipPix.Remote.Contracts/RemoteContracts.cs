namespace FlipPix.Remote.Contracts;

/// <summary>
/// Constants both ends agree on. Every route lives under <see cref="Root"/>; everything but
/// <c>/hello</c> and <c>/pair</c> needs the token a pairing handed out, sent as a Bearer header or,
/// for players that can't set headers, as the <see cref="TokenQuery"/> query parameter.
/// </summary>
public static class RemoteApi
{
    public const int DefaultPort = 47800;
    /// <summary>UDP. A phone broadcasts <see cref="DiscoveryProbe"/>; each desktop answers with a <see cref="HelloDto"/>.</summary>
    public const int DiscoveryPort = 47801;
    public const string DiscoveryProbe = "FLIPPIX_DISCOVER_V1";
    public const string Root = "/api/v1";
    public const string TokenQuery = "access_token";
    public const string AppName = "FlipPix";
}

public static class JobKinds
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Story = "story";
}

public static class JobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Stopped = "stopped";

    public static bool IsActive(string state) => state is Queued or Running;
}

public static class ItemStates
{
    public const string Waiting = "waiting";
    public const string Working = "working";
    public const string Done = "done";
    public const string Failed = "failed";
}

public static class MediaKinds
{
    public const string Image = "image";
    public const string Video = "video";
}

/// <summary>Unauthenticated: who is answering. Also the discovery reply, with the port filled in.</summary>
public sealed class HelloDto
{
    public string App { get; init; } = RemoteApi.AppName;
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1";
    public int Port { get; init; }
}

public sealed class PairRequest
{
    public string Code { get; init; } = "";
    public string DeviceName { get; init; } = "";
}

public sealed class PairResponse
{
    public string Token { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>What the desktop can do right now, in the words the phone shows.</summary>
public sealed class StatusDto
{
    public string Name { get; init; } = "";
    public bool ComfyOnline { get; init; }
    public string ComfyUrl { get; init; } = "";
    public bool HasLlm { get; init; }
    public string LlmLabel { get; init; } = "";
    public bool LibraryReachable { get; init; }
    public string LibraryFolder { get; init; } = "";
    public int Queued { get; init; }
    public int Running { get; init; }
    /// <summary>The image looks this computer offers. Empty from an older desktop: all of them.</summary>
    public List<string> Looks { get; init; } = new();
    /// <summary>Model credits to show, e.g. "Video by MiniMax H3". Their licenses require it.</summary>
    public List<string> Credits { get; init; } = new();
}

/// <summary>
/// Everything a job can be asked to do. Fields that don't apply to <see cref="Kind"/> are ignored.
/// A picture reference is <c>upload:&lt;id&gt;</c> (sent from the phone) or <c>library:&lt;id&gt;</c>
/// (already on the desktop).
/// </summary>
public sealed class JobRequest
{
    public string Kind { get; set; } = "";

    // Image
    public string? Prompt { get; set; }
    public string Look { get; set; } = "photo";
    public string Shape { get; set; } = "portrait";
    public int Count { get; set; } = 1;

    // Video
    public string? Idea { get; set; }
    public int Seconds { get; set; } = 10;
    public List<string> Pictures { get; set; } = new();
    /// <summary>A scene already written (a retake): the writer is skipped.</summary>
    public string? Script { get; set; }

    // Story
    public string? Story { get; set; }
    public int Clips { get; set; } = 6;
}

public sealed class JobItemDto
{
    public int Index { get; init; }
    public string State { get; init; } = ItemStates.Waiting;
    public string Status { get; init; } = "";
    public double Progress { get; init; }
    /// <summary>A story clip's beat, in words a person reads.</summary>
    public string? Label { get; init; }
    /// <summary>A story clip's written scene.</summary>
    public string? Text { get; init; }
    public string MediaKind { get; init; } = MediaKinds.Image;
    public string? FileUrl { get; init; }
    public string? ThumbUrl { get; init; }
    public string? PreviewUrl { get; init; }
    /// <summary>The output's id in the library, so it can be reused as a reference picture.</summary>
    public string? LibraryId { get; init; }
}

public sealed class JobDto
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string State { get; init; } = JobStates.Queued;
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public double Progress { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset? Started { get; init; }
    public DateTimeOffset? Finished { get; init; }
    /// <summary>1 = next to start. 0 when not waiting.</summary>
    public int QueuePosition { get; init; }
    /// <summary>Width over height of what it makes, known before anything is made.</summary>
    public double Ratio { get; init; } = 1;
    public JobRequest Request { get; init; } = new();
    public List<JobItemDto> Items { get; init; } = new();
    /// <summary>A video's written scene.</summary>
    public string? Script { get; init; }
    /// <summary>The first reference picture, for a card that has nothing rendered yet.</summary>
    public string? PosterUrl { get; init; }
    /// <summary>Unfinished items can be made again from what was already written.</summary>
    public bool CanRetry { get; init; }
}

public sealed class JobsPage
{
    public long Revision { get; init; }
    public List<JobDto> Jobs { get; init; } = new();
}

public sealed class UploadResponse
{
    public string Id { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public string ThumbUrl { get; init; } = "";
}

public sealed class LibraryItemDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The top-level folder under the output folder, or "" for files directly in it.</summary>
    public string Folder { get; init; } = "";
    public string Kind { get; init; } = MediaKinds.Image;
    public long Size { get; init; }
    public DateTimeOffset Modified { get; init; }
    public string ThumbUrl { get; init; } = "";
    public string PreviewUrl { get; init; } = "";
    public string FileUrl { get; init; } = "";
}

public sealed class LibraryFolderDto
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
}

public sealed class LibraryPage
{
    public List<LibraryItemDto> Items { get; init; } = new();
    public int Total { get; init; }
    public int Offset { get; init; }
    public List<LibraryFolderDto> Folders { get; init; } = new();
    /// <summary>True while the first scan of a large folder is still running.</summary>
    public bool Scanning { get; init; }
    /// <summary>Why there is nothing to show, when the folder itself is the problem.</summary>
    public string? Problem { get; init; }
}

/// <summary>What is known about how a library item was made: only what the remote made itself.</summary>
public sealed class LibraryDetailDto
{
    public LibraryItemDto Item { get; init; } = new();
    public string? Prompt { get; init; }
    public string? Look { get; init; }
    public string? Shape { get; init; }
    public string? Idea { get; init; }
}

public static class AssistTasks
{
    /// <summary>A short idea → a full image prompt.</summary>
    public const string Polish = "polish";
    /// <summary>A picture → a prompt that would make a picture like it.</summary>
    public const string ImagePrompt = "image-prompt";
    /// <summary>A picture → a plain description, for reading.</summary>
    public const string Describe = "describe";
    /// <summary>A picture → one sentence of what could happen next in it.</summary>
    public const string VideoIdea = "video-idea";
}

public sealed class AssistRequest
{
    public string Task { get; init; } = AssistTasks.Polish;
    public string? Text { get; init; }
    public string? Picture { get; init; }
}

public sealed class AssistResponse
{
    public string Text { get; init; } = "";
}

public sealed class ErrorDto
{
    public string Error { get; init; } = "";
}

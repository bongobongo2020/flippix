using FlipPix.Remote.Contracts;
using FlipPix.Remote.Engine;

namespace FlipPix.Remote.Jobs;

/// <summary>
/// A job as the desktop keeps it: what was asked, how far it got, and what it made. Mutated only
/// under <see cref="JobManager"/>'s lock; the phone sees it through <see cref="JobManager.ToDto"/>.
/// Plain settable properties so it round-trips through the jobs file across desktop restarts.
/// </summary>
public sealed class RemoteJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public JobRequest Request { get; set; } = new();
    public string State { get; set; } = JobStates.Queued;
    public string Title { get; set; } = "";
    public string Status { get; set; } = "Waiting its turn";
    public double Progress { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public double Ratio { get; set; } = 1;
    public List<RemoteJobItem> Items { get; set; } = new();

    // Video and story
    /// <summary>ResolutionSelector's aspect option, fixed from the first picture.</summary>
    public string? Aspect { get; set; }
    public string? Script { get; set; }
    public string? PosterRef { get; set; }

    // Story
    /// <summary>The pictures every clip is cast from, a generated portrait included.</summary>
    public List<string> Cast { get; set; } = new();
    /// <summary>One casting-sheet line per cast picture, handed to every writer call.</summary>
    public List<string> CastLines { get; set; } = new();

    public bool IsActive => JobStates.IsActive(State);

    /// <summary>Something is left to make, and what was already written can be reused.</summary>
    public bool CanRetry => !IsActive && (Items.Count == 0 || Items.Any(i => i.State != ItemStates.Done));
}

public sealed class RemoteJobItem
{
    public int Index { get; set; }
    public string State { get; set; } = ItemStates.Waiting;
    public string Status { get; set; } = "Waiting";
    public double Progress { get; set; }
    public string? Label { get; set; }
    public string? Text { get; set; }
    public string MediaKind { get; set; } = MediaKinds.Image;
    public ComfyOutput? Output { get; set; }

    /// <summary>The output's path inside the output folder, when ComfyUI saved it there.</summary>
    public string? RelativePath =>
        Output is { Type: "output" } o
            ? (string.IsNullOrEmpty(o.Subfolder) ? o.FileName : o.Subfolder.Replace('\\', '/').Trim('/') + "/" + o.FileName)
            : null;
}

/// <summary>What the library can say about a file the remote made: the words that made it.</summary>
public sealed class MadeRecord
{
    public string Kind { get; set; } = "";
    public string? Prompt { get; set; }
    public string? Look { get; set; }
    public string? Shape { get; set; }
    public string? Idea { get; set; }
}

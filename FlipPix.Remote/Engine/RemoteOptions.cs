namespace FlipPix.Remote.Engine;

/// <summary>
/// How much of the remote a host exposes. The desktop passes none and gets everything; the iOS
/// Companion narrows it to the two graphs it installs and screens every output before the phone
/// sees it, as the Krea 2 and MiniMax H3 licenses require of an app that makes them available.
/// </summary>
public sealed class RemoteOptions
{
    /// <summary>The image looks the phone may ask for (<see cref="ImageLook.Key"/>). Null: all of them.</summary>
    public IReadOnlySet<string>? Looks { get; init; }

    /// <summary>Checks every picture and video, and every photo the phone sends. Null: nothing is checked.</summary>
    public IContentFilter? Filter { get; init; }

    /// <summary>
    /// List only what the remote made in the phone's library, not everything in the output folder:
    /// a file made some other way was never screened.
    /// </summary>
    public bool LibraryMadeOnly { get; init; }

    /// <summary>Model credits the phone shows, e.g. "Video by MiniMax H3".</summary>
    public IReadOnlyList<string> Credits { get; init; } = Array.Empty<string>();
}

/// <summary>A content classifier the remote runs on pictures before they reach the phone.</summary>
public interface IContentFilter
{
    /// <summary>Null when the picture may be shown; otherwise the reason, in words the phone shows.</summary>
    Task<string?> CheckImageAsync(byte[] image, CancellationToken ct);
}

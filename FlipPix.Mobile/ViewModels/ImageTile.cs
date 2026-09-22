using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.Mobile.Services;

namespace FlipPix.Mobile.ViewModels;

public enum TileState { Waiting, Developing, Done, Failed }

/// <summary>One picture on the contact sheet, from the moment it is asked for until it is shown.</summary>
public partial class ImageTile : ObservableObject
{
    public required string Prompt { get; init; }
    public required ImageLook Look { get; init; }
    public required ImageShape Shape { get; init; }
    public required long Seed { get; init; }

    /// <summary>Width over height, so a waiting tile already has the shape of the picture it will hold.</summary>
    public double Ratio => Shape switch { ImageShape.Portrait => 0.8, ImageShape.Landscape => 1.25, _ => 1.0 };
    public double ShapeWidth => 100 * Ratio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsFailed), nameof(IsPending), nameof(IsDeveloping))]
    private TileState _state = TileState.Waiting;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Waiting its turn";
    [ObservableProperty] private Bitmap? _picture;

    public bool IsDone => State == TileState.Done;
    public bool IsFailed => State == TileState.Failed;
    public bool IsPending => State is TileState.Waiting or TileState.Developing;
    public bool IsDeveloping => State == TileState.Developing;
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FlipPix.Mobile.Controls;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views.Studio;

public partial class StudioViewerView : UserControl
{
    private Point? _pressedAt;
    private TopLevel? _top;

    public StudioViewerView()
    {
        InitializeComponent();
        var stage = this.FindControl<Panel>("Stage")!;
        stage.PointerPressed += (_, e) => _pressedAt = e.GetPosition(this);
        stage.PointerReleased += OnStageReleased;

        this.FindControl<TextBlock>("NoPlayer")!.IsVisible = false;
        DataContextChanged += (_, _) => Watch();
    }

    private void Watch()
    {
        if (DataContext is not ViewerViewModel vm) return;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewerViewModel.IsVideo) or nameof(ViewerViewModel.Picture) or nameof(ViewerViewModel.IsImage))
                UpdateStage(vm);
        };
        UpdateStage(vm);
    }

    // Without a native player (desktop preview, headless checks) the surface stays empty; say so.
    private void UpdateStage(ViewerViewModel vm)
    {
        this.FindControl<TextBlock>("NoPlayer")!.IsVisible = vm.IsVideo && !VideoSurface.IsSupported;
        this.FindControl<Border>("PictureWait")!.IsVisible = vm.IsImage && vm.Picture == null;
    }

    // Beside the picture when there's room; under it in portrait on the smaller iPads and in Split View,
    // where a 360-point column would leave the picture a postage stamp.
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var stage = this.FindControl<Grid>("StageArea")!;
        var inspector = this.FindControl<Border>("Inspector")!;
        var below = e.NewSize.Width < 1000;
        Grid.SetRowSpan(stage, below ? 1 : 2);
        Grid.SetColumnSpan(stage, below ? 2 : 1);
        Grid.SetRow(inspector, below ? 1 : 0);
        Grid.SetRowSpan(inspector, below ? 1 : 2);
        Grid.SetColumn(inspector, below ? 0 : 1);
        Grid.SetColumnSpan(inspector, below ? 2 : 1);
        inspector.Width = below ? double.NaN : 360;
        inspector.MaxHeight = below ? Math.Round(e.NewSize.Height * 0.36) : double.PositiveInfinity;
        inspector.BorderThickness = below ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top != null) _top.KeyDown += OnKeyDown;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_top != null) _top.KeyDown -= OnKeyDown;
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    // With a keyboard attached, the arrows walk the set; Escape is the shell's (it closes the viewer).
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || DataContext is not ViewerViewModel { IsOpen: true } vm) return;
        if (e.Key == Key.Right) { vm.Next(); e.Handled = true; }
        else if (e.Key == Key.Left) { vm.Previous(); e.Handled = true; }
    }

    // A sideways swipe on a picture moves to the next or previous one. Videos keep their touches:
    // the native player draws over this panel and handles its own.
    private void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedAt is not { } start || DataContext is not ViewerViewModel vm) return;
        _pressedAt = null;
        var end = e.GetPosition(this);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) < 60 || Math.Abs(dx) < Math.Abs(dy) * 1.5) return;
        if (dx < 0) vm.Next();
        else vm.Previous();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewerViewModel vm) await ViewerView.SaveCurrentAsync(this, vm);
    }
}

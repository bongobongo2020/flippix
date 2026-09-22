using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FlipPix.Mobile.Views;

public partial class NextUpView : UserControl
{
    public static readonly StyledProperty<string> HeadingProperty = AvaloniaProperty.Register<NextUpView, string>(nameof(Heading), "");
    public static readonly StyledProperty<string> BodyProperty = AvaloniaProperty.Register<NextUpView, string>(nameof(Body), "");
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<NextUpView, Geometry?>(nameof(Icon));

    public string Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public string Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public NextUpView() => InitializeComponent();
}

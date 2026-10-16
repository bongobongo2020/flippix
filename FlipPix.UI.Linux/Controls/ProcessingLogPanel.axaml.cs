using Avalonia;
using Avalonia.Controls;

namespace FlipPix.UI.Linux.Controls;

/// <summary>
/// Reusable collapsible processing-log panel. Tabs bind their own log property to
/// <see cref="LogText"/>; Header / IsLogExpanded / MaxLogHeight are optional overrides.
/// </summary>
public partial class ProcessingLogPanel : UserControl
{
    public static readonly StyledProperty<string> LogTextProperty =
        AvaloniaProperty.Register<ProcessingLogPanel, string>(nameof(LogText), string.Empty);
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<ProcessingLogPanel, string>(nameof(Header), "📝 Processing Log");
    public static readonly StyledProperty<bool> IsLogExpandedProperty =
        AvaloniaProperty.Register<ProcessingLogPanel, bool>(nameof(IsLogExpanded), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> MaxLogHeightProperty =
        AvaloniaProperty.Register<ProcessingLogPanel, double>(nameof(MaxLogHeight), 150.0);

    public ProcessingLogPanel()
    {
        InitializeComponent();
    }

    public string LogText { get => GetValue(LogTextProperty); set => SetValue(LogTextProperty, value); }
    public string Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public bool IsLogExpanded { get => GetValue(IsLogExpandedProperty); set => SetValue(IsLogExpandedProperty, value); }
    public double MaxLogHeight { get => GetValue(MaxLogHeightProperty); set => SetValue(MaxLogHeightProperty, value); }
}

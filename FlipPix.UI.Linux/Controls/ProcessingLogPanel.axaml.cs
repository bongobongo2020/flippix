using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Controls
{
    /// <summary>
    /// Reusable collapsible processing-log panel. Tabs bind their own log property to
    /// <see cref="LogText"/>; Header / IsLogExpanded / MaxLogHeight are optional overrides.
    /// </summary>
    public partial class ProcessingLogPanel : UserControl
    {
        public static readonly StyledProperty<string> LogTextProperty =
            AvaloniaProperty.Register<ProcessingLogPanel, string>(nameof(LogText), string.Empty);

        public string LogText
        {
            get => GetValue(LogTextProperty);
            set => SetValue(LogTextProperty, value);
        }

        public static readonly StyledProperty<string> HeaderProperty =
            AvaloniaProperty.Register<ProcessingLogPanel, string>(nameof(Header), "📝 Processing Log");

        public string Header
        {
            get => GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public static readonly StyledProperty<bool> IsLogExpandedProperty =
            AvaloniaProperty.Register<ProcessingLogPanel, bool>(nameof(IsLogExpanded));

        public bool IsLogExpanded
        {
            get => GetValue(IsLogExpandedProperty);
            set => SetValue(IsLogExpandedProperty, value);
        }

        public static readonly StyledProperty<double> MaxLogHeightProperty =
            AvaloniaProperty.Register<ProcessingLogPanel, double>(nameof(MaxLogHeight), 150.0);

        public double MaxLogHeight
        {
            get => GetValue(MaxLogHeightProperty);
            set => SetValue(MaxLogHeightProperty, value);
        }

        public ProcessingLogPanel() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}

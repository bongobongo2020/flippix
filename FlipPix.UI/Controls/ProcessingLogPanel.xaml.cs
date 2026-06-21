using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace FlipPix.UI.Controls
{
    /// <summary>
    /// Reusable collapsible processing-log panel. Tabs bind their own log
    /// property to <see cref="LogText"/>; Header / IsLogExpanded / MaxLogHeight
    /// are optional overrides.
    /// </summary>
    public partial class ProcessingLogPanel : UserControl
    {
        public ProcessingLogPanel()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty LogTextProperty =
            DependencyProperty.Register(nameof(LogText), typeof(string),
                typeof(ProcessingLogPanel), new PropertyMetadata(string.Empty));

        public string LogText
        {
            get => (string)GetValue(LogTextProperty);
            set => SetValue(LogTextProperty, value);
        }

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string),
                typeof(ProcessingLogPanel), new PropertyMetadata("📝 Processing Log"));

        public string Header
        {
            get => (string)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public static readonly DependencyProperty IsLogExpandedProperty =
            DependencyProperty.Register(nameof(IsLogExpanded), typeof(bool),
                typeof(ProcessingLogPanel), new PropertyMetadata(false));

        public bool IsLogExpanded
        {
            get => (bool)GetValue(IsLogExpandedProperty);
            set => SetValue(IsLogExpandedProperty, value);
        }

        public static readonly DependencyProperty MaxLogHeightProperty =
            DependencyProperty.Register(nameof(MaxLogHeight), typeof(double),
                typeof(ProcessingLogPanel), new PropertyMetadata(150.0));

        public double MaxLogHeight
        {
            get => (double)GetValue(MaxLogHeightProperty);
            set => SetValue(MaxLogHeightProperty, value);
        }
    }
}

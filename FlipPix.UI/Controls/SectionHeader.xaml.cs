using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace FlipPix.UI.Controls
{
    /// <summary>
    /// Compact section heading (accent dot + all-caps label) used inside the side rail
    /// of a two-column tab. The dot picks up the enclosing tab's TabAccentBrush, so tabs
    /// stay visually distinct without repeating a colour on every header.
    /// </summary>
    public partial class SectionHeader : UserControl
    {
        public SectionHeader()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string),
                typeof(SectionHeader), new PropertyMetadata(string.Empty));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty NoteProperty =
            DependencyProperty.Register(nameof(Note), typeof(string),
                typeof(SectionHeader), new PropertyMetadata(string.Empty));

        /// <summary>
        /// Optional muted qualifier shown after the title, e.g. "· required".
        /// Hidden entirely when empty.
        /// </summary>
        public string Note
        {
            get => (string)GetValue(NoteProperty);
            set => SetValue(NoteProperty, value);
        }
    }
}

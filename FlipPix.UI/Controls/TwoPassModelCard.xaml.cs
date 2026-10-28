using UserControl = System.Windows.Controls.UserControl;

namespace FlipPix.UI.Controls
{
    /// <summary>
    /// The 🌹 ErosMax Turbo 2-Pass MODEL &amp; LORAS card. Pure view: it binds to whichever view model hosts it
    /// (🌀 MiniMax I2V or 🎬 H3 Video Editor), both of which expose the same TwoPass* names.
    /// </summary>
    public partial class TwoPassModelCard : UserControl
    {
        public TwoPassModelCard()
        {
            InitializeComponent();
        }
    }
}

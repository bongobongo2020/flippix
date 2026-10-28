using UserControl = System.Windows.Controls.UserControl;

namespace FlipPix.UI.Controls
{
    /// <summary>
    /// The 🌹 ErosMax Turbo 2-Pass SAMPLING, ATTENTION, OUTPUT and REFMODS cards. Pure view: it binds to
    /// whichever view model hosts it (🌀 MiniMax I2V or 🎬 H3 Video Editor), both of which expose the same
    /// TwoPass* and RefMod* names.
    /// </summary>
    public partial class TwoPassTuningCards : UserControl
    {
        public TwoPassTuningCards()
        {
            InitializeComponent();
        }
    }
}

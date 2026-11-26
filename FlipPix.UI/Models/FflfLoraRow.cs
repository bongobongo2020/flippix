using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>One extra LoRA on the ⛓ FFLF tab, stacked after the main one on both passes.</summary>
    public partial class FflfLoraRow : ObservableObject
    {
        [ObservableProperty] private bool _on = true;
        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private double _strength = 0.5;
    }
}

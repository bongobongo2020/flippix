using FlipPix.UI.Services;
using FlipPix.UI.ViewModels.Video;
using Xunit;

namespace FlipPix.Tests;

public class FflfLookTests
{
    private const string Pasted = """
        MASTER VIDEO PROMPT
        Format: POV first-person, 150 s total. A is the only on-screen subject (look = see Wardrobe Prompt; space/light = see Setting Prompt).
        Fight direction: impact-first.

        SETTING PROMPT (standalone)
        Indoor studio, seamless black cyc.

        WARDROBE PROMPT (standalone — character "A")
        Long honey-blonde hair in a low ponytail, navy sports tank.
        """;

    [Fact]
    public void Split_PastedMasterPrompt_FillsTheThreeBoxes()
    {
        var parts = H3FflfViewModel.SplitLook(Pasted.Replace("\n", "\r\n"));

        Assert.NotNull(parts);
        Assert.StartsWith("Format: POV", parts!.Value.Style);
        Assert.Contains("see Wardrobe Prompt", parts.Value.Style);
        Assert.EndsWith("impact-first.", parts.Value.Style);
        Assert.Equal("Indoor studio, seamless black cyc.", parts.Value.Setting);
        Assert.Equal("Long honey-blonde hair in a low ponytail, navy sports tank.", parts.Value.Wardrobe);
    }

    [Fact]
    public void Split_PlainStyle_LeavesItAlone()
    {
        Assert.Null(H3FflfViewModel.SplitLook("Cinematic live action; the setting is a dojo."));
        Assert.Null(H3FflfViewModel.SplitLook("MASTER VIDEO PROMPT\nOnly a master block."));
    }

    [Fact]
    public void Compose_PutsEachBoxUnderItsHeading_AndSkipsEmptyOnes()
    {
        Assert.Equal("Style.", FflfGraph.ComposeLook("Style.", " ", null));
        Assert.Equal("Style.\n\nSETTING: Black void.\n\nWARDROBE (keep identical in every shot): Navy tank.",
            FflfGraph.ComposeLook(" Style. ", "Black void.", "Navy tank."));
        Assert.Equal("SETTING: Black void.", FflfGraph.ComposeLook("", "Black void.", ""));
    }
}

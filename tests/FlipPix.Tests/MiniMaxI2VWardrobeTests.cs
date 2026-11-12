using FlipPix.UI.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// 🌀 MiniMax I2V's SUBJECTS box: reading the detector's reply, and writing the wardrobe block into the idea
/// without ever leaving two of them behind.
/// </summary>
public sealed class MiniMaxI2VWardrobeTests
{
    [Fact]
    public void Detector_lines_become_subjects()
    {
        var reply = "1 | man | left | black leather jacket, white T-shirt, blue jeans.\n"
                  + "- 2 | Woman | right | red silk slip dress, gold hoop earrings\n"
                  + "Some chatter without columns";

        var subjects = MiniMaxI2VViewModel.ParseSubjectLines(reply);

        Assert.Equal(2, subjects.Count);
        Assert.Equal(("man", "left", "black leather jacket, white T-shirt, blue jeans"), subjects[0]);
        Assert.Equal(("woman", "right", "red silk slip dress, gold hoop earrings"), subjects[1]);
    }

    [Fact]
    public void No_people_reads_as_none() =>
        Assert.Empty(MiniMaxI2VViewModel.ParseSubjectLines("NONE"));

    [Fact]
    public void Apply_replaces_an_earlier_block_instead_of_stacking()
    {
        var man = new MiniMaxI2VSubject(1) { Sex = "man", Position = "left", Clothing = "grey suit" };
        var woman = new MiniMaxI2VSubject(2) { Sex = "woman", Position = "right", Clothing = "blue dress." };

        var first = MiniMaxI2VViewModel.ApplyWardrobeBlock("They dance.",
            MiniMaxI2VViewModel.BuildWardrobeBlock(new[] { man, woman }));
        Assert.Equal("They dance.\n\n" + MiniMaxI2VViewModel.WardrobeHeading
                     + "\n- Subject 1 (man, left): grey suit.\n- Subject 2 (woman, right): blue dress.", first);

        man.Clothing = "navy suit";
        var second = MiniMaxI2VViewModel.ApplyWardrobeBlock(first,
            MiniMaxI2VViewModel.BuildWardrobeBlock(new[] { man, woman }));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(second, "Wardrobe \\("));
        Assert.Contains("navy suit", second);
        Assert.DoesNotContain("grey suit", second);
        Assert.StartsWith("They dance.", second);
    }

    [Fact]
    public void Apply_to_an_empty_idea_is_just_the_block()
    {
        var block = MiniMaxI2VViewModel.BuildWardrobeBlock(new[] { new MiniMaxI2VSubject(1) { Sex = "woman", Clothing = "jeans" } });
        Assert.Equal(block, MiniMaxI2VViewModel.ApplyWardrobeBlock("", block));
    }
}

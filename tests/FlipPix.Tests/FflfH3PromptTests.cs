using System;
using System.Linq;
using FlipPix.UI.Services;
using Xunit;

namespace FlipPix.Tests;

/// <summary>The ⛓ FFLF beat as a MiniMax H3 full-reference prompt (VIDEO_PROMPT_WRITING_GUIDE_ref_en).</summary>
public class FflfH3PromptTests
{
    private const string Beat =
        "[9 s] B raises his arms to defend. A grabs his wrist and throws B over her shoulder. " +
        "Sound: Feet scuffing packed dirt, a sharp exhale, a heavy thud.";

    private static readonly FflfGraph.H3CastMember[] Cast =
    {
        new("A", "on the left", "woman, athletic build, long blonde hair", "A wears a brown leather vest and grey trousers."),
        new("B", "on the right", "man, muscular build, bald with a dark beard", "B wears loose black trousers, bare-chested."),
    };

    private static string Compose(bool identity = true, bool first = false, string? spoken = null, string? style = null) =>
        FflfGraph.ComposeH3Prompt(Beat, 9, style ?? "Live-action, cinematic, a locked-off medium-wide shot.", "A dirt arena ringed by tents.",
            Cast, identity, first, new[] { "A (the person on the left)", "B (the person on the right)" }, spoken);

    [Fact]
    public void HasTheGuidesSixSectionsInOrder()
    {
        var prompt = Compose();
        var order = new[] { "subject_definitions:", "summary:", "retention_analysis:", "detailed_description:", "overall_soundscape:", "non_diegetic_music:" }
            .Select(h => prompt.IndexOf(h, StringComparison.Ordinal)).ToList();

        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.StartsWith("subject_definitions:", prompt);
        Assert.EndsWith("non_diegetic_music:\nN/A", prompt.Replace("\r", string.Empty));
    }

    [Fact]
    public void DefinesTheStartFrame_AndCitesTheOtherPicturesInsideEachPerson()
    {
        var prompt = Compose();

        Assert.Contains("<Picture 1> is the first frame of [Shot 1]", prompt);
        // Identity image is <Picture 2>, the sheets follow: A's is 3, B's is 4. None gets a standalone entry (guide §2.2).
        Assert.Contains("<Subject 1> is A, the person on the left, in <Picture 1>, <Picture 2>, and <Picture 3> (their character sheet", prompt);
        Assert.Contains("<Subject 2> is B, the person on the right, in <Picture 1>, <Picture 2>, and <Picture 4>", prompt);
        Assert.DoesNotContain("\n<Picture 2> is", prompt);
        Assert.Contains("wearing a brown leather vest and grey trousers", prompt);
        Assert.Contains("<Subject 3> is the place in <Picture 1>: a dirt arena ringed by tents.", prompt);
        Assert.DoesNotContain("[9 s]", prompt);
    }

    [Fact]
    public void SummaryAndRetentionUseTheGuidesMarkers()
    {
        var prompt = Compose();

        Assert.Contains("summary:\n[keyframe completion + reference generation] A single continuous 9-second shot", prompt.Replace("\r", string.Empty));
        Assert.Contains("<Picture 1> ([Shot 1] first frame): fully_preserved", prompt);
        Assert.Contains("<Subject 1> (appears in [Shot 1]): fully_preserved", prompt);
    }

    [Fact]
    public void TheShot_KeepsTheBeat_BindsTheLetters_AndTakesTheSoundOut()
    {
        var prompt = Compose().Replace("\r", string.Empty);
        var description = prompt[prompt.IndexOf("detailed_description:", StringComparison.Ordinal)..prompt.IndexOf("overall_soundscape:", StringComparison.Ordinal)];

        Assert.Contains("Live-action, cinematic, a locked-off medium-wide shot.\n[Shot 1] A is <Subject 1>. B is <Subject 2>.", description);
        Assert.Contains("A grabs his wrist and throws B over her shoulder.", description);
        Assert.Contains("No one speaks.", description);
        Assert.DoesNotContain("Sound:", description);
        Assert.Contains("overall_soundscape:\nFeet scuffing packed dirt, a sharp exhale, a heavy thud.", prompt);
    }

    [Fact]
    public void DropsTheWholeVideosRuntimeFromTheStyle()
    {
        var prompt = Compose(style: "Two-hander drama. Total runtime: 150 seconds (10 beats x 15 seconds). Locked-off camera.");

        Assert.DoesNotContain("150 seconds", prompt);
        Assert.Contains("Two-hander drama. Locked-off camera.", prompt);
    }

    [Fact]
    public void ASpokenLine_GoesInTheShotWithASpeakerId()
    {
        var prompt = Compose(spoken: "\"You lose.\"");

        Assert.Contains("(S1) says, <d>[English] You lose.</d>", prompt);
        Assert.DoesNotContain("No one speaks.", prompt);
        // The tab's dialogue direction is not added a second time.
        Assert.Equal(prompt, FflfGraph.WithDialogueDirection(prompt));
    }

    [Fact]
    public void DialogueWithoutAWrittenLine_PutsTheDirectionInTheShot()
    {
        var prompt = FflfGraph.WithDialogueDirection(Compose()).Replace("\r", string.Empty);
        var description = prompt[..prompt.IndexOf("overall_soundscape:", StringComparison.Ordinal)];

        Assert.Contains(FflfGraph.DialogueDirection, description);
        Assert.DoesNotContain("No one speaks.", prompt);
        Assert.EndsWith("N/A", prompt);
    }

    [Fact]
    public void WithoutACast_ThePeopleOfTheFirstFrameAreOneSubject()
    {
        var prompt = FflfGraph.ComposeH3Prompt("She turns to the window.", 5, null, null, Array.Empty<FflfGraph.H3CastMember>(),
                                               hasIdentityImage: false, isFirstBeat: true);

        Assert.Contains("<Subject 1> is the people in <Picture 1>", prompt);
        Assert.Contains("The natural ambient sound of the place", prompt);
        Assert.Contains("the video starts exactly on it.", prompt);
    }

    [Fact]
    public void SplitSound_OnlyTakesATrailingSoundSentence()
    {
        Assert.Equal(("A waves.", "Wind."), FflfGraph.SplitSound("A waves. Sound: Wind"));
        Assert.Equal(("A waves.", "Birdsong, a door creaks."), FflfGraph.SplitSound("A waves. Sounds — birdsong, a door creaks."));
        Assert.Equal(("A hears a sound: a bell.", string.Empty), FflfGraph.SplitSound("A hears a sound: a bell."));
    }

    [Fact]
    public void H3Cast_GivesEachPersonTheirOwnWardrobeParagraph()
    {
        var people = new[]
        {
            new FflfCastMember("A", "left, foreground", "woman, blonde", "vest"),
            new FflfCastMember("B", "right", "man, bald", "trousers"),
        };
        var cast = FflfGraph.H3Cast(people, "A wears a brown vest.\n\nB wears black trousers.");

        Assert.Equal("on the left, foreground", cast[0].Where);
        Assert.Equal("A wears a brown vest.", cast[0].Wardrobe);
        Assert.Equal("B wears black trousers.", cast[1].Wardrobe);

        var fromWardrobe = FflfGraph.H3Cast(Array.Empty<FflfCastMember>(), "A wears red.\n\nB wears blue.");
        Assert.Equal(new[] { "A", "B" }, fromWardrobe.Select(c => c.Letter));
    }
}

using System.Linq;
using FlipPix.UI.Services;
using Xunit;

namespace FlipPix.Tests;

public class FflfStoryEngineTests
{
    // The layout of the Schopenhauer explainer sheet: ===== rules, bracketed headings, copied notes, wrapped lines.
    private const string Sheet = """
        ====================================================================
        [MASTER PROMPT]
        ====================================================================
        Indoor studio, single-subject POV explainer video. Total runtime: 45
        seconds (3 beats x 15 seconds). One woman ("A") faces the camera.

        ====================================================================
        [LOCATION]
        ====================================================================
        (Separate prompt — use independently of beats)
        Indoor photography studio. Seamless soft-gray paper backdrop.

        ====================================================================
        [WARDROBE]
        ====================================================================
        (Separate prompt — use independently of beats)
        A wears an elegant cream silk blouse with softly rolled sleeves.

        ====================================================================
        [BEATS]
        ====================================================================
        (15 seconds each. Actions refer to "A". Eyes stay on camera at all
        times. All motion is slow, fluid, single-take friendly.)

        BEAT 1 — A leans in slightly toward the lens, warm smile building;
           opens one palm softly toward the viewer in greeting.

        BEAT 2 — A tilts her head with curiosity; raises one hand.

        BEAT 3 — A visibly relaxes: shoulders drop, slow deep breath in and
           out; a calm stillness takes over her face.

        ====================================================================
        [DIALOGUE]
        ====================================================================
        (Spoken audio, recorded separately. Each block corresponds to the
        matching beat. Natural, warm, measured pace.)

        --- DIALOGUE 1 (Beat 1) ---
        "Let me show you one idea that can change the way you see
        everything."

        --- DIALOGUE 2 (Beat 2) ---
        "He called the first side 'representation.'"

        --- DIALOGUE 3 (Beat 3) ---
        "But there is one escape — brief, but real."
        """;

    [Fact]
    public void Parse_TheExampleSheet_FillsEveryPart()
    {
        var script = FflfStoryEngine.Parse(Sheet.Replace("\n", "\r\n"));

        Assert.StartsWith("Indoor studio, single-subject POV explainer video. Total runtime: 45 seconds", script.MasterPrompt);
        Assert.Equal("Indoor photography studio. Seamless soft-gray paper backdrop.", script.Location);
        Assert.Equal("A wears an elegant cream silk blouse with softly rolled sleeves.", script.Wardrobe);

        Assert.Equal(3, script.Beats.Count);
        Assert.Equal("A leans in slightly toward the lens, warm smile building; opens one palm softly toward the viewer in greeting.", script.Beats[0]);
        Assert.Equal("A tilts her head with curiosity; raises one hand.", script.Beats[1]);
        Assert.StartsWith("A visibly relaxes: shoulders drop", script.Beats[2]);

        Assert.Equal(3, script.Dialogue.Count);
        Assert.Equal("Let me show you one idea that can change the way you see everything.", script.Dialogue[0]);
        Assert.Equal("He called the first side 'representation.'", script.Dialogue[1]);
        Assert.Equal("But there is one escape — brief, but real.", script.Dialogue[2]);
    }

    [Fact]
    public void Parse_MarkdownHeadingsAndPlainNumbers()
    {
        const string reply = """
            <think>planning the beats</think>
            ## MASTER PROMPT
            Two-hander drama, one continuous shot. A and B sit at a kitchen table.

            ## SETTING
            A small kitchen at dusk.

            ## WARDROBE
            A wears a grey hoodie.

            B wears a green cardigan.

            ## BEATS
            1. A slides a letter across the table.
            2. B reads it, then looks up.

            ## DIALOGUE
            DIALOGUE 1: A: "You should read this."
            DIALOGUE 2: ""
            """;

        var script = FflfStoryEngine.Parse(reply);

        Assert.Equal("A small kitchen at dusk.", script.Location);
        Assert.Equal("A wears a grey hoodie.\n\nB wears a green cardigan.", script.Wardrobe);
        Assert.Equal(new[] { "A slides a letter across the table.", "B reads it, then looks up." }, script.Beats);
        Assert.Equal(new[] { "You should read this.", "" }, script.Dialogue);
        Assert.True(script.HasDialogue);
    }

    [Fact]
    public void Parse_NoBeats_Throws()
    {
        Assert.Throws<System.InvalidOperationException>(() => FflfStoryEngine.Parse("[MASTER PROMPT]\nOnly a master prompt.\n[BEATS]\n"));
    }

    [Fact]
    public void ToBeatsText_RoundTripsThroughTheBeatBox_WithoutLosingWords()
    {
        var beats = new[] { "A leans in toward the lens.", "A visibly relaxes: shoulders drop, a slow breath." };

        var parsed = FflfGraph.ParseBeats(FflfStoryEngine.ToBeatsText(beats));

        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, p => Assert.Equal(string.Empty, p.Title));
        Assert.Equal("A leans in toward the lens.", parsed[0].Text);
        Assert.Equal("A visibly relaxes — shoulders drop, a slow breath.", parsed[1].Text);
    }

    [Fact]
    public void BuildRequest_SizesTheLinesToTheBeat()
    {
        var request = FflfStoryEngine.BuildRequest("A story.", 10, 15, withDialogue: true, hasOpeningImage: false);

        Assert.Contains("Exactly 10 beats of 15 seconds each — total runtime 150 seconds", request);
        Assert.Contains("about 39 words and never more than 44", request);
        Assert.Contains("A story.", request);
    }

    [Fact]
    public void BuildRequest_WritesTheSheetAroundTheOpeningPicture()
    {
        const string picture = "PEOPLE: one woman (A), thirties, red bob haircut. WARDROBE: A wears a green raincoat.";
        var request = FflfStoryEngine.BuildRequest("A knight rides to war in silver armour.", 4, 10,
            withDialogue: false, hasOpeningImage: true, openingImage: picture);

        Assert.Contains("THE OPENING PICTURE", request);
        Assert.Contains(picture, request);
        Assert.Contains("BEAT 1 starts from the exact pose in the picture", request);
        Assert.Contains("the picture wins", request);
        // The picture comes before the story, so the writer reads it first.
        Assert.True(request.IndexOf(picture) < request.IndexOf("THE STORY:"));
    }

    [Fact]
    public void BuildRequest_WithoutADescriptionStillSaysThereIsAnOpeningPicture()
    {
        var request = FflfStoryEngine.BuildRequest("A story.", 2, 5, withDialogue: false, hasOpeningImage: true, openingImage: "  ");

        Assert.DoesNotContain("THE OPENING PICTURE", request);
        Assert.Contains("existing opening picture", request);
    }
}

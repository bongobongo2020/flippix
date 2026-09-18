using System.Linq;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// <see cref="H3SpecPrompt"/> — ⚡ H3 Express's 📐 Singularity spec build — and the places a six-section clip has
/// to pass through unharmed: the stamp, the description editor and the clothing re-dress.
/// </summary>
public class H3SpecPromptTests
{
    private static readonly H3SpecPrompt.CastMember[] Duo =
    {
        new(1, "man", "a black leather jacket, grey jeans and brown boots"),
        new(2, "woman", "a red silk blouse and dark trousers"),
    };

    private static readonly StoryContinuity.Environment Alley =
        new(false, "the alley behind the club", "night", "heavy rain, neon signs");

    private const string Wardrobe =
        "Character 1 (<Picture 1>, a man) wears: a black leather jacket, grey jeans and brown boots.\n" +
        "Character 2 (<Picture 2>, a woman) wears: a red silk blouse and dark trousers.";

    /// <summary>A reply the way a local writer actually decorates one: a heading, a hyphenated label, a picture
    /// tag from habit, a subject tag missing its bracket, and the wrong mode marker.</summary>
    private const string MessyReply =
        "Here is the clip.\n" +
        "summary: [keyframe completion + reference generation] <Picture 1> chases <Subject 2> down the alley.\n\n" +
        "## Detailed Description:\n" +
        "[Shot 1] Live-action. A medium tracking shot follows <Picture 1> as he runs, <Subject 2 ahead of him.\n\n" +
        "[Shot 2] At 00:05.000, <Subject 1> lunges and <Subject 2> recoils against the wall.\n\n" +
        "overall_soundscape: Rain drumming on metal, footsteps on wet stone.\n" +
        "non-diegetic music: A low pulsing synth.";

    private static string Normalized(string reply) =>
        H3SpecPrompt.RenderWriterSections(H3SpecPrompt.ToSubjectTags(reply));

    private static string SpecBody() => H3SpecPrompt.Assemble(
        H3SpecPrompt.BuildSubjectDefinitions(Duo, Alley, setting: null),
        H3SpecPrompt.WithSummaryMarker("<Subject 1> chases <Subject 2> down the alley and pins her to the wall."),
        H3SpecPrompt.BuildRetentionAnalysis(Duo),
        "[Shot 1] A medium tracking shot follows <Subject 1>, wearing a black leather jacket.\n\n" +
        "[Shot 2] At 00:05.000, <Subject 1> lunges and <Subject 2>, wearing a red silk blouse, recoils.",
        "Rain drumming on metal, footsteps on wet stone.",
        "A low pulsing synth.");

    // ── Reading the writer ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_decorated_reply_comes_back_as_four_canonical_sections_with_subject_tags()
    {
        var body = Normalized(MessyReply);
        var s = H3SpecPrompt.Parse(body);

        Assert.StartsWith("summary:\n", body);
        foreach (var label in H3SpecPrompt.WriterSections)
            Assert.NotEqual(string.Empty, s[label]);
        Assert.DoesNotContain("Here is the clip", body);
        Assert.DoesNotContain("<Picture", body);
        Assert.Contains("<Subject 2> ahead of him", body);
        Assert.Equal("A low pulsing synth.", s[H3SpecPrompt.NonDiegeticMusic]);
    }

    [Fact]
    public void A_reply_naming_both_subjects_passes()
    {
        Assert.Null(H3SpecPrompt.Validate(Normalized(MessyReply), castCount: 2));
    }

    [Fact]
    public void A_reply_that_never_names_the_second_subject_is_sent_back()
    {
        var reply = Normalized(MessyReply.Replace("<Subject 2", "the woman"));
        var complaint = H3SpecPrompt.Validate(reply, castCount: 2);

        Assert.NotNull(complaint);
        Assert.Contains("<Subject 2>", complaint);
    }

    [Fact]
    public void A_reply_that_invents_a_third_subject_is_sent_back()
    {
        var reply = Normalized(MessyReply.Replace("recoils against the wall.", "recoils as <Subject 3> watches."));
        var complaint = H3SpecPrompt.Validate(reply, castCount: 2);

        Assert.NotNull(complaint);
        Assert.Contains("<Subject 3>", complaint);
    }

    [Fact]
    public void A_reply_with_no_shots_section_is_sent_back()
    {
        Assert.NotNull(H3SpecPrompt.Validate("summary:\n[reference generation] Something happens.", castCount: 1));
    }

    // ── The assembled body ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_body_carries_the_six_sections_in_the_spec_order()
    {
        var body = SpecBody();
        var order = new[]
        {
            H3SpecPrompt.SubjectDefinitions, H3SpecPrompt.Summary, H3SpecPrompt.RetentionAnalysis,
            H3SpecPrompt.DetailedDescription, H3SpecPrompt.OverallSoundscape, H3SpecPrompt.NonDiegeticMusic,
        }.Select(label => body.IndexOf("\n" + label, System.StringComparison.Ordinal) is var i && i >= 0 ? i
                              : body.StartsWith(label, System.StringComparison.Ordinal) ? 0 : -1).ToList();

        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.True(H3SpecPrompt.IsSpecBody(body));
    }

    [Fact]
    public void The_code_sections_bind_subjects_to_pictures_and_hold_the_wardrobe_and_the_place()
    {
        var s = H3SpecPrompt.Parse(SpecBody());
        var definitions = s[H3SpecPrompt.SubjectDefinitions];
        var retention = s[H3SpecPrompt.RetentionAnalysis];

        Assert.Contains("<Subject 2>: Character 2, a woman", definitions);
        Assert.Contains("(<Picture 2>)", definitions);
        Assert.Contains("a red silk blouse and dark trousers.", definitions);
        Assert.Contains("<Environment>: Exterior.", definitions);
        Assert.Contains("<Picture 1> → <Subject 1>: partially_preserved", retention);
        Assert.DoesNotContain("fully_preserved", retention);
    }

    [Fact]
    public void The_summary_is_opened_by_exactly_one_reference_generation_marker()
    {
        var marked = H3SpecPrompt.WithSummaryMarker("[keyframe completion + reference generation] A chase.");

        Assert.Equal("[reference generation] A chase.", marked);
        Assert.Equal(marked, H3SpecPrompt.WithSummaryMarker(marked));
    }

    [Fact]
    public void An_empty_score_is_written_as_NA()
    {
        var body = H3SpecPrompt.Assemble("<Subject 1>: x", "[reference generation] y", "z", "[Shot 1] w", "rain", "");
        Assert.Equal("N/A", H3SpecPrompt.Parse(body)[H3SpecPrompt.NonDiegeticMusic]);
    }

    // ── Through the stamp, the editor and the re-dress ─────────────────────────────────────────

    [Fact]
    public void Strip_undoes_Apply_on_a_spec_body_and_restamping_is_idempotent()
    {
        var cast = new CastPromptStamp.CastInfo("man", "woman");
        var body = SpecBody();

        var stamped = CastPromptStamp.Apply(body, 3, 3, Wardrobe, selectiveCast: false, cast);

        Assert.StartsWith(CastPromptStamp.ReferenceLinePrefix, stamped);
        Assert.Contains("(@char2_front)", stamped);
        Assert.Contains("<Subject 2>", stamped);
        Assert.Equal(body, CastPromptStamp.Strip(stamped));
        Assert.Equal(stamped, CastPromptStamp.Apply(CastPromptStamp.Strip(stamped), 3, 3, Wardrobe, false, cast));
    }

    [Fact]
    public void The_description_of_a_spec_body_is_its_shots()
    {
        var body = SpecBody();
        var shots = H3SpecPrompt.Parse(body)[H3SpecPrompt.DetailedDescription];

        Assert.Equal(shots, CastPromptStamp.ExtractDescription(body));

        var edited = CastPromptStamp.ReplaceDescription(body, "[Shot 1] A static close-up on <Subject 1>.");
        var s = H3SpecPrompt.Parse(edited);
        Assert.Equal("[Shot 1] A static close-up on <Subject 1>.", s[H3SpecPrompt.DetailedDescription]);
        Assert.Equal("Rain drumming on metal, footsteps on wet stone.", s[H3SpecPrompt.OverallSoundscape]);
        Assert.Contains("<Subject 1>: Character 1, a man", s[H3SpecPrompt.SubjectDefinitions]);
    }

    [Fact]
    public void A_three_field_body_still_reads_as_before()
    {
        const string plain = "integrated_multimodal_description: [Shot 1] <Picture 1> runs.\n\noverall_soundscape: Rain.";

        Assert.Equal("[Shot 1] <Picture 1> runs.", CastPromptStamp.ExtractDescription(plain));
        Assert.False(H3SpecPrompt.IsSpecBody(plain));
    }

    [Fact]
    public void A_redress_must_keep_every_spec_section_and_subject_tag()
    {
        var body = SpecBody();
        var changes = new[] { new ClipRedress.Change(2, "woman", "a red silk blouse and dark trousers", "a blue denim jacket") };

        var request = ClipRedress.BuildRequest(body, changes, string.Empty);
        Assert.Contains("<Subject 2> is a woman.", request);
        Assert.Contains("starting with subject_definitions:", request);

        Assert.Null(ClipRedress.Validate(body, body.Replace("a red silk blouse and dark trousers", "a blue denim jacket")));
        Assert.NotNull(ClipRedress.Validate(body, body.Replace("retention_analysis:", "retention:")));
        Assert.NotNull(ClipRedress.Validate(body, body.Replace("<Subject 2>", "the woman")));
    }

    // ── Fight direction ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_next_clip_is_handed_the_last_shot_without_its_marker_and_the_score()
    {
        var body = SpecBody();

        Assert.Equal("<Subject 1> lunges and <Subject 2>, wearing a red silk blouse, recoils.", H3SpecPrompt.LastShot(body));

        var handoff = H3SpecPrompt.Handoff(body, castCount: 2);
        Assert.Contains("<Subject 1> lunges and <Subject 2>", handoff);
        Assert.DoesNotContain("[Shot 2]", handoff);
        Assert.Contains("the same facing", handoff);
        Assert.Contains("A low pulsing synth.", handoff);
    }

    [Fact]
    public void There_is_no_handoff_from_a_clip_with_no_shots()
    {
        Assert.Equal(string.Empty, H3SpecPrompt.Handoff(null, castCount: 2));
        Assert.Equal(string.Empty, H3SpecPrompt.Handoff("summary:\n[reference generation] Nothing.", castCount: 2));
    }

    [Fact]
    public void An_N_A_score_is_not_handed_on()
    {
        var body = SpecBody().Replace("A low pulsing synth.", "N/A");
        Assert.DoesNotContain("THE SCORE SO FAR", H3SpecPrompt.Handoff(body, castCount: 2));
    }

    [Fact]
    public void A_beat_with_quoted_lines_must_have_them_spoken()
    {
        Assert.True(H3SpecPrompt.BeatHasLines("CHARACTER 2 swings at CHARACTER 1. CHARACTER 2: \"You're dead.\""));
        Assert.True(H3SpecPrompt.BeatHasLines("CHARACTER 1: “Get up.”"));
        Assert.False(H3SpecPrompt.BeatHasLines("CHARACTER 2 swings at CHARACTER 1 and misses."));

        var silent = Normalized(MessyReply);
        Assert.Null(H3SpecPrompt.Validate(silent, castCount: 2));
        Assert.Contains("<d>", H3SpecPrompt.Validate(silent, castCount: 2, beatHasLines: true));

        var spoken = silent.Replace("recoils against the wall.",
            "recoils against the wall. <Subject 2> (S1) says: <d>[English] Get away from me.</d>");
        Assert.Null(H3SpecPrompt.Validate(spoken, castCount: 2, beatHasLines: true));
    }

    [Fact]
    public void A_two_hander_is_blocked_facing_each_other_with_both_fighters_working()
    {
        var duo = H3SpecPrompt.RulesFor(2, 15, 3, setting: null, hasContinuityPlan: true, lastClip: false);
        Assert.Contains("FACE EACH OTHER", duo);
        Assert.Contains("THE FIGHT", duo);
        Assert.Contains("A line gets its own shot", duo);
        Assert.DoesNotContain("write dialogue ONLY if", duo);

        var solo = H3SpecPrompt.RulesFor(1, 15, 3, setting: null, hasContinuityPlan: true, lastClip: false);
        Assert.DoesNotContain("THE FIGHT", solo);
        Assert.Contains("One main action chain per shot", solo);
    }

    [Fact]
    public void The_fight_director_beat_sheet_asks_for_dialogue_in_the_shape_the_parser_reads()
    {
        var system = H3SpecPrompt.DirectorBeatSheetSystem(castCount: 2, continuity: true);
        Assert.Contains("fight director", system);
        Assert.Contains("CHARACTER 2: \"<a line>\"", system);
        Assert.Contains("dawn, morning, midday, afternoon", system);   // the shared continuity rules ride along
        Assert.DoesNotContain("Invent no event", system);

        // A director beat: action, a quoted line, then the environment suffix — the line stays in the beat.
        var (_, beats) = StoryBeatSheet.Parse(
            "SETTING: a bar at night\n" +
            "1. CHARACTER 2 swings a bottle at CHARACTER 1, who ducks and drives him into the bar. " +
            "CHARACTER 2: \"You're dead.\" [INT | the bar | night | neon signs]\n");

        Assert.Single(beats);
        Assert.Contains("CHARACTER 2: \"You're dead.\"", beats[0].Text);
        Assert.Equal("INT | the bar | night | neon signs", beats[0].Env);
    }
}

using System.Collections.Generic;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// The H3 Video Editor's ✍ H3 rewriter: what the model is asked, how its reply is read back, which replies are
/// sent back for a rewrite, and the six-section clip that is finally rendered.
/// </summary>
public class H3RewriterBeatWriterTests
{
    private static readonly IReadOnlyList<H3SpecPrompt.CastMember> Cast = new[]
    {
        new H3SpecPrompt.CastMember(1, "woman", "a green canvas jacket and black jeans"),
        new H3SpecPrompt.CastMember(2, "man", "a grey wool coat"),
    };

    private static H3RewriterBeatWriter.BeatContext Ctx(bool continuous = false, int plate = 3,
                                                        string beat = "<Subject 1> opens the door.") =>
        new(ClipIndex: 1, ClipCount: 4, Seconds: 8, Aspect: "16:9 (Widescreen)",
            Cast: new[]
            {
                new H3RewriterBeatWriter.CastVoice(1, "woman", "a green canvas jacket", "a warm, quick alto"),
                new H3RewriterBeatWriter.CastVoice(2, "man", "a grey wool coat", ""),
            },
            PlateSubject: plate, SceneSentence: "Interior. A narrow bookshop, late evening.",
            ContinuityBlock: "", Setting: "A small coastal town.", Beat: beat,
            PreviousBeat: "<Subject 2> waits outside.", NextBeat: null, ContinuousTake: continuous);

    private const string Good =
        "summary:\n[reference generation] <Subject 1> lets <Subject 2> in.\n\n" +
        "detailed_description:\nThe target video is live-action and cinematic.\n" +
        "[Shot 1] A medium shot frames <Subject 1> at the door of <Subject 3>. The camera pushes in with small " +
        "amplitude at slow speed as she turns the handle with her right hand. <Subject 1> (S1), in a warm, quick alto, " +
        "says: <d>[English] You came back.</d> She closes her mouth. " +
        "[Shot 2] At 00:04.500, the camera cuts to <Subject 2> stepping inside.\n\n" +
        "overall_soundscape:\nThe door creaks and rain taps the glass.\n\n" +
        "non_diegetic_music:\nN/A";

    [Fact]
    public void A_well_formed_clip_passes()
    {
        Assert.Null(H3RewriterBeatWriter.Validate(H3RewriterBeatWriter.Normalize(Good), Ctx()));
    }

    [Fact]
    public void The_request_lists_every_tag_the_voice_and_the_plate()
    {
        var req = H3RewriterBeatWriter.BuildRequest(Ctx(), string.Empty);
        Assert.Contains("<Subject 1> is Character 1, a woman, wearing a green canvas jacket. VOICE: a warm, quick alto.", req);
        Assert.Contains("<Subject 2> is Character 2, a man", req);
        Assert.Contains("<Subject 3> is the location plate: Interior. A narrow bookshop, late evening.", req);
        Assert.Contains("LAST clip", req);
        Assert.DoesNotContain("REJECTED", req);
        Assert.Contains("REJECTED", H3RewriterBeatWriter.BuildRequest(Ctx(), "[Shot 1] must not have a timestamp."));
    }

    [Fact]
    public void Reasoning_fences_and_the_base_label_are_read_through()
    {
        var raw = "<think>planning the shot</think>\n```\n**integrated_multimodal_description:** [Shot 1] A wide shot " +
                  "frames <Subject 1>.\n\noverall_soundscape: Wind.\n\nnon_diegetic_music: N/A\n```";
        var body = H3RewriterBeatWriter.Normalize(raw);
        Assert.StartsWith("detailed_description:\n[Shot 1] A wide shot", body);
        Assert.DoesNotContain("<think>", body);
        Assert.DoesNotContain("```", body);
        Assert.DoesNotContain("**", body);
    }

    [Fact]
    public void A_chained_clip_with_a_cut_is_sent_back()
    {
        var complaint = H3RewriterBeatWriter.Validate(H3RewriterBeatWriter.Normalize(Good), Ctx(continuous: true));
        Assert.NotNull(complaint);
        Assert.Contains("ONE CONTINUOUS TAKE", complaint);
    }

    [Theory]
    [InlineData("[Shot 1] At 00:00.000, <Subject 1> waits.", "must not have a timestamp")]
    [InlineData("[Shot 1] <Subject 1> waits. [Shot 2] (0s-4s) cut.", "must open with a single cut time")]
    [InlineData("[Shot 1] <Subject 1> waits. [Shot 2] At 00:09.000, cut.", "outside the 8s clip")]
    [InlineData("[Shot 1] [Pan left] <Subject 1> waits.", "Bracketed camera commands")]
    [InlineData("[Shot 1] <Picture 1> waits.", "Never write <Picture N>")]
    [InlineData("[Shot 1] <Subject 5> waits.", "Unknown tag(s) <Subject 5>")]
    [InlineData("[Shot 1] <Subject 1> says: <d>Hello.</d>", "language tag")]
    [InlineData("[Shot 1] She says: <d>[English] Hello.</d>", "No speaker ID")]
    [InlineData("<Subject 1> waits.", "no [Shot 1]")]
    public void Format_breaks_are_named(string description, string expected)
    {
        var body = "detailed_description:\n" + description + "\n\noverall_soundscape:\nWind.";
        var complaint = H3RewriterBeatWriter.Validate(body, Ctx());
        Assert.NotNull(complaint);
        Assert.Contains(expected, complaint);
    }

    [Fact]
    public void Quoted_lines_in_the_beat_must_be_spoken()
    {
        var body = "detailed_description:\n[Shot 1] <Subject 1> opens the door.\n\noverall_soundscape:\nWind.";
        var complaint = H3RewriterBeatWriter.Validate(body, Ctx(beat: "<Subject 1> opens the door: \"You came back.\""));
        Assert.NotNull(complaint);
        Assert.Contains("quoted lines", complaint);
    }

    [Fact]
    public void The_assembled_clip_has_six_sections_the_plate_and_no_score()
    {
        var env = new StoryContinuity.Environment(true, "a narrow bookshop", "late evening", "warm lamplight");
        var defs = H3RewriterBeatWriter.BuildSubjectDefinitions(Cast, plateSubject: 3, platePicture: 3, env, null);
        var retention = H3RewriterBeatWriter.BuildRetentionAnalysis(Cast, plateSubject: 3, platePicture: 3);
        var writer = "detailed_description:\n[Shot 1] <Subject 1> waits.\n\noverall_soundscape:\nWind.\n\n" +
                     "non_diegetic_music:\nSoft piano.";
        var clip = H3RewriterBeatWriter.AssembleClip(writer, defs, retention, StoryContinuity.SceneSentence(env),
                                                     "<Subject 1> waits by the door.");

        var s = H3SpecPrompt.Parse(clip);
        foreach (var label in new[] { H3SpecPrompt.SubjectDefinitions, H3SpecPrompt.Summary, H3SpecPrompt.RetentionAnalysis,
                                      H3SpecPrompt.DetailedDescription, H3SpecPrompt.OverallSoundscape, H3SpecPrompt.NonDiegeticMusic })
            Assert.True(s[label].Length > 0, label);

        Assert.Contains("<Subject 3> is the location in <Picture 3>", s[H3SpecPrompt.SubjectDefinitions]);
        Assert.DoesNotContain("<Environment>:", s[H3SpecPrompt.SubjectDefinitions]);
        Assert.Contains("<Picture 3> → <Subject 3>: partially_preserved", s[H3SpecPrompt.RetentionAnalysis]);
        Assert.StartsWith("[reference generation] <Subject 1> waits by the door.", s[H3SpecPrompt.Summary]);
        Assert.StartsWith(H3RewriterBeatWriter.DefaultStyle, s[H3SpecPrompt.DetailedDescription]);
        Assert.Contains("Continuity of place and time:", s[H3SpecPrompt.DetailedDescription]);
        Assert.Equal("N/A", s[H3SpecPrompt.NonDiegeticMusic]);
    }

    [Fact]
    public void Without_a_plate_the_environment_line_stays()
    {
        var env = new StoryContinuity.Environment(false, "a harbour wall", "dawn", "grey overcast");
        var defs = H3RewriterBeatWriter.BuildSubjectDefinitions(Cast, plateSubject: 0, platePicture: 0, env, null);
        Assert.Contains("<Environment>:", defs);
        Assert.DoesNotContain("location in <Picture", defs);
        Assert.Contains("<Screen map>", defs);
        Assert.DoesNotContain("fights", defs);
    }
}

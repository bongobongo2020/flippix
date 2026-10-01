using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// The action rules layered on the 📐 spec build (2026-09-29), from what made fight clips work in practice: a
/// fixed screen map in the code-written section, mid-attack openings and a moving camera in every clip's rule
/// block, and <c>N/A</c> — never "no music" in words — for the score.
/// </summary>
public class H3SpecPromptActionRulesTests
{
    private static readonly H3SpecPrompt.CastMember[] Duo =
    {
        new(1, "man", "a black leather jacket"),
        new(2, "woman", "a red silk blouse"),
    };

    private static readonly H3SpecPrompt.CastMember[] Solo = { new(1, "man", "a black leather jacket") };

    private static readonly StoryContinuity.Environment Alley =
        new(false, "the alley behind the club", "night", "heavy rain, neon signs");

    [Theory]
    [InlineData("No music.")]
    [InlineData("no music")]
    [InlineData("None")]
    [InlineData("N/A")]
    [InlineData("n/a.")]
    [InlineData("Silence.")]
    [InlineData("No background music or score.")]
    [InlineData("There is no score.")]
    [InlineData("")]
    public void A_score_that_says_there_is_none_is_written_NA(string music) =>
        Assert.Equal("N/A", H3SpecPrompt.NormalizeMusic(music));

    [Theory]
    [InlineData("A low pulsing synth that builds with each blow.")]
    [InlineData("Taiko drums, no melody, rising in intensity.")]
    public void A_real_score_is_kept(string music) =>
        Assert.Equal(music, H3SpecPrompt.NormalizeMusic(music));

    [Fact]
    public void Assemble_writes_NA_for_a_negated_score()
    {
        var body = H3SpecPrompt.Assemble("<Subject 1>: x", "[reference generation] y", "z",
                                         "[Shot 1] w", "Rain.", "No music.");
        Assert.EndsWith("non_diegetic_music:\nN/A", body);
    }

    /// <summary>MiniMax I2V keeps the writer's reply as text; each segment's music field is fixed in place and
    /// the rest is left byte-identical.</summary>
    [Fact]
    public void Every_segments_music_field_is_normalized_in_place()
    {
        const string reply =
            "Ref2VA:\n\nsummary:\nA fight.\n\ndetailed_description:\n[0.0s-5.0s] They fight.\n\n" +
            "overall_soundscape:\nRain.\n\nnon_diegetic_music:\nNo music, only silence.";
        var fixedReply = H3SpecPrompt.NormalizeMusicSections(reply);
        Assert.EndsWith("non_diegetic_music:\nN/A", fixedReply);
        Assert.StartsWith(reply[..reply.IndexOf("non_diegetic_music:", StringComparison.Ordinal)], fixedReply);

        const string scored = "overall_soundscape:\nRain.\n\nnon_diegetic_music:\nLow strings, building.";
        Assert.Equal(scored, H3SpecPrompt.NormalizeMusicSections(scored));
    }

    /// <summary>The map is code-written, so every clip of a chain carries the same words.</summary>
    [Fact]
    public void A_two_hander_carries_one_fixed_screen_map()
    {
        var defs = H3SpecPrompt.BuildSubjectDefinitions(Duo, Alley, setting: null);
        Assert.Contains(H3SpecPrompt.ScreenMap, defs);
        Assert.Contains("<Subject 1> fights from screen-left", defs);
        Assert.Equal(defs, H3SpecPrompt.BuildSubjectDefinitions(Duo, Alley, setting: null));
    }

    [Fact]
    public void A_solo_clip_has_no_screen_map() =>
        Assert.DoesNotContain("<Screen map>", H3SpecPrompt.BuildSubjectDefinitions(Solo, Alley, setting: null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_clip_opens_mid_attack_with_a_moving_camera(bool lastClip)
    {
        var rules = H3SpecPrompt.RulesFor(2, 15, 3, setting: null, hasContinuityPlan: true, lastClip: lastClip);
        Assert.Contains("MID-ATTACK", rules);
        Assert.Contains("never static or locked-off", rules);
        Assert.Contains("No circling", rules);
    }

    [Fact]
    public void The_beat_sheet_no_longer_asks_for_a_standoff()
    {
        var system = H3SpecPrompt.DirectorBeatSheetSystem(2, continuity: false);
        Assert.DoesNotContain("the standoff and first contact", system);
        Assert.DoesNotContain("squared up", system);
        Assert.Contains("mid-attack", system);
    }

    [Theory]
    [InlineData(H3SpecPrompt.BuildTag)]
    [InlineData(H3SpecPrompt.FightDirectorBuildTag)]
    [InlineData(H3SpecPrompt.EarlierBuildTag)]
    public void Every_spec_build_is_still_recognised(string tag) => Assert.True(H3SpecPrompt.IsSpecBuild(tag));

    [Fact]
    public void The_action_rules_have_their_own_build_tag() =>
        Assert.NotEqual(H3SpecPrompt.FightDirectorBuildTag, H3SpecPrompt.BuildTag);
}

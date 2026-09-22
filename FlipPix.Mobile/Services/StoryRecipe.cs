using FlipPix.UI.Services;
using StoryBeat = FlipPix.UI.Services.StoryBeatSheet.StoryBeat;
using SceneEnv = FlipPix.UI.Services.StoryContinuity.Environment;

namespace FlipPix.Mobile.Services;

/// <summary>
/// ⚡ H3 Express for the phone, kept to what makes a film: a story, a cast, and clips.
///
/// <para>The chain is the desktop's, shared as source: <see cref="StoryBeatSheet"/> turns the story into
/// exactly N beats with a place, hour and light each; <see cref="StoryContinuity"/> holds that plan still
/// across clips; <see cref="ClipChainWriter"/> writes one clip per LLM call (a local model degenerates
/// past about four clips in one reply). Each clip is written in the six-field Ref2VA shape and rendered
/// by <see cref="VideoRecipe"/>, the graph the Video page already uses, with the same cast pictures on
/// every clip. That is how the people stay the same people from clip to clip.</para>
///
/// <para>What it leaves out on purpose: the desktop's wardrobe pass, character sheets and stack choice.
/// A missing cast photo is made once, from the story, by the Photo look.</para>
/// </summary>
public static class StoryRecipe
{
    public const int ClipSeconds = 10;
    public const int MaxCast = 3;

    public const string DescribeSystem =
        "You write one line of a casting sheet from a photograph. Say who or what it shows so a film " +
        "crew could recognise them: for a person, apparent age range, build, hair, and every visible " +
        "garment with its colour; for a place, what kind of place it is and its look. One sentence, at " +
        "most 45 words. No names, no guesses about identity, ethnicity or occupation, no preamble.";

    public const string CastPhotoSystem =
        "You write one prompt for a photorealistic image model: a portrait photograph of the story's " +
        "main character, standing in the story's main setting, facing the camera, full figure visible, " +
        "natural light. Describe apparent age, build, hair, face, and every garment with its colour, then " +
        "the setting behind them. 50 to 90 words, one paragraph, concrete and plain. Reply with the prompt " +
        "only: no title, no quotes, no names.";

    /// <summary>How the beat sheet is told to name the story's people: by their pictures.</summary>
    public static string CastBrief(IReadOnlyList<string> descriptions)
    {
        var lines = descriptions.Select((d, i) => $"PICTURE {i + 1} is {d.TrimEnd('.')}.");
        var tags = string.Join(", ", Enumerable.Range(1, descriptions.Count).Select(i => $"PICTURE {i}"));
        return $"The story has {descriptions.Count} reference picture(s). " + string.Join(" ", lines) +
               $" Call the pictured people {tags} and nothing else, never by the name the story gives them, " +
               "and keep that mapping identical in every beat. There is NO other picture: anyone else the " +
               "story needs is described in plain words every time (\"a young man in a grey coat\"), never " +
               "given a PICTURE number.";
    }

    /// <summary>
    /// One clip's user message: the Ref2VA request the Video page sends, plus the three lines of story
    /// that make this clip this clip (the beat before, its own, the beat after) and the continuity block.
    /// </summary>
    public static string ClipRequest(
        IReadOnlyList<string> descriptions, string setting, IReadOnlyList<StoryBeat> beats,
        IReadOnlyList<SceneEnv> plan, int index, string rejection)
    {
        var lines = new List<string> { $"You are given {descriptions.Count} reference picture(s), in order:" };
        for (var i = 0; i < descriptions.Count; i++) lines.Add($"  <Picture {i + 1}> — {descriptions[i]}");
        lines.Add("");
        lines.Add($"Write ONE segment. Target duration: {ClipSeconds} seconds.");
        lines.Add($"This segment is clip {index + 1} of {beats.Count} of one continuous story. <Subject N> is " +
                  "the person shown in <Picture N>; bind them that way in subject_definitions.");
        lines.Add(VideoRecipe.SoundRequest);

        if (setting.Length > 0)
        {
            lines.Add("");
            lines.Add("SETTING — the same story world in every clip:");
            lines.Add(setting);
        }

        var continuity = StoryContinuity.WriterBlock(EnvFor(plan, index),
            index > 0 ? EnvFor(plan, index - 1) : (SceneEnv?)null);
        if (continuity.Length > 0)
        {
            lines.Add("");
            lines.Add(continuity.Replace("[Shot 1]", "detailed_description"));
        }

        lines.Add("");
        lines.Add(index > 0
            ? $"THE CLIP BEFORE THIS ONE already showed this; do not show it again:\n{beats[index - 1].Text}"
            : "This is the FIRST clip: it opens the story, already in motion.");
        lines.Add("");
        lines.Add(index + 1 < beats.Count
            ? $"THE CLIP AFTER THIS ONE will show this; do not reach into it, end on the way there:\n{beats[index + 1].Text}"
            : "This is the LAST clip: the story's final moment lands inside it.");

        lines.Add("");
        lines.Add("Draft idea from the user (this clip's action; expand only this and fill all " +
                  $"{ClipSeconds} seconds with it):");
        lines.Add(Retag(beats[index].Text, descriptions.Count) + StoryBeatSheet.DescribePart(beats[index]));

        if (rejection.Length > 0)
        {
            lines.Add("");
            lines.Add("Your previous attempt at this clip was rejected: " + rejection);
        }
        return string.Join("\n", lines);
    }

    private static readonly System.Text.RegularExpressions.Regex PictureTag =
        // Upper case and not after "<": the Ref2VA body's own <Picture N> tags must survive untouched.
        new(@"(?<!<)\bPICTURE\s*(\d+)\b");

    /// <summary>
    /// The beat sheet's PICTURE N, rewritten for the renderer: a pictured person becomes the Ref2VA
    /// subject bound to that picture; a number with no picture behind it (the model invented a cast
    /// member) becomes plain words, because a dangling tag would reach the video model as text.
    /// </summary>
    public static string Retag(string text, int castCount) =>
        PictureTag.Replace(text, m => int.TryParse(m.Groups[1].Value, out var n) && n >= 1 && n <= castCount
            ? $"<Subject {n}>"
            : "another person");

    /// <summary>A beat as the person reading the strip should see it: "person 1", not a tag.</summary>
    public static string DisplayBeat(string text)
    {
        var s = PictureTag.Replace(text.Trim(), m => $"person {m.Groups[1].Value}");
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>Null when the clip can be rendered; otherwise the reason, phrased for the model.</summary>
    public static string? Validate(string body, SceneEnv env)
    {
        if (!body.Contains("detailed_description", StringComparison.OrdinalIgnoreCase))
            return "it had no detailed_description, the timeline the video is rendered from. Reply with the " +
                   "six fields, starting with Ref2VA:.";
        if (!body.Contains("subject_definitions", StringComparison.OrdinalIgnoreCase))
            return "it had no subject_definitions, so no picture was bound to anyone. Bind each person to " +
                   "<Subject N>, the subject shown in <Picture N>.";
        return StoryContinuity.Contradiction(body, env);
    }

    /// <summary>
    /// Writes the planned environment into the timeline in code, so every clip in one place says it in
    /// the same words. The writer was told it too; only this copy is identical from clip to clip.
    /// </summary>
    public static string StampScene(string body, SceneEnv env)
    {
        var sentence = StoryContinuity.SceneSentence(env);
        if (sentence.Length == 0) return body;
        const string label = "detailed_description:";
        var at = body.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return body;
        var after = at + label.Length;
        return body[..after] + "\n" + sentence + " " + body[after..].TrimStart();
    }

    public static SceneEnv EnvFor(IReadOnlyList<SceneEnv> plan, int index) =>
        plan.Count == 0 ? default : plan[Math.Clamp(index, 0, plan.Count - 1)];
}

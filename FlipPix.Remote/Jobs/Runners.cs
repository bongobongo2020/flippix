using System.Diagnostics;
using FlipPix.Remote.Contracts;
using FlipPix.Remote.Engine;
using FlipPix.UI.Services;

namespace FlipPix.Remote.Jobs;

/// <summary>Pictures: each tile is its own render of the chosen look, one after another.</summary>
public static class ImageRunner
{
    public static ImageShape ShapeOf(string? shape) => shape switch
    {
        "square" => ImageShape.Square,
        "landscape" => ImageShape.Landscape,
        _ => ImageShape.Portrait,
    };

    public static double Ratio(ImageShape shape) => shape switch
    {
        ImageShape.Portrait => 0.8,
        ImageShape.Landscape => 1.25,
        _ => 1.0,
    };

    public static async Task RunAsync(JobContext c)
    {
        var r = c.Job.Request;
        var look = ImageLook.All.First(l => l.Key == r.Look);
        var shape = ShapeOf(r.Shape);
        var todo = c.Job.Items.Where(i => i.State != ItemStates.Done).ToList();

        for (var k = 0; k < todo.Count; k++)
        {
            var item = todo[k];
            c.Set(j => j.Status = todo.Count == 1 ? "Developing" : $"Developing {k + 1} of {todo.Count}");
            c.SetItem(item, i => { i.State = ItemStates.Working; i.Status = "Sending to the server"; i.Progress = 0; });
            try
            {
                var graph = look.Build(r.Prompt!, shape, Workflows.RandomSeed());
                var outputs = await c.Engine.Comfy.RunAsync(graph, (v, max) =>
                {
                    c.Report(item, (double)v / max, $"Step {v} of {max}");
                    c.Set(j => j.Progress = j.Items.Average(i => i.State == ItemStates.Done ? 1 : i.Progress));
                }, c.Token);

                var image = outputs.FirstOrDefault(o => !o.IsVideo)
                    ?? throw new InvalidOperationException("The server finished but saved no picture.");
                c.SetItem(item, i =>
                {
                    i.Output = image;
                    i.State = ItemStates.Done;
                    i.Status = "Done";
                    i.Progress = 1;
                }, persist: true);
                c.Remember(item, new MadeRecord { Kind = JobKinds.Image, Prompt = r.Prompt, Look = r.Look, Shape = r.Shape });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                c.SetItem(item, i => { i.State = ItemStates.Failed; i.Status = JobManager.FirstLine(ex.Message); }, persist: true);
            }
        }

        Finish(c, "picture");
    }

    /// <summary>Done when everything came out; otherwise failed, saying how much did.</summary>
    internal static void Finish(JobContext c, string noun)
    {
        c.Set(j =>
        {
            var done = j.Items.Count(i => i.State == ItemStates.Done);
            j.Progress = 1;
            j.State = done == j.Items.Count && done > 0 ? JobStates.Done : JobStates.Failed;
            var took = j.Started is { } s ? Clock(DateTimeOffset.Now - s) : "";
            j.Status = j.State == JobStates.Done
                ? $"Made in {took}"
                : done == 0
                    ? j.Items.FirstOrDefault(i => i.State == ItemStates.Failed)?.Status ?? "Nothing came out"
                    : $"{done} of {j.Items.Count} {noun}s made";
        }, persist: true);
    }

    internal static string Clock(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : $"{Math.Max(1, t.Seconds)} s";
}

/// <summary>
/// MiniMax I2V from reference pictures: the scene is written from the pictures by the desktop's
/// vision LLM (or wrapped as written without one), then rendered in one pass.
/// </summary>
public static class VideoRunner
{
    public static async Task RunAsync(JobContext c)
    {
        var r = c.Job.Request;
        var item = c.Job.Items[0];
        var ct = c.Token;

        c.Set(j => j.Status = "Getting the photos ready");
        var pictures = new List<Picture>();
        foreach (var p in r.Pictures) pictures.Add(await c.Engine.Uploads.ResolveAsync(p, ct));

        // 1. The scene. A retake arrives with it already written.
        if (c.Job.Script == null)
        {
            c.Set(j => j.Status = "Writing the scene from your photos");
            c.SetItem(item, i => { i.State = ItemStates.Working; i.Status = "Writing the scene"; });
            var script = await WriteScriptAsync(c.Engine, pictures, r.Seconds, r.Idea ?? "", ct);
            c.Set(j => j.Script = script, persist: true);
        }

        // 2. The pictures, uploaded to ComfyUI once each.
        c.Set(j => { j.Status = "Sending photos to the server"; j.Progress = 0.02; });
        var names = new List<string>();
        foreach (var p in pictures) names.Add(await c.Engine.Uploads.EnsureOnServerAsync(c.Engine.Comfy, p, ct));

        // 3. The render: a draft, the finish at twice the size, then audio and encode. Each arrives as
        // its own progress run, so a step count that restarts or changes size is the next stage.
        c.SetItem(item, i => { i.State = ItemStates.Working; i.Status = "Waiting for the server"; i.Progress = 0; });
        c.Set(j => j.Status = "Waiting for the server");
        var graph = VideoRecipe.Build(names, c.Job.Script!, r.Seconds, c.Job.Aspect!, Workflows.RandomSeed(),
            $"FlipPixMobile/video_{DateTime.Now:yyyyMMdd_HHmmss}");
        var outputs = await c.Engine.Comfy.RunAsync(graph, Staged((p, status) =>
        {
            if (c.Report(item, p, status)) c.Set(j => { j.Progress = p; j.Status = status; });
        }), ct);

        var video = outputs.FirstOrDefault(o => o.IsVideo)
            ?? throw new InvalidOperationException("The server finished but saved no video.");
        c.SetItem(item, i =>
        {
            i.Output = video;
            i.State = ItemStates.Done;
            i.Status = "Done";
            i.Progress = 1;
        }, persist: true);
        c.Remember(item, new MadeRecord { Kind = JobKinds.Video, Idea = r.Idea, Prompt = c.Job.Script });
        ImageRunner.Finish(c, "video");
    }

    /// <summary>Draft, finish, final touches: three runs of sampler steps read as one bar.</summary>
    internal static Action<int, int> Staged(Action<double, string> report)
    {
        var stage = 0;
        var (lastValue, lastMax) = (0, 0);
        return (v, max) =>
        {
            if (lastMax != 0 && (v < lastValue || max != lastMax)) stage++;
            (lastValue, lastMax) = (v, max);
            var within = (double)v / max;
            var (p, s) = stage switch
            {
                0 => (0.45 * within, $"Drafting, step {v} of {max}"),
                1 => (0.45 + 0.45 * within, $"Finishing, step {v} of {max}"),
                _ => (0.9 + 0.1 * within, "Adding the final touches"),
            };
            report(Math.Min(1, p), s);
        };
    }

    private static async Task<string> WriteScriptAsync(RemoteEngine engine, IReadOnlyList<Picture> pictures,
        int seconds, string idea, CancellationToken ct)
    {
        if (!engine.Llm.Target.IsSet) return VideoRecipe.ScriptWithoutLlm(pictures.Count, seconds, idea);
        var reply = await engine.Llm.ChatAsync(VideoRecipe.SystemPrompt(),
            VideoRecipe.Request(pictures.Count, seconds, idea),
            pictures.Select(p => p.Jpeg).ToList(), maxTokens: 3000, temperature: 0.7, ct: ct);
        var script = VideoRecipe.CleanScript(reply);
        if (!script.Contains("detailed_description", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The LLM didn't write a usable scene. Is the desktop's LLM a vision model?");
        return script;
    }
}

/// <summary>
/// ⚡ H3 Express, simplified: a story and an optional cast become N shots of 10 s. The desktop's own
/// story chain writes it (beat sheet, continuity plan, one call per clip); each clip is rendered by
/// the Video job's graph with the same cast pictures, which is what keeps the people the same people.
/// </summary>
public static class StoryRunner
{
    public static async Task RunAsync(JobContext c)
    {
        var ct = c.Token;
        // A retry films what was written; a story that never got as far as its shots starts over.
        var written = c.Job.Items.Count > 0 && c.Job.Cast.Count > 0 && c.Job.Items.All(i => !string.IsNullOrEmpty(i.Text));
        if (!written)
        {
            c.Set(j => { j.Items.Clear(); j.Cast.Clear(); j.CastLines.Clear(); j.Progress = 0; });
            await CastAsync(c, ct);
            await WriteAsync(c, ct);
        }
        await FilmAsync(c, ct);
    }

    // ── 1. The cast ─────────────────────────────────────────────────────────────────────────────

    private static async Task CastAsync(JobContext c, CancellationToken ct)
    {
        var r = c.Job.Request;
        var llm = c.Engine.Llm;
        var lines = new List<string>();
        var cast = new List<string>();

        if (r.Pictures.Count == 0)
        {
            // No photos: write a portrait of the lead in the story's setting and have the Photo look take
            // it. It becomes picture 1, and every clip is cast from it.
            c.Set(j => j.Status = "Writing a portrait of your lead");
            var prompt = (await llm.ChatAsync(StoryRecipe.CastPhotoSystem, r.Story!, Array.Empty<byte[]>(), 300, 0.7, ct))
                .Trim().Trim('"');
            c.Set(j => j.Status = "Photographing your lead");
            var photo = ImageLook.All.First(l => l.Key == "photo");
            var outputs = await c.Engine.Comfy.RunAsync(photo.Build(prompt, ImageShape.Landscape, Workflows.RandomSeed()),
                (v, max) => c.Set(j => j.Status = $"Photographing your lead, step {v} of {max}"), ct);
            var image = outputs.FirstOrDefault(o => !o.IsVideo)
                ?? throw new InvalidOperationException("The portrait came back empty.");
            var saved = await c.Engine.Uploads.SaveBytesAsync(await c.Engine.Comfy.DownloadAsync(image, ct), ct);
            cast.Add("upload:" + saved.Id);
            lines.Add(prompt);
        }
        else
        {
            // One line per photo, read once by the vision model and then handed to every call as text:
            // cheaper than attaching the pictures to every clip, and identical in every clip by design.
            cast.AddRange(r.Pictures);
            for (var i = 0; i < r.Pictures.Count; i++)
            {
                c.Set(j => j.Status = $"Looking at photo {i + 1} of {r.Pictures.Count}");
                var picture = await c.Engine.Uploads.ResolveAsync(r.Pictures[i], ct);
                var line = await llm.ChatAsync(StoryRecipe.DescribeSystem, $"Describe <Picture {i + 1}>.",
                    new[] { picture.Jpeg }, 200, 0.4, ct);
                lines.Add(line.Trim());
            }
        }

        var lead = await c.Engine.Uploads.ResolveAsync(cast[0], ct);
        c.Set(j =>
        {
            j.Cast = cast;
            j.CastLines = lines;
            j.PosterRef = cast[0];
            j.Aspect = VideoRecipe.AspectFor(lead.Width, lead.Height);
            j.Ratio = VideoRecipe.AspectRatioOf(j.Aspect);
        }, persist: true);
    }

    // ── 2. The shots ────────────────────────────────────────────────────────────────────────────

    private static async Task WriteAsync(JobContext c, CancellationToken ct)
    {
        var r = c.Job.Request;
        var lm = new LMStudioService(c.Engine.Llm);
        var model = c.Engine.Llm.Target.Model;
        var descriptions = c.Job.CastLines;
        c.Set(j => j.Status = $"Dividing the story into {r.Clips} shots");

        var (setting, beats) = await StoryBeatSheet.WriteAsync(
            lm, model, r.Story!, r.Clips, StoryRecipe.ClipSeconds,
            StoryRecipe.CastBrief(descriptions), perBeatCast: false, imagePath: null,
            log: m => Debug.WriteLine("[FlipPix Remote] " + m), token: ct, continuity: true);
        if (beats.Count == 0) throw new InvalidOperationException("The story couldn't be divided into shots.");

        var plan = StoryContinuity.Plan(beats.Select(b => b.Env).ToList(), setting, beats.Select(b => b.Text).ToList());
        var system = VideoRecipe.SystemPrompt();
        var castCount = descriptions.Count;

        await ClipChainWriter.WriteAsync(
            lm, model, system, beats.Count,
            buildRequest: (i, why) => StoryRecipe.ClipRequest(descriptions, setting, beats, plan, i, why),
            normalize: VideoRecipe.CleanScript,
            validate: (i, body) => StoryRecipe.Validate(body, StoryRecipe.EnvFor(plan, i)),
            onProgress: (n, total) => c.Set(j =>
            {
                j.Status = $"Writing shot {n} of {total}";
                j.Progress = 0.1 * (n - 1) / total;
            }),
            log: m => Debug.WriteLine("[FlipPix Remote] " + m),
            token: ct,
            // Each clip lands on the phone's strip the moment it is written.
            onWritten: (i, body) => c.Set(j => j.Items.Add(new RemoteJobItem
            {
                Index = j.Items.Count,
                MediaKind = MediaKinds.Video,
                Label = StoryRecipe.DisplayBeat(beats[Math.Min(i, beats.Count - 1)].Text),
                Text = StoryRecipe.Retag(StoryRecipe.StampScene(body, StoryRecipe.EnvFor(plan, i)), castCount),
            }), persist: true));

        if (c.Job.Items.Count == 0) throw new InvalidOperationException("No shot came back usable from the writer.");
    }

    // ── 3. The film ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the unfinished clips one at a time. One clip failing is not the film failing: it is
    /// marked, the next one starts, and it can be filmed again afterwards from the same scene.
    /// </summary>
    private static async Task FilmAsync(JobContext c, CancellationToken ct)
    {
        c.Set(j => j.Status = "Sending the cast to the server");
        var names = new List<string>();
        foreach (var reference in c.Job.Cast)
        {
            var picture = await c.Engine.Uploads.ResolveAsync(reference, ct);
            names.Add(await c.Engine.Uploads.EnsureOnServerAsync(c.Engine.Comfy, picture, ct));
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var todo = c.Job.Items.Where(i => i.State != ItemStates.Done).ToList();
        var total = c.Job.Items.Count;
        for (var k = 0; k < todo.Count; k++)
        {
            var clip = todo[k];
            var number = clip.Index + 1;
            var before = k;
            c.Set(j => j.Status = $"Filming shot {number} of {total}");
            c.SetItem(clip, i => { i.State = ItemStates.Working; i.Status = "Waiting for the server"; i.Progress = 0; });
            try
            {
                var graph = VideoRecipe.Build(names, clip.Text!, StoryRecipe.ClipSeconds, c.Job.Aspect!,
                    Workflows.RandomSeed(), $"FlipPixMobile/story_{stamp}_clip{number:00}");
                var outputs = await c.Engine.Comfy.RunAsync(graph, VideoRunner.Staged((p, status) =>
                {
                    if (c.Report(clip, p, status)) c.Set(j => j.Progress = 0.1 + 0.9 * (before + p) / todo.Count);
                }), ct);
                var video = outputs.FirstOrDefault(o => o.IsVideo)
                    ?? throw new InvalidOperationException("The server finished but saved no video.");
                c.SetItem(clip, i =>
                {
                    i.Output = video;
                    i.State = ItemStates.Done;
                    i.Status = "Done";
                    i.Progress = 1;
                }, persist: true);
                c.Remember(clip, new MadeRecord { Kind = JobKinds.Story, Idea = clip.Label, Prompt = clip.Text });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                c.SetItem(clip, i => { i.State = ItemStates.Failed; i.Status = JobManager.FirstLine(ex.Message); }, persist: true);
            }
        }

        ImageRunner.Finish(c, "shot");
    }

    public static string TitleOf(string story)
    {
        var first = story.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? story;
        var sentence = first.Split(new[] { ". ", "! ", "? " }, 2, StringSplitOptions.None)[0].TrimEnd('.');
        return sentence.Length <= 60 ? sentence : sentence[..57].TrimEnd() + "…";
    }
}

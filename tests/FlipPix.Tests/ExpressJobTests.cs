using FlipPix.UI.Models;

namespace FlipPix.Tests;

/// <summary>
/// The ⚡ H3 Express job snapshot — see <see cref="ExpressJob"/>.
///
/// <para>What is worth testing here without a GPU is the part a queued job's correctness rests on:
/// that a snapshot really carries <b>every</b> dial. The whole point of the queue is that job ② renders
/// on its own settings rather than on job ①'s, and the way that breaks is silently — a property added to
/// the tab and not to <c>CloneSettings</c>/<c>TakeSettingsFrom</c>, so the second folder quietly comes out
/// on the first one's stack. <see cref="EverySettingIsCopied"/> walks the type by reflection so a new
/// property fails the test rather than a film.</para>
/// </summary>
public class ExpressJobTests
{
    /// <summary>Settings that are not settings: the identity, the story list, the run state and the
    /// thumbnails, none of which a copy should carry.</summary>
    private static readonly HashSet<string> NotSettings = new()
    {
        nameof(ExpressJob.Id), nameof(ExpressJob.Stories), nameof(ExpressJob.State),
        nameof(ExpressJob.Position), nameof(ExpressJob.Cast1Preview), nameof(ExpressJob.Cast2Preview),
    };

    /// <summary>One job with nothing left at its default, so a field that is not copied shows up.</summary>
    private static ExpressJob Filled() => new()
    {
        Folder = @"D:\stories\noir",
        Cast1Photo = @"D:\faces\ada.png",
        Cast1Sex = ExpressCastMember.Female,
        Cast1Outfit = "a charcoal wool coat",
        Cast1OutfitSource = @"D:\faces\ada.png",
        Cast2Photo = @"D:\faces\ray.png",
        Cast2Sex = ExpressCastMember.Male,
        Cast2Outfit = "a grey flannel suit",
        Cast2OutfitSource = @"D:\faces\ray.png",
        CastOwnClothes = true,
        CastPhotoEngine = "ideogram",
        UseTaoMate = true,
        UseSingularity = false,
        SingularityErSde = true,
        DiffusionModel = "h3-minimax/taomate.safetensors",
        Steps = 13,
        Lora = "H3/some-lora.safetensors",
        LoraStrength = 0.65,
        AspectRatio = "21:9 (Ultrawide)",
        Megapixels = 1.5,
        PreviewMegapixels = 0.3,
        UpscaleSteps = 5,
        UseRife = false,
        StoryDurationSeconds = 120,
        ClipLengthSeconds = 7,
        ResearchPrompts = false,
        SpecPrompts = true,
        ReuseSavedPrompts = false,
        VisualStyle = "Live action — 35mm cinematic",
    };

    /// <summary>
    /// Every public settable property that is a setting survives both routes a job's settings travel:
    /// CloneSettings (➕ opens on a copy) and TakeSettingsFrom (💾 writes an edit back).
    /// </summary>
    [Fact]
    public void EverySettingIsCopied()
    {
        var source = Filled();
        var clone = source.CloneSettings();
        var written = new ExpressJob();
        written.TakeSettingsFrom(source);

        var missed = new List<string>();
        foreach (var p in typeof(ExpressJob).GetProperties())
        {
            if (!p.CanWrite || NotSettings.Contains(p.Name)) continue;
            var want = p.GetValue(source);
            if (!Equals(p.GetValue(clone), want)) missed.Add($"CloneSettings dropped {p.Name}");
            if (!Equals(p.GetValue(written), want)) missed.Add($"TakeSettingsFrom dropped {p.Name}");
        }

        Assert.True(missed.Count == 0,
            "A job's settings must all survive a copy, or a queued job renders on the previous job's " +
            "dials:\n  " + string.Join("\n  ", missed));
    }

    /// <summary>Every property in <see cref="Filled"/> is actually set away from its default — otherwise
    /// the test above would pass on a property it never really exercised.</summary>
    [Fact]
    public void TheFixtureLeavesNothingAtItsDefault()
    {
        var filled = Filled();
        var fresh = new ExpressJob();
        var same = typeof(ExpressJob).GetProperties()
            .Where(p => p.CanWrite && !NotSettings.Contains(p.Name))
            .Where(p => Equals(p.GetValue(filled), p.GetValue(fresh)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(same.Count == 0,
            "Filled() must move every setting off its default so EverySettingIsCopied really checks it: "
            + string.Join(", ", same));
    }

    /// <summary>A copy carries the settings and not the work: ➕ on a running job must not inherit its
    /// stories or its "now rendering" state.</summary>
    [Fact]
    public void CloneSettingsLeavesTheStoriesAndTheStateBehind()
    {
        var source = Filled();
        source.State = ExpressJobState.Running;
        source.Stories.Add(new BatchStory(@"D:\stories\noir\reel1.txt"));

        var clone = source.CloneSettings();

        Assert.Empty(clone.Stories);
        Assert.Equal(ExpressJobState.Queued, clone.State);
        Assert.NotEqual(source.Id, clone.Id);
    }

    /// <summary>A full clone shares the story rows, so a job re-queued from another one reports on the
    /// same films rather than on copies that will never be rendered.</summary>
    [Fact]
    public void CloneCarriesTheStoryRowsThemselves()
    {
        var source = Filled();
        var row = new BatchStory(@"D:\stories\noir\reel1.txt");
        source.Stories.Add(row);

        var clone = source.Clone();

        Assert.Same(row, Assert.Single(clone.Stories));
    }

    /// <summary>What the row says about a job, since that is all the queue card shows of it.</summary>
    [Fact]
    public void TheRowNamesTheFolderAndWhatItRendersOn()
    {
        var job = Filled();
        Assert.Equal("noir", job.Title);
        Assert.Equal("🍥 TaoMate", job.StackLabel);
        Assert.Contains("13 steps", job.RenderLine);
        Assert.Equal(2, job.CastCount);
        Assert.Contains("their own clothes", job.CastLine);

        job.Cast1Photo = string.Empty;
        job.Cast2Photo = string.Empty;
        Assert.Equal("cast from each story", job.CastLine);
    }

    /// <summary>The Singularity sub-option is part of the stack's name, because two queued jobs that differ
    /// only by it are two different renders.</summary>
    [Fact]
    public void TheStackLabelNamesTheSingularitySampler()
    {
        var job = new ExpressJob { UseTaoMate = false, UseSingularity = true, SingularityErSde = false };
        Assert.Equal("✴️ Singularity", job.StackLabel);
        job.SingularityErSde = true;
        Assert.Equal("✴️ Singularity · er_sde", job.StackLabel);
        job.UseSingularity = false;
        Assert.Equal("🌹 H3 Eros", job.StackLabel);
    }

    /// <summary>Re-queueing a finished job puts its stories back to waiting — otherwise the run would walk
    /// a list with nothing on it to do.</summary>
    [Fact]
    public void ResetStoriesPutsThemAllBackToWaiting()
    {
        var job = Filled();
        var done = new BatchStory(@"D:\stories\noir\reel1.txt") { State = BatchStoryState.Done };
        var failed = new BatchStory(@"D:\stories\noir\reel2.txt") { State = BatchStoryState.Failed };
        job.Stories.Add(done);
        job.Stories.Add(failed);

        job.ResetStories();

        Assert.All(job.Stories, s => Assert.True(s.IsWaiting));
    }

    /// <summary>The ordinal is the job's place in the pipeline, and stays readable past the circled digits.</summary>
    [Fact]
    public void TheOrdinalIsTheJobsPlaceInTheQueue()
    {
        Assert.Equal("①", new ExpressJob { Position = 1 }.Ordinal);
        Assert.Equal("⑳", new ExpressJob { Position = 20 }.Ordinal);
        Assert.Equal("21", new ExpressJob { Position = 21 }.Ordinal);
    }
}

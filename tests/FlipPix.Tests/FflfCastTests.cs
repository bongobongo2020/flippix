using FlipPix.UI.Services;
using Xunit;

namespace FlipPix.Tests;

public class FflfCastTests
{
    private const string TwoPeople = """
        PEOPLE:
        A | left | woman, about thirty, slim, light skin, red chin-length bob, soft smile
        B | right | man, forties, broad, dark skin, short grey beard, serious
        WARDROBE:
        A | green knee-length raincoat over a white tee, blue jeans
        - **B** | navy wool suit, white shirt, no tie.
        POSE: A stands facing B; B leans on the counter.
        PLACE: a small café at dusk, warm tungsten light.
        CAMERA: waist-up two-shot, eye level, photo-real.
        """;

    [Fact]
    public void Parse_ReadsEveryPersonWithTheirOutfit()
    {
        var cast = FflfCast.Parse(TwoPeople);

        Assert.Equal(2, cast.Count);
        Assert.Equal(("A", "left"), (cast[0].Letter, cast[0].Position));
        Assert.Contains("red chin-length bob", cast[0].Look);
        Assert.Equal("green knee-length raincoat over a white tee, blue jeans", cast[0].Outfit);
        Assert.Equal(("B", "right"), (cast[1].Letter, cast[1].Position));
        Assert.Equal("navy wool suit, white shirt, no tie", cast[1].Outfit);
        Assert.Equal("A (the person on the left)", cast[0].Label);
    }

    [Fact]
    public void Parse_NobodyAndProseGiveNoCast()
    {
        Assert.Empty(FflfCast.Parse("PEOPLE: none\nPLACE: an empty beach."));
        Assert.Empty(FflfCast.Parse(null));
        // A letter line outside PEOPLE / WARDROBE is not a person.
        Assert.Empty(FflfCast.Parse("POSE:\nA | stands still"));
    }

    [Fact]
    public void SheetPrompt_WithTwoPeopleNamesTheOneToDraw()
    {
        var cast = FflfCast.Parse(TwoPeople);
        var prompt = FflfSheetGraph.Prompt(cast[1], cast.Count);

        Assert.Contains("only the person on the right of the photo", prompt);
        Assert.Contains("navy wool suit", prompt);
        Assert.Contains("Leave out the other person", prompt);
        Assert.Contains("head-and-shoulders close-up of the face", prompt);
        Assert.DoesNotContain("raincoat", prompt);
        Assert.DoesNotContain("<", prompt);
    }

    [Fact]
    public void SheetPrompt_WithOnePersonNamesNobodyElse()
    {
        var prompt = FflfSheetGraph.Prompt(new FflfCastMember("A", "centre", "a woman", "a red dress"), 1);

        Assert.Contains("of the person in the photo (a woman)", prompt);
        Assert.DoesNotContain("Leave out", prompt);
    }

    [Theory]
    [InlineData("klein", null)]
    [InlineData("qwen21", "workflow/image/qwen-edit/image_qwen_image_2_1_image_edit-changephoto.json")]
    [InlineData("fibo2", "workflow/image/fibo2API.json")]
    [InlineData("kleintrue", null)]
    [InlineData("qwen2511", "workflow/image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json")]
    [InlineData("firered", "workflow/firered-image-edit-1.1API.json")]
    public void SheetGraph_BuildsEveryEngineAtTheSheetSize(string engine, string? file)
    {
        Assert.Equal(file, FflfSheetGraph.WorkflowFile(engine));
        string? json = null;
        if (file != null)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "FlipPix.sln"))) dir = dir.Parent;
            json = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, file));
        }

        var graph = FflfSheetGraph.Build(engine, json, "opening.png", "make a sheet", 42, "video/fflf/sheets/x_A");
        var text = graph.ToJsonString();

        Assert.Contains("\"opening.png\"", text);
        Assert.Contains("make a sheet", text);
        Assert.Contains("video/fflf/sheets/x_A", text);
        Assert.Contains($"{FflfSheetGraph.Width}", text);
        Assert.Contains($"{FflfSheetGraph.Height}", text);
        Assert.NotNull(graph[FflfSheetGraph.SaveNode(engine)]);
    }

    [Fact]
    public void SheetGraph_Qwen21_SendsThePhotoAtTheTestedReferenceSize()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "FlipPix.sln"))) dir = dir.Parent;
        var json = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, FflfSheetGraph.WorkflowFile("qwen21")!));

        var graph = FflfSheetGraph.Build("qwen21", json, "opening.png", "make a sheet", 42, "x");
        Assert.Equal(2048, graph["459:474"]!["inputs"]!["resolution"]!.GetValue<int>());

        var custom = FflfSheetGraph.Build("qwen21", json, "opening.png", "make a sheet", 42, "x", referenceResolution: 1536);
        Assert.Equal(1536, custom["459:474"]!["inputs"]!["resolution"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("qwen2511", "115:3")]
    [InlineData("firered", "130")]
    public void SheetGraph_QwenEditPlusEngines_SampleOnAnEmptySheetSizedLatent(string engine, string sampler)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "FlipPix.sln"))) dir = dir.Parent;
        var json = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, FflfSheetGraph.WorkflowFile(engine)!));

        var graph = FflfSheetGraph.Build(engine, json, "opening.png", "make a sheet", 42, "x");
        var latentId = graph[sampler]!["inputs"]!["latent_image"]![0]!.GetValue<string>();
        var latent = graph[latentId]!["inputs"]!;
        Assert.Equal("EmptySD3LatentImage", graph[latentId]!["class_type"]!.GetValue<string>());
        Assert.Equal(FflfSheetGraph.Width, latent["width"]!.GetValue<int>());
        Assert.Equal(FflfSheetGraph.Height, latent["height"]!.GetValue<int>());
    }

    [Fact]
    public void ComposePrompt_NumbersTheSheetsAfterTheOtherPictures()
    {
        var sheets = new[] { "A (the person on the left)", "B (the person on the right)" };

        var first = FflfGraph.ComposePrompt("A waves.", null, hasIdentityImage: false, isFirstBeat: true, sheets);
        Assert.Contains("<Picture 2> is the character sheet of A (the person on the left)", first);
        Assert.Contains("<Picture 3> is the character sheet of B (the person on the right)", first);

        var later = FflfGraph.ComposePrompt("B nods.", null, hasIdentityImage: true, isFirstBeat: false, sheets);
        Assert.Contains("<Picture 2> shows how the characters look", later);
        Assert.Contains("<Picture 3> is the character sheet of A", later);
        Assert.Contains("<Picture 4> is the character sheet of B", later);
    }
}

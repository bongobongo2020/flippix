using System.Text.Json.Nodes;

namespace FlipPix.Mobile.Services;

public enum ImageShape { Portrait, Square, Landscape }

/// <summary>
/// One way of making a picture, named for what it looks like rather than which model draws it.
/// Each look runs a desktop graph as authored and writes only prompt, canvas and seed — the same
/// node maps the desktop Image Generator uses, so a fix there is the reference for a fix here.
/// </summary>
public sealed class ImageLook
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Blurb { get; init; }
    public required Func<string, ImageShape, long, JsonObject> Build { get; init; }

    public static IReadOnlyList<ImageLook> All { get; } = new[]
    {
        new ImageLook
        {
            Key = "photo", Name = "Photo",
            Blurb = "Real camera look. Fast, upscaled 2×.",
            Build = BuildKrea2,
        },
        new ImageLook
        {
            Key = "dream", Name = "Dream",
            Blurb = "Rewrites a short idea into a rich scene first.",
            Build = BuildQwen21,
        },
        new ImageLook
        {
            Key = "portrait", Name = "Detail",
            Blurb = "Slow and careful. Skin, fabric, texture.",
            Build = BuildZimageBase,
        },
    };

    // krea2RealismV1: 6 prompt, 10 latent, 27 sampler seed, 28 RTX 2× → 23 save.
    // SaveImageKJ does not register in /history, so 23 becomes a plain SaveImage off 28 and the
    // PreviewImage 5 is dropped, exactly as the desktop does.
    private static JsonObject BuildKrea2(string prompt, ImageShape shape, long seed)
    {
        var g = Workflows.Load("krea2.json");
        var (w, h) = shape switch
        {
            ImageShape.Portrait => (1024, 1280),
            ImageShape.Landscape => (1280, 1024),
            _ => (1024, 1024),
        };
        Workflows.Set(g, "6", "text", prompt);
        Workflows.Set(g, "10", "width", w);
        Workflows.Set(g, "10", "height", h);
        Workflows.Set(g, "27", "seed", seed);
        g["23"] = new JsonObject
        {
            ["class_type"] = "SaveImage",
            ["inputs"] = new JsonObject
            {
                ["filename_prefix"] = "FlipPixMobile/Photo",
                ["images"] = new JsonArray("28", 0),
            },
        };
        g.Remove("5");
        return g;
    }

    // qwen21-prompt-enhancer: the prompt goes into 468 ONLY — writing it anywhere downstream cuts
    // the enhancer out. 473 turns the rewrite on; 459:456 canvas; 459:458 seed; 461 save.
    private static JsonObject BuildQwen21(string prompt, ImageShape shape, long seed)
    {
        var g = Workflows.Load("qwen21.json");
        var (w, h) = shape switch
        {
            ImageShape.Portrait => (1088, 1600),
            ImageShape.Landscape => (1600, 1088),
            _ => (1600, 1600),
        };
        Workflows.Set(g, "468", "value", prompt);
        Workflows.Set(g, "473", "value", true);
        Workflows.Set(g, "459:456", "width", w);
        Workflows.Set(g, "459:456", "height", h);
        Workflows.Set(g, "459:458", "seed", seed);
        Workflows.Set(g, "461", "filename_prefix", "FlipPixMobile/Dream");
        return g;
    }

    // z-image-base: 76:67 prompt, 76:68 SD3 latent, 76:69 KSampler seed, 9 save.
    private static JsonObject BuildZimageBase(string prompt, ImageShape shape, long seed)
    {
        var g = Workflows.Load("zimage-base.json");
        var (w, h) = shape switch
        {
            ImageShape.Portrait => (1024, 1280),
            ImageShape.Landscape => (1280, 1024),
            _ => (1152, 1152),
        };
        Workflows.Set(g, "76:67", "text", prompt);
        Workflows.Set(g, "76:68", "width", w);
        Workflows.Set(g, "76:68", "height", h);
        Workflows.Set(g, "76:69", "seed", seed);
        Workflows.Set(g, "9", "filename_prefix", "FlipPixMobile/Detail");
        return g;
    }
}

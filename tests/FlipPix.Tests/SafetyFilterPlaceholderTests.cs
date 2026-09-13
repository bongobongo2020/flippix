using FlipPix.UI.Services;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// <see cref="SafetyFilterPlaceholder"/> — telling Ideogram 4's "Image blocked by safety filter" grey card from a
/// real cast portrait. The synthetic images are shaped on the measured files: the placeholder is a flat grey of
/// ~112 with one pale line of text, a portrait is a person on a light grey backdrop.
/// </summary>
public class SafetyFilterPlaceholderTests
{
    private const int W = 136;
    private const int H = 200;

    private static byte[] Fill(byte r, byte g, byte b)
    {
        var pixels = new byte[W * 4 * H];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static void Rect(byte[] pixels, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * W + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
            }
    }

    private static bool Check(byte[] pixels) => SafetyFilterPlaceholder.LooksLikePlaceholder(pixels, W, H, W * 4);

    [Fact]
    public void The_grey_card_with_its_line_of_text_is_the_placeholder()
    {
        var card = Fill(111, 111, 110);
        Rect(card, 12, 96, 124, 104, 215, 215, 212);   // "Image blocked by safety filter", as a pale band

        Assert.True(Check(card));
    }

    [Fact]
    public void A_person_on_a_light_grey_backdrop_is_not()
    {
        var portrait = Fill(200, 200, 198);
        Rect(portrait, 48, 20, 88, 190, 60, 45, 40);      // dark clothing
        Rect(portrait, 58, 20, 78, 45, 205, 160, 135);    // face and skin

        Assert.False(Check(portrait));
    }

    [Fact]
    public void A_flat_black_or_white_frame_is_not_the_placeholder()
    {
        Assert.False(Check(Fill(0, 0, 0)));
        Assert.False(Check(Fill(255, 255, 255)));
    }

    [Fact]
    public void A_flat_coloured_frame_is_not_the_placeholder()
    {
        Assert.False(Check(Fill(40, 120, 150)));
    }

    [Fact]
    public void Only_Ideogram_photos_are_checked_and_they_fall_back_to_Krea2_Spicy()
    {
        Assert.True(CastPhotoWorkflows.MayRenderSafetyPlaceholder("ideogram"));
        Assert.False(CastPhotoWorkflows.MayRenderSafetyPlaceholder("krea2spicy"));
        Assert.Equal("krea2spicy", CastPhotoWorkflows.SafetyFallbackEngine);
    }
}

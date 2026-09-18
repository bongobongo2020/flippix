using System;
using System.Collections.Generic;
using System.Linq;

namespace FlipPix.UI.Linux.ViewModels.Video
{
    /// <summary>
    /// One entry in the visual-style dropdown the MiniMax H3 tabs offer.
    ///
    /// <see cref="Clause"/> is not a hint — it is the literal wording the prompt writer is told to open
    /// <c>[Shot 1]</c> with, because that opening phrase is the only place H3 reads the medium from.
    /// Anything paraphrased there comes back as a different-looking video.
    /// </summary>
    public sealed class H3VisualStyle
    {
        internal H3VisualStyle(string name, string clause)
        {
            Name = name;
            Clause = clause;
        }

        /// <summary>Dropdown label. Grouped by a leading family word so a long list stays scannable.</summary>
        public string Name { get; }

        /// <summary>The style sentence, copied verbatim into <c>[Shot 1]</c>. Empty on <see cref="H3VisualStyles.Auto"/>.</summary>
        public string Clause { get; }

        public bool IsAuto => Clause.Length == 0;

        public override string ToString() => Name;
    }

    /// <summary>
    /// The style vocabulary shared by the H3 tabs.
    ///
    /// It exists because the writer was picking the same look almost every time: the system prompt's first
    /// worked example of a style was "Anime cinematic in a high-production gacha style", and a small local
    /// model anchors hard on the first example it is shown. The examples in the prompt file have been
    /// widened, and this list is the explicit override for when the choice should not be the model's at all.
    /// </summary>
    internal static class H3VisualStyles
    {
        /// <summary>Leaves the medium to the writer — the behaviour the tabs had before the dropdown.</summary>
        internal static readonly H3VisualStyle Auto =
            new("Auto — match the scene / story", string.Empty);

        /// <summary>Every preset, Auto first, then grouped by family.</summary>
        internal static IReadOnlyList<H3VisualStyle> All { get; } = new List<H3VisualStyle>
        {
            Auto,

            // ── Live action ────────────────────────────────────────────────────
            new("Live action — 35mm cinematic",
                "Live-action cinematic, shot on 35mm film with anamorphic lenses, shallow depth of field, natural filmic colour grading and fine grain"),
            new("Live action — modern blockbuster",
                "Live-action blockbuster cinematic, crisp digital capture, high-contrast teal-and-orange grade, wide anamorphic lens flares and heavy atmospheric haze"),
            new("Live action — handheld documentary",
                "Photoreal handheld documentary footage, available light only, naturalistic colour, restless camera and visible lens breathing"),
            new("Live action — nature documentary",
                "Nature documentary photography, long-lens telephoto compression, golden natural light and ultra-clean high-resolution detail"),
            new("Live action — film noir (B&W)",
                "Black-and-white film noir, hard low-key key light, venetian-blind shadow bars across the frame, deep crushed blacks and smoke-thick air"),
            new("Live action — 1970s 16mm",
                "Grainy 16mm film, warm faded 1970s colour stock, halation blooming around the highlights, gate weave and soft focus falloff"),
            new("Live action — silent era",
                "Silent-era black-and-white film, slightly sped-up judder, iris vignette, emulsion scratches and drifting dust"),
            new("Live action — Technicolor musical",
                "1950s Technicolor musical, hyper-saturated primary colours, glossy studio key lighting and soft-focus glamour close-ups"),
            new("Live action — symmetrical storybook",
                "Symmetrical storybook cinematic, dead-centre compositions, a flat pastel palette and deliberate deadpan staging"),
            new("Live action — cyberpunk neon noir",
                "Cyberpunk neon-noir live action, rain-slick streets, magenta and cyan practical signage, volumetric haze and heavy lens bloom"),
            new("Live action — VHS home video",
                "1980s VHS home-video look, soft interlaced scan lines, chroma bleed, tracking noise and a timecode burn-in"),
            new("Live action — found-footage horror",
                "Found-footage analog horror, a low-resolution camcorder, a harsh on-camera light punching into darkness, tape dropouts and tracking artefacts"),
            new("Live action — security camera",
                "Fixed security-camera footage, wide-angle barrel distortion, greenish low-light sensor noise, low frame-rate judder and a timestamp burned into the corner"),

            // ── Anime ──────────────────────────────────────────────────────────
            new("Anime — gacha cinematic",
                "Anime cinematic in a high-production gacha style"),
            new("Anime — 1990s cel",
                "Hand-painted 1990s cel anime, visible ink line art, painted backgrounds, a muted film-stock palette and 35mm grain"),
            new("Anime — shonen action",
                "High-energy shonen action anime, bold ink outlines, speed lines and impact frames, saturated primaries and exaggerated smear frames"),
            new("Anime — photoreal backgrounds",
                "Modern anime film, hyper-detailed photoreal painted backgrounds with god rays and lens flare, cel-shaded characters and towering volumetric skies"),
            new("Anime — watercolour pastoral",
                "Soft watercolour anime, hand-painted pastoral backgrounds, gentle diffuse daylight and a muted natural palette"),
            new("Anime — 1980s OVA",
                "1980s OVA anime, airbrushed highlights, neon-lit city backgrounds, dense mechanical detail and heavy film grain"),
            new("Anime — chibi",
                "Chibi anime, two-head-tall characters, thick clean outlines, bright flat colours and bouncy exaggerated motion"),

            // ── 3D and stop-motion ─────────────────────────────────────────────
            new("3D — animated feature",
                "3D CG animated feature film, stylised character models with soft subsurface skin, warm global illumination and shallow depth of field"),
            new("3D — game engine cinematic",
                "Real-time game-engine cinematic, physically based rendering, volumetric light shafts, screen-space reflections and crisp specular detail"),
            new("3D — clay stop-motion",
                "Stop-motion clay animation, fingerprints and seams visible in the plasticine, practical miniature sets and a slight frame-to-frame judder"),
            new("3D — puppet stop-motion",
                "Felt-and-wire puppet stop-motion, visible fabric weave and armature joints, hand-built miniature sets under warm practical lamps"),

            // ── 2D animation ───────────────────────────────────────────────────
            new("2D — paper cut-out",
                "Paper cut-out animation, layered card textures with hard torn edges, drop shadows between the layers and flat stage lighting"),
            new("2D — flat vector",
                "Flat vector 2D animation, bold simple shapes, a strictly limited palette, crisp edges and no gradients"),
            new("2D — rubber-hose cartoon",
                "1930s rubber-hose cartoon, black and white, bouncing squash-and-stretch limbs, film wobble and dust"),
            new("2D — pixel art",
                "Retro pixel-art animation, chunky limited-palette sprites, dithered gradients and CRT scanlines"),

            // ── Painted and graphic ────────────────────────────────────────────
            new("Painted — oil on canvas",
                "Living oil painting, thick visible impasto brushstrokes, canvas weave showing through and warm chiaroscuro light"),
            new("Painted — dark fantasy realism",
                "Dark fantasy oil-painted realism, chiaroscuro torchlight, desaturated earth tones and hard rim light"),
            new("Painted — storybook watercolour",
                "Children's storybook watercolour, soft transparent washes, loose pencil outlines and warm paper grain"),
            new("Painted — sumi-e ink wash",
                "Sumi-e ink wash on rice paper, bleeding black brushstrokes, wide negative space and a single accent colour"),
            new("Graphic — inked comic book",
                "Inked comic-book style, heavy spot blacks, halftone dot shading, bold outlines and a limited flat palette"),
            new("Graphic — hyperpop music video",
                "Hyperpop music-video style, RGB-split glitch boxes, blown-out highlights, strobing colour flashes and rapid perspective warps"),
        };

        /// <summary>Looks a preset up by name, falling back to <see cref="Auto"/> — for restored settings.</summary>
        internal static H3VisualStyle Resolve(string? name) =>
            All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Auto;

        /// <summary>
        /// The block handed to the prompt writer. A locked style is stated as settled fact — the same trick
        /// the wardrobe uses — because a model asked to "keep the style consistent" across N independently
        /// written clips cannot, while a model handed the exact sentence can.
        /// </summary>
        internal static string Rule(H3VisualStyle style) => style.IsAuto
            ? "VISUAL STYLE: yours to choose, but choose it deliberately. Read the medium off the scene " +
              "image where there is one, and off the story's period, place and tone where there is not — " +
              "live action, documentary, 3D CG, stop-motion, painted, graphic and animated styles are all " +
              "equally available, and anime is not the default. State it in the opening words of " +
              "[Shot 1], ahead of the shot size, and keep that same wording in every shot and every " +
              "clip.\n"
            : "VISUAL STYLE IS ALREADY DECIDED AND IS NOT YOURS TO CHOOSE. [Shot 1] opens with exactly " +
              "these words, placed ahead of the shot size and copied verbatim — never rephrased, " +
              "shortened or swapped for another art style:\n" + style.Clause + "\n" +
              "Every clip in the sequence opens the same way, and the whole video stays in that medium — " +
              "its lighting, colour palette, surface texture and the way motion is rendered all belong to " +
              "it, so describe them as that style would look. Never name any other style anywhere in the " +
              "prompt.\n";
    }
}

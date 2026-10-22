using Vintagestory.API.Client;

namespace VintageHorizons.Checks;

/// <summary>
/// Grass-covered ground is composited, not painted.
///
/// Vanilla's `chunktopsoil.fsh` draws it as `brownSoil * (1 - grass.a) + grass * grass.a`,
/// and colour-maps only the grass half. A LOD vertex carries one colour and the tint is a
/// plain multiply, so the mod stores the composite and dilutes the slot's tint by the share
/// that came from untinted dirt. That only reproduces the shader if one identity holds, and
/// everything the renderer does with those two numbers assumes it does.
/// </summary>
public static class TopSoilColorChecks
{
    public static void Run(Check c)
    {
        TheDilutedTintReproducesVanillasShader(c);
        FullCoverageIsTheOrdinaryCase(c);
        Degenerate(c);
        TheTopTileIsTakenFromATwoTileStripAndNothingElse(c);
        PartTintedModelsMixLikeTheGame(c);
        FacesAverageTheAreaTheyDraw(c);
    }

    /// <summary>
    /// A model that tints some parts and not others - cattails: tinted leaves and stems, brown
    /// cobs and tan tips the game leaves alone (owner report 2026-10-22: distant cattails went
    /// neon green). The stored colour and share must reproduce the parts drawn the game's way,
    /// (own + tinted * tint) / weight, for any tint.
    /// </summary>
    static void PartTintedModelsMixLikeTheGame(Check c)
    {
        // Cattail-like: the measured leaf, stem and cob means, roughly in cattails' proportions.
        var parts = new[]
        {
            new LodModelParts.Part(107f, 107f, 107f, 0.69f, Tinted: true),
            new LodModelParts.Part(138f, 138f, 138f, 0.14f, Tinted: true),
            new LodModelParts.Part(131f, 71f, 21f, 0.165f, Tinted: false),
            new LodModelParts.Part(198f, 173f, 91f, 0.005f, Tinted: false),
            new LodModelParts.Part(255f, 0f, 0f, 0f, Tinted: false),   // no weight: ignored
        };
        c.True(LodModelParts.TryCompose(parts, out float r, out float g, out float b,
            out LodUntintedShare share), "a model with tinted parts composes");

        float worst = 0f;
        foreach (float tint in new[] { 0.05f, 0.42f, 0.62f, 1f })
        {
            double weight = 0, drawnR = 0, drawnG = 0, drawnB = 0;
            foreach (LodModelParts.Part part in parts)
            {
                float t = part.Tinted ? tint : 1f;
                weight += part.Weight;
                drawnR += part.Weight * part.R * t;
                drawnG += part.Weight * part.G * t;
                drawnB += part.Weight * part.B * t;
            }
            worst = Math.Max(worst, Math.Abs(r * LodTopSoil.Dilute(share.R, tint) - (float)(drawnR / weight)));
            worst = Math.Max(worst, Math.Abs(g * LodTopSoil.Dilute(share.G, tint) - (float)(drawnG / weight)));
            worst = Math.Max(worst, Math.Abs(b * LodTopSoil.Dilute(share.B, tint) - (float)(drawnB / weight)));
        }
        c.True(worst < 0.01f, $"stored colour and share reproduce the game's mix of parts (worst {worst:0.0000})");
        c.True(share.R > share.B,
            "the brown cob is the untinted share, and it carries red rather than blue");

        c.False(LodModelParts.TryCompose(new[] { new LodModelParts.Part(100f, 100f, 100f, 1f, Tinted: false) },
            out _, out _, out _, out _), "a model with nothing tinted stays on the ordinary path");
        c.False(LodModelParts.TryCompose(new[] { new LodModelParts.Part(100f, 100f, 100f, 0f, Tinted: true) },
            out _, out _, out _, out _), "tinted parts with no weight are nothing to tint");
    }

    /// <summary>
    /// Each face is averaged over the area of the texture its UV draws, scaled from the model's
    /// texture units to the file's pixels, and covers as much of the block as that area is
    /// opaque. A cattail leaf face draws one blade out of a mostly empty texture.
    /// </summary>
    static void FacesAverageTheAreaTheyDraw(Check c)
    {
        // A 4 x 2 file: left half transparent, right half opaque red (0xAARRGGBB).
        const int clear = 0, red = unchecked((int)0xFFFF0000);
        int[] argb = { clear, clear, red, red, clear, clear, red, red };

        var whole = VintageHorizonsModSystem.RegionMean(argb, 4, 2, null, 16, 16);
        c.Near(255f, whole.R, 1e-3, "the whole texture averages its opaque pixels");
        c.Near(0.5f, whole.Coverage, 1e-6, "and covers as much as is opaque");

        // The model's texture is 16 units wide; the right half is u 8..16.
        var right = VintageHorizonsModSystem.RegionMean(argb, 4, 2, new[] { 8f, 0f, 16f, 16f }, 16, 16);
        c.Near(1f, right.Coverage, 1e-6, "a face drawing the opaque half covers its whole face");
        var flipped = VintageHorizonsModSystem.RegionMean(argb, 4, 2, new[] { 16f, 16f, 8f, 0f }, 16, 16);
        c.Near(1f, flipped.Coverage, 1e-6, "a mirrored UV reads the same area");
        var left = VintageHorizonsModSystem.RegionMean(argb, 4, 2, new[] { 0f, 0f, 8f, 16f }, 16, 16);
        c.Near(0f, left.Coverage, 1e-6, "a face drawing only empty texture covers nothing");
    }

    /// <summary>
    /// composite * (share + (1 - share) * tint) == soil * (1 - a) + grass * a * tint,
    /// for every channel, coverage and tint. This is the whole design in one line.
    /// </summary>
    static void TheDilutedTintReproducesVanillasShader(Check c)
    {
        // Real values: fertlow soil and the full grass overlay as the atlas averages them,
        // then the sparse and very sparse coverages, against a midsummer grass tint.
        float[] soils = { 106f, 85f, 61f, 84f, 58f, 31f, 0f, 255f };
        float[] grasses = { 149f, 151f, 127f, 148f, 149f, 129f, 255f, 0f };
        float[] coverages = { 146f / 255f, 128f / 255f, 87f / 255f, 0.25f, 0.5f, 1f };
        float[] tints = { 0.482f, 0.612f, 0.051f, 0.1f, 1f };

        int checks = 0;
        float worst = 0f;
        foreach (float soil in soils)
        foreach (float grass in grasses)
        foreach (float a in coverages)
        foreach (float tint in tints)
        {
            float composite = LodTopSoil.Composite(soil, grass, a);
            float share = LodTopSoil.UntintedShare(soil, grass, a);
            float got = composite * LodTopSoil.Dilute(share, tint);
            float want = soil * (1f - a) + grass * a * tint;
            worst = Math.Max(worst, Math.Abs(got - want));
            checks++;
        }

        c.True(worst < 0.01f,
            $"the diluted tint reproduces the top-soil shader across {checks} combinations (worst error {worst:F4})");
    }

    /// <summary>
    /// A block with no overlay must come out exactly where it was before any of this: the
    /// share is zero, so the slot holds the sampled tint unchanged and the stored colour is
    /// the texture's own. Anything else would have recoloured every rock in the world.
    /// </summary>
    static void FullCoverageIsTheOrdinaryCase(Check c)
    {
        c.Eq(0f, LodTopSoil.UntintedShare(106f, 149f, coverage: 1f), "full coverage leaves nothing untinted");
        c.Eq(149f, LodTopSoil.Composite(106f, 149f, coverage: 1f), "and the composite is just the overlay");
        c.Eq(0.482f, LodTopSoil.Dilute(0f, 0.482f), "a zero share passes the sampled tint straight through");

        // The other end: no overlay at all is the soil on its own, untouched by any tint.
        c.Eq(1f, LodTopSoil.UntintedShare(106f, 149f, coverage: 0f), "no coverage means nothing is tinted");
        c.Eq(1f, LodTopSoil.Dilute(1f, 0.482f), "and a full share ignores the tint entirely");
    }

    /// <summary>
    /// A black channel divides by zero if it is not guarded, and the share is a fraction so
    /// rounding must not push it outside 0..1 - the renderer multiplies by (1 - share).
    /// </summary>
    static void Degenerate(Check c)
    {
        c.Eq(0f, LodTopSoil.UntintedShare(0f, 0f, 0.5f), "a black block has no untinted share rather than a NaN");

        foreach (float a in new[] { 0f, 0.001f, 0.999f, 1f })
        foreach (float soil in new[] { 0f, 1f, 255f })
        foreach (float grass in new[] { 0f, 1f, 255f })
        {
            float share = LodTopSoil.UntintedShare(soil, grass, a);
            c.True(share >= 0f && share <= 1f, $"share stays in 0..1 for soil={soil} grass={grass} a={a}");
        }
    }

    /// <summary>
    /// Which half of the grass overlay counts.
    ///
    /// `specialSecondTexture` is two tiles wide, the side fringe on the left and the top on
    /// the right, and `chunktopsoil.fsh` shifts one tile right for an upward normal. The
    /// colour path averaged BOTH halves, and the fringe is mostly transparent, so the
    /// coverage came out around a third low - 0.687 for full grass where the top tile is
    /// 1.000. Coverage decides how much untinted dirt shows through, the seasonal tint has
    /// almost no blue in it, and that untinted dirt is therefore where nearly all the blue
    /// in distant grass was coming from. Measured against the owner's own cache: it stored
    /// soil-medium-normal as 128,120,98 where the top tile alone gives 152,154,127.
    ///
    /// The rule has to be asked rather than assumed, because taking the right half of a
    /// texture that is NOT a strip reads a neighbouring block's tile out of the atlas.
    /// </summary>
    static void TheTopTileIsTakenFromATwoTileStripAndNothingElse(Check c)
    {
        // On the square atlas an upgraded install has.
        static bool Square(TextureAtlasPosition rect) =>
            VintageHorizonsModSystem.IsTwoTileStrip(rect, 4096, 4096);
        c.True(Square(Rect(0.5f, 0.25f)), "a two-tile strip takes its top tile");
        c.True(!Square(Rect(0.25f, 0.25f)), "a square texture is used whole");
        c.True(!Square(Rect(0.25f, 0.5f)), "a tall texture is used whole");

        // Padding trims the rectangle, so the ratio is never exactly two; the rule has to
        // survive that without letting a square through.
        c.True(Square(Rect(0.4980f, 0.2500f)),
            "a strip trimmed by atlas padding still takes its top tile");
        c.True(!Square(Rect(0.2480f, 0.2500f)),
            "a square trimmed by atlas padding still does not");

        // A fresh install's atlas is 4096 x 2048, so in atlas fractions a 64 x 32 strip spans
        // 64/4096 by 32/2048 - the same fraction both ways. Judged in raw fractions it looked
        // square, the top tile was never cut out, and grass tops drew the whole strip: three
        // quarters grass, a quarter bare soil (2026-10-21).
        TextureAtlasPosition wideAtlasStrip = Rect(64f / 4096f, 32f / 2048f);
        TextureAtlasPosition wideAtlasSquare = Rect(32f / 4096f, 32f / 2048f);
        c.True(VintageHorizonsModSystem.IsTwoTileStrip(wideAtlasStrip, 4096, 2048),
            "on a fresh install's 2:1 atlas a two-tile strip still takes its top tile");
        c.True(!VintageHorizonsModSystem.IsTwoTileStrip(wideAtlasSquare, 4096, 2048),
            "and a square texture there is still used whole");

        // The arithmetic the split feeds, on the real measured numbers: full grass over
        // fertmedium soil. The whole strip is what the mod used to read, the top tile is
        // what vanilla draws.
        const float soil = 82.6f, grass = 151.9f;
        c.True(LodTopSoil.UntintedShare(soil, 148.2f, 0.687f) > 0.19f,
            "the whole strip leaves a fifth of the block untinted");
        c.Eq(0f, LodTopSoil.UntintedShare(soil, grass, 1f),
            "the top tile alone leaves none of it untinted");
    }

    /// <summary>An atlas rectangle of a given UV size at a fixed corner. The atlas is square,
    /// so the two axes are the same scale and the ratio is meaningful.</summary>
    static TextureAtlasPosition Rect(float width, float height) =>
        new() { x1 = 0.1f, y1 = 0.1f, x2 = 0.1f + width, y2 = 0.1f + height };
}

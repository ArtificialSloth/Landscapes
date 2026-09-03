namespace VintageHorizons.Checks;

/// <summary>
/// Which blocks share a tint slot, and what happens when the table fills.
///
/// A slot carries the untinted share - the bare dirt showing through a grass overlay, which the
/// seasonal tint must not touch. Two blocks sharing a slot share that share, so a key that is
/// too coarse lends one block's dirt to another. The old key was the MEAN of the three channels
/// rounded to eighths, which collapsed the base game's 29 top-soil blocks onto six shares:
/// `soil-medium-verysparse` at (0.176, 0.125, 0.079) and `forestfloor-5` at (0.188, 0.142,
/// 0.107) both landed in bucket 1, and whichever registered first lent the other 35% too much
/// blue. Blue is where it shows, because the seasonal tint carries almost none of it.
/// </summary>
public static class SlotKeyChecks
{
    public static void Run(Check c)
    {
        TheKeyIsWhatTheTableCanHold(c);
        TheTwoBlocksThatCollidedNoLongerDo(c);
        DistanceIsMeasuredInStoredBytes(c);
    }

    /// <summary>
    /// The key is exactly the resolution the published table carries: one byte per channel.
    /// Finer would distinguish slots the shader cannot tell apart; coarser throws away accuracy
    /// the table could have held.
    /// </summary>
    static void TheKeyIsWhatTheTableCanHold(Check c)
    {
        // Shares that round to the same byte triple are the same key, because the shader will
        // read the same three bytes for both.
        var a = new LodUntintedShare(0.5f, 0.25f, 0.125f);
        var b = new LodUntintedShare(0.5f + 0.0005f, 0.25f, 0.125f);
        c.Eq(a.Key, b.Key, "a difference below one byte is not a different slot");

        // Shares that round to different bytes ARE different keys. Stated against the rounding
        // the table itself uses rather than against a hand-picked pair: MathF.Round is
        // half-to-even, so 0.5 and 0.5 + 1/255 both land on 128 and a naive pair here fails for
        // a reason that has nothing to do with the key.
        for (int v = 0; v < 255; v++)
        {
            var lower = new LodUntintedShare(v / 255f, 0.25f, 0.125f);
            var upper = new LodUntintedShare((v + 1) / 255f, 0.25f, 0.125f);
            c.True(lower.Key != upper.Key, $"byte {v} and byte {v + 1} are different slots");
        }

        // And the key IS those bytes, in the order the slot table publishes them. If these ever
        // disagreed, two blocks would take different slots and then draw the same share, which
        // looks like the key not working rather than like a packing mistake.
        foreach (float v in new[] { 0f, 0.176f, 0.25f, 0.5f, 0.687f, 1f })
        {
            var share = new LodUntintedShare(v, v / 2f, v / 4f);
            c.Eq(Math.Clamp((int)MathF.Round(v * 255f), 0, 255), (share.Key >> 16) & 0xFF,
                $"red of {v} keys as the byte the table stores");
            c.Eq(Math.Clamp((int)MathF.Round(v / 2f * 255f), 0, 255), (share.Key >> 8) & 0xFF,
                $"green of {v / 2f} keys as the byte the table stores");
            c.Eq(Math.Clamp((int)MathF.Round(v / 4f * 255f), 0, 255), share.Key & 0xFF,
                $"blue of {v / 4f} keys as the byte the table stores");
        }

        // Every channel takes part. The old key was a mean, so it could not tell these apart.
        var flat = new LodUntintedShare(0.3f, 0.3f, 0.3f);
        var skewed = new LodUntintedShare(0.5f, 0.3f, 0.1f);
        c.True(flat.Key != skewed.Key,
            "two shares with the same mean and different channels are different slots");

        // The key packs into the three low bytes, so it can be a dictionary key without
        // colliding with an unrelated share.
        var seen = new HashSet<int>();
        for (int r = 0; r <= 255; r += 17)
        {
            for (int g = 0; g <= 255; g += 51)
            {
                for (int bl = 0; bl <= 255; bl += 85)
                {
                    var share = new LodUntintedShare(r / 255f, g / 255f, bl / 255f);
                    c.True(seen.Add(share.Key), $"share {r},{g},{bl} has its own key");
                }
            }
        }

        // Ends round the way the table stores them.
        c.Eq(0, new LodUntintedShare(0f, 0f, 0f).Key, "an all-zero share keys as zero");
        c.Eq((255 << 16) | (255 << 8) | 255, new LodUntintedShare(1f, 1f, 1f).Key,
            "and a full share keys as all ones");
    }

    /// <summary>
    /// The measured collision, from the textures themselves. These two shares are what the
    /// base game's own soil and forest floor produce, and they must not share a slot.
    /// </summary>
    static void TheTwoBlocksThatCollidedNoLongerDo(Check c)
    {
        var soilMediumVerysparse = new LodUntintedShare(0.176f, 0.125f, 0.079f);
        var forestfloor5 = new LodUntintedShare(0.188f, 0.142f, 0.107f);

        c.True(soilMediumVerysparse.Key != forestfloor5.Key,
            "soil-medium-verysparse and forestfloor-5 are different slots");

        // The size of what sharing cost: blue 0.079 against 0.107.
        int blueApart = Math.Abs((int)Math.Round(0.079f * 255) - (int)Math.Round(0.107f * 255));
        c.True(blueApart >= 7,
            $"and their blues really are far apart in stored bytes ({blueApart})");

        // The old key would have merged them. Both means round to the same eighth, which is the
        // defect stated as arithmetic so it cannot come back as an "optimisation".
        int oldA = (int)((0.176f + 0.125f + 0.079f) / 3f * 8f + 0.5f);
        int oldB = (int)((0.188f + 0.142f + 0.107f) / 3f * 8f + 0.5f);
        c.Eq(oldA, oldB, "the old mean-of-eighths key really did collapse them onto one slot");
    }

    static void DistanceIsMeasuredInStoredBytes(Check c)
    {
        var a = new LodUntintedShare(0.2f, 0.2f, 0.2f);
        c.Eq(0, a.DistanceTo(a), "a share is no distance from itself");

        var near = new LodUntintedShare(0.2f + 1f / 255f, 0.2f, 0.2f);
        c.Eq(1, a.DistanceTo(near), "one byte on one channel is a distance of one");

        var far = new LodUntintedShare(0.7f, 0.2f, 0.2f);
        c.True(a.DistanceTo(far) > a.DistanceTo(near),
            "and a bigger difference is a bigger distance");
        c.Eq(a.DistanceTo(far), far.DistanceTo(a), "distance is symmetric");

        // Every channel counts, so a share that differs on three channels is further than one
        // that differs on one by the same amount per channel.
        var oneChannel = new LodUntintedShare(0.3f, 0.2f, 0.2f);
        var threeChannels = new LodUntintedShare(0.3f, 0.3f, 0.3f);
        c.True(a.DistanceTo(threeChannels) > a.DistanceTo(oneChannel),
            "a share that differs on every channel is further away");
    }
}

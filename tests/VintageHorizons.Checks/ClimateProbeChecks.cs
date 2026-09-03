namespace VintageHorizons.Checks;

/// <summary>
/// The climate probe's geometry, which is the part that can be wrong without looking wrong.
///
/// The probe answers one question - how far from the player the client actually holds climate -
/// and it answers it by counting how many probes on a ring land in a loaded map region. If two
/// directions collapsed onto the same point, or a ring came out smaller than asked, the count
/// would still look like a sensible fraction and the reported radius would simply be wrong. The
/// design that gets built next depends on that radius, so it is worth pinning.
/// </summary>
public static class ClimateProbeChecks
{
    public static void Run(Check c)
    {
        TheRingIsAWholeRingAtTheRightRadius(c);
        TheRadiiAndSeparationsAreUsable(c);
    }

    static void TheRingIsAWholeRingAtTheRightRadius(Check c)
    {
        // Radius zero is the player's own position: every direction has to land on it, or the
        // "here" row would disagree with the ring that contains it.
        for (int d = 0; d < LodClimateProbe.Directions; d++)
        {
            LodClimateProbe.Offset(d, 0, out int dx, out int dz);
            c.Eq(0, dx, $"direction {d} at radius 0 has no x offset");
            c.Eq(0, dz, $"direction {d} at radius 0 has no z offset");
        }

        foreach (int radius in LodClimateProbe.Radii)
        {
            if (radius == 0) continue;
            var seen = new HashSet<(int, int)>();
            for (int d = 0; d < LodClimateProbe.Directions; d++)
            {
                LodClimateProbe.Offset(d, radius, out int dx, out int dz);

                // On the ring, within the rounding to whole blocks that integer offsets force.
                double got = Math.Sqrt((double)dx * dx + (double)dz * dz);
                c.True(Math.Abs(got - radius) <= 1.5,
                    $"direction {d} at radius {radius} lands on the ring (got {got:F1})");

                // Distinct, so the fraction reported is out of the number of places it says.
                c.True(seen.Add((dx, dz)),
                    $"direction {d} at radius {radius} is its own place");
            }
            c.Eq(LodClimateProbe.Directions, seen.Count,
                $"radius {radius} probes {LodClimateProbe.Directions} distinct places");
        }

        // The ring must actually surround the player rather than favouring one side: a loaded
        // footprint is not always centred, and a half-ring would read a lopsided one as a
        // smaller radius everywhere.
        foreach (int radius in new[] { 256, 2048 })
        {
            int east = 0, west = 0, north = 0, south = 0;
            for (int d = 0; d < LodClimateProbe.Directions; d++)
            {
                LodClimateProbe.Offset(d, radius, out int dx, out int dz);
                if (dx > 0) east++;
                if (dx < 0) west++;
                if (dz > 0) south++;
                if (dz < 0) north++;
            }
            c.True(east > 0 && west > 0 && north > 0 && south > 0,
                $"radius {radius} probes every side");
            c.Eq(east, west, $"radius {radius} probes east and west evenly");
            c.Eq(north, south, $"radius {radius} probes north and south evenly");
        }
    }

    static void TheRadiiAndSeparationsAreUsable(Check c)
    {
        c.True(LodClimateProbe.Radii.Length > 0, "there are rings to probe");
        c.Eq(0, LodClimateProbe.Radii[0], "the first ring is the player's own position");
        for (int i = 1; i < LodClimateProbe.Radii.Length; i++)
        {
            c.True(LodClimateProbe.Radii[i] > LodClimateProbe.Radii[i - 1],
                $"ring {i} is further out than ring {i - 1}");
        }

        // The furthest ring has to be past anything the LOD can draw, or the probe would
        // report its own limit rather than the client's.
        c.True(LodClimateProbe.Radii[^1] >= 8192,
            "the outermost ring is past any reachable draw distance");

        // Separations are candidate cell sizes, so they have to be the sizes a grid can use.
        foreach (int separation in LodClimateProbe.Separations)
        {
            c.True(separation > 0 && (separation & (separation - 1)) == 0,
                $"separation {separation} is a power of two, as a cell size must be");
        }
        for (int i = 1; i < LodClimateProbe.Separations.Length; i++)
        {
            c.True(LodClimateProbe.Separations[i] > LodClimateProbe.Separations[i - 1],
                $"separation {i} is coarser than the one before it");
        }
    }
}

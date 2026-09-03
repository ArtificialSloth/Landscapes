using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// How far the client actually has climate, and how fast climate moves once it does.
///
/// Both numbers decide the shape of the distant tint and neither can be established off the
/// disk. The whole point of a per-position tint is to stop painting the horizon with the
/// climate under the player's feet, and that only works where the client can be asked what the
/// climate out there IS.
///
/// It usually cannot. <c>ClientWorldMap.GetClimate</c> reads the map region covering the
/// position and, when the client does not hold that region, returns the constant 0xB4B4B4 -
/// temperature 180, rainfall 180 - rather than failing. That is a plausible-looking answer for
/// a place nobody has been, so a sampler built on it would quietly paint two thirds of the
/// horizon one invented colour and look like a tuning problem. <see cref="IMapRegion"/> is the
/// honest test: a region either carries a climate map or it does not.
/// </summary>
public static class LodClimateProbe
{
    /// <summary>
    /// Rings to test, in blocks. Dense where vanilla's own loaded area ends, because that is
    /// where availability is expected to stop, and stretching past any reachable LOD radius so
    /// the answer is not bounded by the question.
    /// </summary>
    public static readonly int[] Radii =
        { 0, 64, 128, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144, 8192 };

    /// <summary>Probes per ring. Sixteen is enough to see a lopsided footprint.</summary>
    public const int Directions = 16;

    /// <summary>
    /// Separations the climate gradient is measured over. These are candidate cell sizes for a
    /// climate field: the largest separation whose temperature change stays small is the
    /// coarsest grid worth storing.
    /// </summary>
    public static readonly int[] Separations = { 32, 64, 128, 256, 512, 1024 };

    /// <summary>
    /// One ring probe's offset. Pure so a check can hold the ring geometry without a world:
    /// a direction that collapsed onto its neighbour would make a lopsided footprint read as
    /// a smaller radius, which is the number this whole probe exists to report.
    /// </summary>
    public static void Offset(int direction, int radius, out int dx, out int dz)
    {
        double a = direction * 2.0 * Math.PI / Directions;
        dx = (int)Math.Round(Math.Cos(a) * radius);
        dz = (int)Math.Round(Math.Sin(a) * radius);
    }

    /// <summary>True when the client holds real climate for this position, not the default.</summary>
    public static bool HasClimate(IBlockAccessor blocks, int x, int z)
    {
        if (x < 0 || z < 0 || x >= blocks.MapSizeX || z >= blocks.MapSizeZ) return false;
        int size = Math.Max(1, blocks.RegionSize);
        IMapRegion? region = blocks.GetMapRegion(x / size, z / size);
        return region?.ClimateMap != null && region.ClimateMap.InnerSize > 0;
    }

    /// <summary>
    /// Raw, unscaled temperature and rainfall at one position, or false where the client has
    /// no climate there. Read AT SEA LEVEL on purpose: the lapse rate is what makes the
    /// adjusted temperature differ from the raw one, and at sea level the two are the same
    /// value, so the temperature needs no reconstruction.
    ///
    /// Rainfall does, because its height terms are shaped differently and are not zero at sea
    /// level. It is undone here rather than left for the caller, and a reading the engine's
    /// clamp saturated is refused rather than guessed.
    /// </summary>
    public static bool TryRawClimate(IClientWorldAccessor world, int x, int z,
        out float temperature, out float rainfall)
    {
        temperature = rainfall = 0f;
        IBlockAccessor blocks = world.BlockAccessor;
        if (!HasClimate(blocks, x, z)) return false;

        ClimateCondition? c = blocks.GetClimateAt(
            new BlockPos(x, world.SeaLevel, z), EnumGetClimateMode.WorldGenValues);
        if (c == null) return false;

        temperature = Climate.DescaleTemperature(c.WorldGenTemperature);

        // RAW rainfall, not what the read returned. Climate.GetRainFall has already added its
        // height terms to that, and at sea level - where this reads - the bonus term is at its
        // largest. Storing the returned number and using it at the terrain's own height spends
        // a sea-level rainfall on a hillside. See LodClimateMath.
        return LodClimateMath.TryRawRainfall(
            c.WorldgenRainfall * 255f, world.SeaLevel, Climate.Sealevel, out rainfall);
    }

    /// <summary>
    /// The report. Written to the log rather than chat because it is far too long for a chat
    /// panel and the owner cannot copy chat out of the game.
    /// </summary>
    public static string Describe(IClientWorldAccessor world, int camX, int camZ, float farDistance,
        LodClimateMemory? memory = null)
    {
        IBlockAccessor blocks = world.BlockAccessor;
        var text = new System.Text.StringBuilder();
        text.AppendLine("[VintageHorizons] climate availability and gradient, "
            + $"from {camX},{camZ}");
        text.AppendLine($"  world: sea level {world.SeaLevel}, region size {blocks.RegionSize}, "
            + $"map {blocks.MapSizeX} by {blocks.MapSizeZ}, LOD draws to {farDistance:F0} blocks");

        if (TryRawClimate(world, camX, camZ, out float hereT, out float hereR))
            text.AppendLine($"  here: raw temperature {hereT:F0}, rainfall {hereR:F0}");
        else
            text.AppendLine("  here: NO CLIMATE, which should be impossible standing on ground");

        if (memory != null)
        {
            text.AppendLine($"  remembered from previous visits: {memory.KnownCells()} cells "
                + $"across {memory.PatchCount} patches, {memory.PendingRequests} still being "
                + "read back. A cell answered once is answered forever, so the rings below "
                + "grow with exploration rather than resetting when the world reloads.");
        }

        // Availability. This is the number that decides whether a per-position tint is
        // possible at all, or only possible near the player.
        text.AppendLine();
        text.AppendLine($"  availability - how many of {Directions} probes per ring sit in a "
            + "map region the client actually holds");
        int reach = 0;
        foreach (int radius in Radii)
        {
            int have = 0;
            for (int d = 0; d < Directions; d++)
            {
                Offset(d, radius, out int dx, out int dz);
                if (HasClimate(blocks, camX + dx, camZ + dz)) have++;
            }
            int probes = radius == 0 ? 1 : Directions;
            int got = radius == 0 ? Math.Min(have, 1) : have;
            text.AppendLine($"    radius {radius,5}: {got,3} of {probes}");
            if (got == probes) reach = radius;
        }
        text.AppendLine($"  fully covered out to about {reach} blocks; "
            + $"the LOD draws to {farDistance:F0}");

        // Gradient, over whatever IS available. Sampled as pairs rather than a line so one
        // unlucky direction cannot set the answer.
        text.AppendLine();
        text.AppendLine("  gradient - mean absolute change between two points that far apart, "
            + "in raw temperature and rainfall counts out of 255");
        foreach (int separation in Separations)
        {
            double tSum = 0, rSum = 0, tMax = 0, rMax = 0;
            int pairs = 0;
            // A coarse lattice inside the covered radius, each point against its neighbour one
            // separation east and one south.
            int span = Math.Max(separation, reach);
            int step = Math.Max(separation, span / 12);
            for (int ox = -span; ox <= span; ox += step)
            {
                for (int oz = -span; oz <= span; oz += step)
                {
                    if (!TryRawClimate(world, camX + ox, camZ + oz, out float t0, out float r0))
                        continue;
                    for (int axis = 0; axis < 2; axis++)
                    {
                        int nx = camX + ox + (axis == 0 ? separation : 0);
                        int nz = camZ + oz + (axis == 0 ? 0 : separation);
                        if (!TryRawClimate(world, nx, nz, out float t1, out float r1)) continue;
                        double dt = Math.Abs(t1 - t0), dr = Math.Abs(r1 - r0);
                        tSum += dt; rSum += dr;
                        tMax = Math.Max(tMax, dt); rMax = Math.Max(rMax, dr);
                        pairs++;
                    }
                }
            }
            if (pairs == 0)
            {
                text.AppendLine($"    {separation,5} blocks apart: no pairs available");
                continue;
            }
            text.AppendLine($"    {separation,5} blocks apart: temperature mean {tSum / pairs:F2} "
                + $"worst {tMax:F0}, rainfall mean {rSum / pairs:F2} worst {rMax:F0} "
                + $"({pairs} pairs)");
        }

        text.AppendLine();
        text.AppendLine("  A climate field would store one temperature and rainfall per cell and "
            + "interpolate between cells, so the mean change over one cell width is roughly the "
            + "error that storing it costs. The climate colour map is 256 by 256 with one texel "
            + "per count, so one count of temperature is one texel of movement.");
        return text.ToString();
    }
}

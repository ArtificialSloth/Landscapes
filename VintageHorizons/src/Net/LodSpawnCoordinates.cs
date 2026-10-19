namespace VintageHorizons.Net;

/// <summary>
/// Block coordinates as players see them. The game's coordinate display shows the player's
/// position minus the world's default spawn (HudElementCoordinates), so the numbers a player reads
/// off the screen and types are small and often negative, while the map underneath counts from
/// its own corner, around 512,000 on a default world. Before 0.4.160 the Generate page and the
/// generation commands took typed numbers as the internal position, so a player who typed what
/// the screen showed got a run near the corner of the map, or a refusal for a negative number
/// (pre-release review B2). Everything typed or shown now goes through here. Pure, so the
/// arithmetic has checks.
/// </summary>
public static class LodSpawnCoordinates
{
    /// <summary>A typed, spawn-relative position as the internal block position.</summary>
    public static (int X, int Z) ToWorld(int x, int z, int spawnX, int spawnZ) =>
        (x + spawnX, z + spawnZ);

    /// <summary>An internal block position as the game's coordinate display would show it.</summary>
    public static (int X, int Z) ToShown(int worldX, int worldZ, int spawnX, int spawnZ) =>
        (worldX - spawnX, worldZ - spawnZ);

    /// <summary>"x, z" the way the game's coordinate display writes a position.</summary>
    public static string Describe(int worldX, int worldZ, int spawnX, int spawnZ)
    {
        (int x, int z) = ToShown(worldX, worldZ, spawnX, spawnZ);
        return $"{x}, {z}";
    }

    /// <summary>
    /// The internal block position a Generate-page request names, or null when it names none.
    /// A request from 0.4.160 or later carries spawn-relative coordinates, which count only for
    /// the Coordinates centre. An older client's request keeps its old meaning: the internal
    /// position, where a negative number meant "not given".
    /// </summary>
    public static (int X, int Z)? RequestedCentre(AssistWorldGenRequest request, int spawnX, int spawnZ)
    {
        if (request.SpawnRelative)
        {
            return request.CentreMode == EnumWorldGenCentreMode.Coordinates
                ? ToWorld(request.X, request.Z, spawnX, spawnZ)
                : null;
        }
        return request.X >= 0 || request.Z >= 0 ? (request.X, request.Z) : null;
    }
}

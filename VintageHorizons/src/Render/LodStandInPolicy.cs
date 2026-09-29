namespace VintageHorizons;

/// <summary>
/// Which quarters of a section a coarse stand-in covers.
///
/// <para>
/// A section with a finer piece that is not ready yet is drawn in that piece's place - a parent
/// covers until its children are ready, so a transition can be coarse but never a hole. Until
/// 0.4.135 the parent then covered its WHOLE area, even where the other three children were
/// ready and drawing fine terrain. A level-4 section is a kilometre across, so a single new piece
/// of ground at the edge of what the game has loaded put 16-block columns over everything around
/// the player, and wherever those stood above the real ground they showed as floating slabs.
/// </para>
/// <para>
/// A quarter is exactly the area of one child: bit <c>qz * 2 + qx</c>, the same indices
/// <see cref="LodWorld.ChildKey"/> takes. Zero means the whole section, which is every ordinary
/// draw, and it is also what an unset shader uniform reads as, so a pass that never sets it
/// draws exactly as before.
/// </para>
/// </summary>
internal static class LodStandInPolicy
{
    public const int AllQuarters = 0b1111;

    public static int QuarterBit(int qx, int qz) => 1 << (qz * 2 + qx);

    /// <summary>
    /// The quarter a packed cluster lies in. Clusters tile a section four by four and quads are
    /// split at their edges, so each quarter is exactly two by two of them and selecting whole
    /// clusters selects exactly that quarter's geometry.
    /// </summary>
    public static int QuarterOfCluster(int cell)
    {
        int perAxis = LodPackedClusterBuilder.SubdivisionsPerAxis;
        int half = perAxis / 2;
        return QuarterBit(cell % perAxis / half, cell / perAxis / half);
    }

    /// <summary>Whether a draw limited to <paramref name="quarters"/> includes this cluster.</summary>
    public static bool DrawsCluster(int quarters, int cell) =>
        quarters == 0 || (quarters & QuarterOfCluster(cell)) != 0;

    /// <summary>
    /// The quarters a section standing in for its <paramref name="uncovered"/> children draws:
    /// just those, or zero - the whole section - when the fix is off or every quarter needs it.
    /// </summary>
    public static int StandInQuarters(int uncovered, bool quarterStandIns) =>
        !quarterStandIns || uncovered == 0 || uncovered == AllQuarters ? 0 : uncovered;

    /// <summary>"whole" or the quarter list, for logs.</summary>
    public static string Describe(int quarters)
    {
        if (quarters == 0 || quarters == AllQuarters) return "whole";
        var parts = new List<string>(4);
        for (int bit = 0; bit < 4; bit++)
            if ((quarters & (1 << bit)) != 0) parts.Add(QuarterName(bit));
        return string.Join("+", parts);
    }

    static string QuarterName(int bit) => bit switch
    {
        0 => "NW",
        1 => "NE",
        2 => "SW",
        _ => "SE",
    };
}

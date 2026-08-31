using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Maps each block to a TEXTURE SLOT, and holds where that block's texture actually lives
/// inside the game's own terrain atlas.
///
/// Distant cached ground is one averaged colour per block. Vanilla ground never is - every
/// block shows a texture tile - so the two meet as speckle against a flat sheet, and no
/// correction to the average closes a difference that was never in the average. Three
/// attempts to treat it as a colour problem each looked right on the terrain they were tuned
/// on and worse than nothing on the next one. The owner, who has played the game for about a
/// thousand hours, called it conclusively: the fix is the real texture, not another
/// approximation of it.
///
/// So this does not recreate anything. The engine will say exactly which rectangle of its own
/// block atlas a block's face occupies, and that atlas can be bound and sampled directly, so
/// cached ground near the seam is drawn with vanilla's own texels.
///
/// Slots are derived from the live Block and never persisted - the same rule the tint slots
/// already follow, and for the same reason: an existing cache gains textures with no
/// re-exploration, and the mapping stays right if a game update moves a block's texture.
/// </summary>
public sealed class LodBlockTextureRegistry
{
    /// <summary>Slot 0 is "no texture": the quad keeps the averaged colour it has today.</summary>
    public const int NoSlot = 0;

    /// <summary>
    /// Cap on distinct textured blocks. A world turned out to resolve about 1,150 of them -
    /// 1,024 filled the table and refused 120 outright - so this is four times what the first
    /// guess was. The table is one small GPU texture; the cap bounds it rather than saves
    /// anything meaningful.
    /// </summary>
    public const int MaxSlots = 4096;

    /// <summary>
    /// Floats per slot: the atlas rectangle, the averaged colour, then the texture's OWN mean.
    ///
    /// The mean is what keeps this honest. The texture is applied as variation AROUND the
    /// block's known colour, never as the colour itself - <c>colour * (texel / textureMean)</c>
    /// - so the average survives exactly and only the detail comes from the atlas. Sampling
    /// the texture directly instead drew grass-covered ground as bare soil, because in this
    /// game grass is an overlay composited on top of a soil texture and the base texture is
    /// brown. The composite colour is already computed correctly elsewhere; this preserves it.
    ///
    /// The fourth row is that overlay's own rectangle. Preserving the average was only half
    /// the answer: grass-covered ground still wore the soil texture's PATTERN, so close up it
    /// read as dirt. Vanilla draws it as soil under a grass texture composited by the grass's
    /// per-pixel alpha, and sampling both is the only way to get the real thing.
    /// </summary>
    public const int FloatsPerSlot = 16;

    /// <summary>
    /// Where a block's colour texture is, and what that texture averages to. Supplied by the
    /// client, which already owns the atlas reading this needs.
    /// </summary>
    /// <param name="Overlay">
    /// The grass-style overlay composited over the base, when the block has one.
    /// </param>
    /// <param name="MeanColor">The base texture's own mean.</param>
    /// <param name="OverlayMeanColor">
    /// The mean of base and overlay composited together, used only if the overlay is actually
    /// accepted.
    ///
    /// Both are carried because the caller cannot know whether the overlay will be taken - it
    /// is refused if it sits on a different atlas page - and normalising by a mean that does
    /// not describe what the shader really samples shifts the colour badly. Dividing the soil
    /// texture by the grass composite's average inflated blue about threefold, because soil
    /// carries more blue than grass does, and turned grassy ground pale.
    /// </param>
    public readonly record struct BlockTexture(
        TextureAtlasPosition? Position, TextureAtlasPosition? Overlay,
        int MeanColor, int OverlayMeanColor);

    public delegate BlockTexture TextureResolver(Block block);

    readonly TextureResolver resolve;
    readonly Dictionary<int, int> slotByBlockId = new();
    readonly float[] table = new float[MaxSlots * FloatsPerSlot];

    int nextSlot = 1;
    int atlasTextureId;
    int atlasNumber = -1;

    public LodBlockTextureRegistry(TextureResolver resolver)
    {
        resolve = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>Bumped whenever a slot is added, so the GPU table is re-uploaded once.</summary>
    public int Version { get; private set; }

    /// <summary>Slots handed out so far, slot 0 excluded.</summary>
    public int SlotCount => nextSlot - 1;

    /// <summary>The atlas page every accepted slot lives on. Zero until the first one.</summary>
    public int AtlasTextureId => atlasTextureId;

    public IReadOnlyList<float> Table => table;

    /// <summary>Blocks refused a slot, by reason, for the status line.</summary>
    public int RefusedNoTexture { get; private set; }
    public int RefusedOtherAtlasPage { get; private set; }
    public int RefusedPerPositionColour { get; private set; }
    public int RefusedTableFull { get; private set; }

    /// <summary>Overlays taken, and overlays refused for sitting on another atlas page.</summary>
    public int OverlaysAccepted { get; private set; }
    public int OverlaysOtherPage { get; private set; }

    /// <summary>
    /// The slot for this block, assigning one on first sight. Main thread only: it reads the
    /// block registry and the texture atlas, neither of which a worker may touch.
    ///
    /// <paramref name="stableColor"/> is the same per-block colour the palette already stores.
    /// It travels with the slot because a textured quad carries the slot INSTEAD of its
    /// colour - the two share one 24-bit field - so the table has to be able to hand the
    /// colour back for everything outside the textured band.
    /// </summary>
    public int SlotFor(Block? block, int stableColor)
    {
        if (block == null || block.BlockId <= 0) return NoSlot;
        if (slotByBlockId.TryGetValue(block.BlockId, out int existing)) return existing;

        // A chiselled block averages the materials of its own block entity at that position,
        // so its colour is genuinely per-position and cannot be recovered from a table keyed
        // by block id (G46). Those keep the colour form, which is exactly today's behaviour.
        if (block.EntityClass != null)
        {
            RefusedPerPositionColour++;
            slotByBlockId[block.BlockId] = NoSlot;
            return NoSlot;
        }

        BlockTexture resolved = resolve(block);
        TextureAtlasPosition? position = resolved.Position;
        if (position == null)
        {
            RefusedNoTexture++;
            slotByBlockId[block.BlockId] = NoSlot;
            return NoSlot;
        }

        // One atlas page for now. A second page would need a second sampler and a per-quad
        // bit to choose between them; blocks that land elsewhere keep their averaged colour,
        // which is a missing texture rather than a wrong one.
        if (atlasNumber < 0)
        {
            atlasNumber = position.atlasNumber;
            atlasTextureId = position.atlasTextureId;
        }
        else if (position.atlasNumber != atlasNumber)
        {
            RefusedOtherAtlasPage++;
            slotByBlockId[block.BlockId] = NoSlot;
            return NoSlot;
        }

        if (nextSlot >= MaxSlots)
        {
            RefusedTableFull++;
            slotByBlockId[block.BlockId] = NoSlot;
            return NoSlot;
        }

        int slot = nextSlot++;
        int at = slot * FloatsPerSlot;
        table[at + 0] = position.x1;
        table[at + 1] = position.y1;
        table[at + 2] = position.x2;
        table[at + 3] = position.y2;
        (float red, float green, float blue) = Channels(stableColor);
        table[at + 4] = red;
        table[at + 5] = green;
        table[at + 6] = blue;
        table[at + 7] = 1f;
        TextureAtlasPosition? overlay = resolved.Overlay;
        bool useOverlay = overlay != null && overlay.atlasNumber == atlasNumber;
        if (overlay != null)
        {
            if (useOverlay) OverlaysAccepted++;
            else OverlaysOtherPage++;
        }

        // The mean must describe what the shader will ACTUALLY sample. Taking the composite
        // mean while the overlay is refused divides soil by grass, and soil carries more blue
        // than grass does, so blue inflated about threefold and grassy ground went pale.
        (float meanRed, float meanGreen, float meanBlue) =
            Channels(useOverlay ? resolved.OverlayMeanColor : resolved.MeanColor);
        // Floored: a texture that averages to nothing in a channel would divide the block's
        // colour by zero and blow that channel out to white.
        table[at + 8] = Math.Max(meanRed, 0.02f);
        table[at + 9] = Math.Max(meanGreen, 0.02f);
        table[at + 10] = Math.Max(meanBlue, 0.02f);

        table[at + 11] = useOverlay ? 1f : 0f;
        table[at + 12] = useOverlay ? overlay!.x1 : 0f;
        table[at + 13] = useOverlay ? overlay!.y1 : 0f;
        table[at + 14] = useOverlay ? overlay!.x2 : 0f;
        table[at + 15] = useOverlay ? overlay!.y2 : 0f;

        slotByBlockId[block.BlockId] = slot;
        Version++;
        return slot;
    }

    /// <summary>
    /// A stored palette colour split into the channels the shader reads back.
    ///
    /// **The low byte is RED.** That is not the usual convention and it is the whole reason
    /// this method exists: the mesher writes <c>color &amp; 0xFF</c> as the red vertex byte,
    /// and the vertex shader reads it the same way. A textured quad takes its colour from
    /// this table instead of from the vertex, so a table that assumed 0xRRGGBB swapped red
    /// and blue on every textured surface - which turned tan sand and brown soil blue across
    /// the whole of level 0 in 0.3.131.
    /// </summary>
    public static (float R, float G, float B) Channels(int storedColor) => (
        (storedColor & 0xFF) / 255f,
        ((storedColor >> 8) & 0xFF) / 255f,
        ((storedColor >> 16) & 0xFF) / 255f);

    /// <summary>
    /// The top tile inside Vintage Story's two-wide TopSoil overlay rectangle.
    ///
    /// The engine maps sides through the left half and moves one tile right for an upward
    /// normal. Return a copy because atlas positions belong to the engine and are shared by
    /// every renderer that uses that texture.
    /// </summary>
    public static TextureAtlasPosition RightHalf(TextureAtlasPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        TextureAtlasPosition half = position.Clone();
        half.x1 = (position.x1 + position.x2) * 0.5f;
        return half;
    }

    public string Describe() =>
        $"{SlotCount}/{MaxSlots} textured blocks on atlas {atlasTextureId}, "
        + $"{OverlaysAccepted} grass overlays ({OverlaysOtherPage} on another page), refused "
        + $"{RefusedNoTexture} no-texture/{RefusedOtherAtlasPage} other-page/"
        + $"{RefusedPerPositionColour} per-position/{RefusedTableFull} table-full";

    /// <summary>
    /// Drops every slot. A world change invalidates block ids and atlas positions together,
    /// and a stale slot would texture ground with another world's block.
    /// </summary>
    public void Clear()
    {
        slotByBlockId.Clear();
        Array.Clear(table);
        nextSlot = 1;
        atlasTextureId = 0;
        atlasNumber = -1;
        RefusedNoTexture = RefusedOtherAtlasPage = RefusedPerPositionColour = RefusedTableFull = 0;
        OverlaysAccepted = OverlaysOtherPage = 0;
        Version++;
    }
}

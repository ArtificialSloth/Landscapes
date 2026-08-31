namespace VintageHorizons.Checks;

public static class TextureSlotBackfillChecks
{
    public static void Run(Check c)
    {
        long firstKey = LodWorld.SectionKey(0, 1, 2);
        long secondKey = LodWorld.SectionKey(0, 3, 4);
        long missingKey = LodWorld.SectionKey(0, 5, 6);

        var first = new LodSection();
        first.FindOrAddPaletteEntry(1, 0, 0, textureSlot: 41);
        first.FindOrAddPaletteEntry(2, 0, 0);
        var second = new LodSection();
        second.FindOrAddPaletteEntry(3, 0, 0);

        var sections = new Dictionary<long, LodSection>
        {
            [firstKey] = first,
            [secondKey] = second,
        };
        var backfill = new LodTextureSlotBackfill();
        int resolverCalls = 0;
        ushort Resolve(int blockId)
        {
            resolverCalls++;
            return (ushort)(100 + blockId);
        }

        backfill.Begin(new[] { firstKey, missingKey, secondKey });
        c.False(backfill.Complete, "a queued resident repair starts incomplete");
        c.Eq(3, backfill.PendingSections, "all queued sections are reported");

        c.Eq(1, backfill.Pump(sections, Resolve, maxEntries: 1, maxMilliseconds: 1000),
            "the entry limit bounds one repair pump");
        c.Eq(0, resolverCalls, "an existing slot is retained without another registry lookup");
        c.Eq((ushort)41, first.Palette[0].TextureSlot, "an existing slot is unchanged");

        c.Eq(1, backfill.Pump(sections, Resolve, maxEntries: 1, maxMilliseconds: 1000),
            "the next bounded pump resumes inside the same palette");
        c.Eq((ushort)102, first.Palette[1].TextureSlot,
            "a missing resident slot is restored from its block id");

        c.Eq(1, backfill.Pump(sections, Resolve, maxEntries: 8, maxMilliseconds: 1000),
            "missing sections are skipped and later resident sections still progress");
        c.Eq((ushort)103, second.Palette[0].TextureSlot,
            "the repair reaches a later resident section");
        c.True(backfill.Complete, "the repair completes after every queued section is examined");
        c.Eq(0, backfill.PendingSections, "completion leaves no retained section keys");

        backfill.Begin(new[] { firstKey });
        backfill.Cancel();
        c.True(backfill.Complete, "turning the pipeline off cancels resident repair work");
        c.Eq(0, backfill.PendingSections, "cancellation releases queued section keys");
    }
}

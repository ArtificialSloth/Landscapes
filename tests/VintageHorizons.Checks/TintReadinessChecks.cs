using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageHorizons.Checks;

public static class TintReadinessChecks
{
    public static void Run(Check c)
    {
        LateSlotBypassesCadence(c);
        MeshWaitsForEveryUsedSlot(c);
        FrostIdentityDoesNotLeak(c);
    }

    static void LateSlotBypassesCadence(Check c)
    {
        const long cadence = 30_000;

        c.True(!LodTintRegistry.RefreshDue(
                initialized: true,
                hasUnreadySlots: false,
                elapsedMilliseconds: 1_000,
                intervalMilliseconds: cadence),
            "a stable published table keeps the ordinary 30-second cadence");
        c.True(LodTintRegistry.RefreshDue(
                initialized: true,
                hasUnreadySlots: true,
                elapsedMilliseconds: 1_000,
                intervalMilliseconds: cadence),
            "a tint registered after publication bypasses the 30-second cadence");
        c.True(LodTintRegistry.RefreshDue(
                initialized: false,
                hasUnreadySlots: false,
                elapsedMilliseconds: 0,
                intervalMilliseconds: cadence),
            "a fresh world samples its tint table immediately");
        c.True(LodTintRegistry.RefreshDue(
                initialized: true,
                hasUnreadySlots: false,
                elapsedMilliseconds: cadence,
                intervalMilliseconds: cadence),
            "the stable table still refreshes when its ordinary cadence expires");
    }

    static void MeshWaitsForEveryUsedSlot(Check c)
    {
        var section = new LodSection();
        section.Palette.Add(new LodPaletteEntry { TintSlot = LodTintRegistry.SlotNone });
        section.Palette.Add(new LodPaletteEntry { TintSlot = 1 });
        section.Palette.Add(new LodPaletteEntry { TintSlot = 3 });

        c.True(!LodTintRegistry.SectionTintsReady(section, readySlotCount: 3),
            "a mesh waits when any used registered tint has not been published");
        c.True(LodTintRegistry.SectionTintsReady(section, readySlotCount: 4),
            "the mesh becomes eligible when every used registered tint is published");

        section.Palette.Add(new LodPaletteEntry { TintSlot = LodTintRegistry.MaxSlots });
        c.True(LodTintRegistry.SectionTintsReady(section, readySlotCount: 4),
            "an unencodable tint agrees with the mesher's identity-tint fallback");

        var plain = new LodSection();
        plain.Palette.Add(new LodPaletteEntry { TintSlot = LodTintRegistry.SlotNone });
        c.True(LodTintRegistry.SectionTintsReady(plain, readySlotCount: 1),
            "untinted terrain does not wait for climate sampling");
    }

    static void FrostIdentityDoesNotLeak(Check c)
    {
        var map = new ColorMap();
        Block Plain(bool frostable) => new()
        {
            ClimateColorMap = "shared",
            ClimateColorMapResolved = map,
            Frostable = frostable,
        };

        var registry = new LodTintRegistry();
        int ordinary = registry.SlotFor(Plain(false), LodUntintedShare.None);
        int frosted = registry.SlotFor(Plain(true), LodUntintedShare.None);
        c.True(ordinary != frosted,
            "blocks sharing colour maps cannot borrow each other's frostable state");
        c.Eq(frosted, registry.SlotFor(Plain(true), LodUntintedShare.None),
            "the frostable material identity is stable across equivalent blocks");

        registry.AssignPages(new LodClimateMapPages(), new LodSeasonMapPages());
        registry.CompleteRefresh();
        int ordinaryFlags = registry.SlotTable[
            LodTintRegistry.SlotTableTexel(ordinary, LodTintRegistry.PageRow)] & 0xFF;
        int frostedFlags = registry.SlotTable[
            LodTintRegistry.SlotTableTexel(frosted, LodTintRegistry.PageRow)] & 0xFF;
        c.Eq(0, ordinaryFlags & 2,
            "an ordinary slot publishes no frostable material flag");
        c.Eq(2, frostedFlags & 2,
            "a frostable slot publishes the flag consumed by the terrain shader");
    }
}

namespace VintageHorizons;

/// <summary>
/// Who this mod is on disk, on the network and in chat, and the name it used to have.
///
/// The fork began as AliasFactory's Vintage Horizons (mod id <c>vintagehorizons</c>) and was
/// renamed Doden's Landscapes (<c>dodenslandscapes</c>) for its public release on 2026-10-07.
/// Two consequences outlive the rename:
/// <list type="bullet">
/// <item>The original mod is another LOD mod as far as players are concerned. Its commands, code
/// file, channel and Harmony ids no longer collide with ours, and this mod does not step aside for
/// it (owner, 2026-10-10): it is on <see cref="OtherLodMods.Known"/>, so the login notice names
/// it. Running the two together is untested.</item>
/// <item>This mod never reads, moves or deletes anything under the old id. Builds 0.4.138-0.4.159
/// moved the fork's earlier caches and settings across once, but <c>vintagehorizons</c> is also
/// the original mod's own folder, so after release that move could only ever take over another
/// mod's data (pre-release review B1). The owner's own data had already moved; the move was
/// removed in 0.4.160 (owner, 2026-10-19).</item>
/// </list>
/// Internal formats keep their old names on purpose: the cache's database type code, the height
/// sample file headers, the game start-up file's marker and its backup suffix. Renaming those
/// would make the new build refuse files the old one wrote.
/// </summary>
public static class ModIdentity
{
    public const string ModId = "dodenslandscapes";
    public const string DisplayName = "Doden's Landscapes";
    public const string LegacyModId = "vintagehorizons";
}

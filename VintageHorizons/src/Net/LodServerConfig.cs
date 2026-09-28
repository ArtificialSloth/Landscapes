namespace VintageHorizons.Net;

/// <summary>
/// Admin knobs for the server side, in <c>ModConfig/vintagehorizons-server.json</c>
/// (DESIGN.md §10.6). Written out on first run so the options are discoverable without
/// reading the source.
///
/// Serving is on by default, because installing the mod on a server *is* the opt-in and a
/// mod that silently does nothing until a file is edited reads as broken. What is
/// deliberately conservative is the radius: an admin who wants no map sharing at all sets
/// <see cref="EnableServing"/> false, and an admin who wants some gets a bounded amount by
/// default rather than the whole world.
/// </summary>
public class LodServerConfig
{
    /// <summary>
    /// Build a server-side LOD cache at all. Off means the server keeps no cache and
    /// serves nothing, whatever the other settings say - clients still work, using their
    /// own captures exactly as on a vanilla server.
    /// </summary>
    public bool EnableCapture = true;

    /// <summary>Answer client requests. Off keeps the cache but shares none of it.</summary>
    public bool EnableServing = true;

    /// <summary>
    /// How far from a player the assist will serve, in blocks. 0 means unlimited.
    ///
    /// This is the map-revealing control. Sections come from wherever players have
    /// collectively been, so without a cap a new player could pull a survey of the whole
    /// explored world - coastlines, structures, other people's bases - without travelling.
    /// 8192 still gives an enormous horizon while keeping that a local advantage.
    /// </summary>
    public int ServeRadiusBlocks = 8192;

    /// <summary>Sections served per player per second. See LodAssist for the reasoning.</summary>
    public int MaxSectionsPerSecondPerPlayer = LodAssist.MaxSectionsPerSecondPerPlayer;

    /// <summary>
    /// Sections served per second across all players. The cap that bounds what the server
    /// pays: every section served is a main-thread blob read.
    /// </summary>
    public int MaxSectionsPerSecondTotal = LodAssist.MaxSectionsPerSecondTotal;

    /// <summary>
    /// Build the cache from terrain the world already has, by loading every chunk column
    /// around spawn that was generated in some earlier session.
    ///
    /// On by default, which <see cref="PregenRadiusChunks"/> deliberately is not, because
    /// the two do different things. Sweeping generates nothing - it indexes terrain that
    /// already exists, costs no worldgen, adds no disk beyond the LOD cache itself, and
    /// reveals nowhere a player has not already been. A savegame accumulates terrain for
    /// as long as anyone plays, while the LOD cache only ever saw the fraction that
    /// streamed past someone running this mod.
    /// </summary>
    public bool SweepSavegame = true;

    /// <summary>
    /// How far out to look, in chunks. Every position inside it is examined; positions with
    /// no generated terrain are skipped, so a small world costs little regardless of this.
    /// 0 disables the sweep as surely as SweepSavegame false does.
    /// </summary>
    public int SweepRadiusChunks = 128;

    /// <summary>
    /// Generated columns loaded per second. Higher than the pre-generation rate because the
    /// work is a deserialize rather than a worldgen pass, and the sweep is competing with
    /// nothing on a server that is otherwise idle at startup.
    /// </summary>
    public int SweepColumnsPerSecond = 16;

    /// <summary>
    /// Existing generated columns recaptured per second while a finite regional snow-change
    /// campaign is active. Vanilla weather snapshots trigger those campaigns; one dormant
    /// region is sampled every 30 seconds so inactive terrain eventually catches up. No
    /// columns are continuously cycled between changes. Work stays on the sweep's proven-safe
    /// generated-column set and never generates missing terrain. 0 disables automatic snow
    /// refresh while retaining the one-time startup sweep.
    /// </summary>
    public int RefreshColumnsPerSecond = 1;

    /// <summary>
    /// Columns per second the background audit re-reads from the savegame and compares
    /// against what this cache proved it captured. A mismatch is queued for recapture under
    /// the same safety and scheduling rules the sweep uses.
    ///
    /// This is the half of change detection that protects correctness rather than saving
    /// work. Watching changes as they happen is not airtight: the owner's 2026-09-17 log
    /// shows three columns that unloaded before the capture queue reached them, and a crash,
    /// a capture error, or terrain edited while the mod was disabled does the same. Those
    /// are silent - the cache simply holds terrain that is no longer true - and nothing else
    /// in the design would ever notice.
    ///
    /// Sixteen a second walks a 57,600-column world about once an hour. The whole world's
    /// worth of reading measured 73 seconds on the owner's 3.22 GB save, so spread across an
    /// hour it is immeasurable. 0 disables the audit outright, and its off state costs
    /// nothing at all: no reads are requested and the read path serves no audit work.
    /// </summary>
    public int AuditColumnsPerSecond = 16;

    /// <summary>
    /// Let an admin build the LOD cache around themselves with /vhgen. Columns nobody
    /// has generated are peeked: real worldgen runs from the seed, capture reads the
    /// result, and nothing is written to the savegame. Columns that exist are loaded
    /// under the same neighbourhood rule the sweep uses, so player edits stay correct.
    ///
    /// On by default, unlike <see cref="PregenRadiusChunks"/>, and the difference is
    /// the same one that puts sweeping on by default: this runs only when a person
    /// with the controlserver privilege asks, and the reply states the cost first.
    /// Off means the command still exists but refuses, with the reason.
    /// </summary>
    public bool EnableGenerateCommand = true;

    /// <summary>
    /// The largest radius, in chunks, that /vhgen accepts. 0 disables the command as
    /// surely as the flag does. 128 is a 4096-block radius: 66,049 columns, roughly 48
    /// minutes at the engine's measured ~23 columns per second of worldgen.
    /// </summary>
    public int GenerateMaxRadiusChunks = 128;

    /// <summary>
    /// The radius used when /vhgen start gets no argument. Small on purpose: a person
    /// typing a command wants a result. 32 chunks is 4,225 columns, roughly 3 minutes.
    /// </summary>
    public int GenerateDefaultRadiusChunks = 32;

    /// <summary>
    /// Columns started per second. The engine saturates near 23 per second whatever
    /// this says. A higher value only means the in-flight cap binds first. A lower one
    /// is a real throttle for a server with players on it.
    /// </summary>
    public int GenerateColumnsPerSecond = 16;

    /// <summary>
    /// Peeks outstanding at once. A memory ceiling as much as a contention one: each
    /// peek that lands hands a whole chunk column to the capture thread, which unpacks
    /// it to read it - 1-2 MB per column, held until capture drains it. 64 is
    /// TopoHorizon's measured value; unbounded reached ~520 in flight and every peek
    /// slowed under the contention.
    /// </summary>
    public int GenerateMaxInFlight = 64;

    /// <summary>
    /// Allow controlserver users to permanently generate a bounded part of the real
    /// savegame with /vhworldgen. The command remains registered when this is false so it
    /// can explain which setting disabled it.
    /// </summary>
    public bool EnableWorldGeneration = true;

    /// <summary>Largest radius /vhworldgen accepts. 256 is 263,169 square columns.</summary>
    public int WorldGenMaxRadiusChunks = 256;

    /// <summary>Radius used when /vhworldgen start omits one.</summary>
    public int WorldGenDefaultRadiusChunks = 32;

    /// <summary>
    /// Balanced preset: keep the engine generation queue around this many columns. The
    /// engine's hard queue is 2,000 including neighbours; this setting never reaches it.
    /// </summary>
    public int WorldGenQueueTarget = 120;

    /// <summary>Hard bound on columns kept resident while capture catches up.</summary>
    public int WorldGenMaxInFlight = 160;

    /// <summary>Stop issuing while this many distant-terrain sections await saving.</summary>
    public int WorldGenDirtySectionLimit = 512;

    /// <summary>Stop issuing when the oldest distant-terrain save has waited this long.</summary>
    public int WorldGenOldestDirtySeconds = 120;

    /// <summary>
    /// Pre-build the cache around spawn at startup, in chunks of radius. 0 (default)
    /// means never, and the cache then fills only as players travel or an admin runs
    /// /vhgen.
    ///
    /// This runs the same peek generator /vhgen uses. It generates terrain nobody has
    /// visited and captures it, and it writes NOTHING to the savegame - the first
    /// version loaded columns instead, which cost worldgen time and disk for terrain no
    /// player had seen. The setting still reveals map that nobody has explored, which is
    /// why it stays off unless an admin asks for it.
    ///
    /// This is the one setting that makes the mod generate terrain nobody has visited, so
    /// it is off unless an admin asks for it. Worth asking for: it is the difference
    /// between a horizon on the first join and one that appears over weeks of play. Cost is
    /// worldgen time and disk - at the measured mean 45.9 KB a section, radius 64 (a 4096
    /// block square) is on the order of a few hundred MB.
    /// </summary>
    public int PregenRadiusChunks;

    /// <summary>Chunk columns requested per second while pre-generating. Keep it modest.</summary>
    public int PregenColumnsPerSecond = 8;

    /// <summary>
    /// Let the game's chunk thread sleep after a pass over its queue that found nothing to do,
    /// as its other threads do, where the game would otherwise go straight round again (see
    /// <see cref="ChunkThreadRest"/>). With the default single world-generation thread on a
    /// machine with 8 or more logical processors, the game re-scans parked neighbour columns
    /// about 100,000 times a second while players explore new terrain, and the garbage that
    /// makes forces collections that pause everything. Generation is unchanged. False leaves
    /// the game's chunk thread exactly as it ships: the patch is not installed at all.
    /// </summary>
    public bool RestIdleChunkThread = true;

    /// <summary>
    /// Horizon silhouettes (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER 4.9). Null, the default, is
    /// automatic: on for singleplayer and LAN-hosted worlds and off on dedicated servers. True
    /// explicitly opts a dedicated server in. False switches them off everywhere, and the server
    /// then keeps no silhouette heights at all.
    /// </summary>
    public bool? EnableSilhouettes;

    /// <summary>
    /// Use an installed, accepted height accelerator for the broad silhouette field. Watersheds'
    /// native sampler is preferred when both supported mods are installed; Algernon's Terrain
    /// Sampler is accepted when Watersheds is absent. False keeps the standalone preview path.
    /// </summary>
    public bool SilhouetteUseAccelerators = true;

    /// <summary>
    /// Maximum silhouette reach a dedicated server grants, in blocks. Integrated servers do not
    /// apply this clamp: their player owns both the work and the map being revealed.
    /// </summary>
    public int SilhouetteReachBlocks = 8192;

    /// <summary>Engine terrain previews issued per second when no accepted accelerator answers.</summary>
    public int SilhouettePreviewsPerSecond = Silhouette.LodSilhouetteBuilder.PreviewsPerSecond;

    /// <summary>
    /// Set to "1" in a dedicated test server's environment to keep silhouette heights there for
    /// the accelerator comparison (plan P0.3) without opting players into silhouettes.
    /// Developer-only: players are offered nothing unless EnableSilhouettes is explicitly true.
    /// </summary>
    public const string MeasureSilhouettesVariable = "VINTAGEHORIZONS_SILHOUETTE_MEASURE";

    /// <summary>Whether this server offers silhouettes and keeps their heights.</summary>
    public bool SilhouettesEnabled(bool isDedicated) => EnableSilhouettes != false
        && (!isDedicated || EnableSilhouettes == true);

    /// <summary>
    /// Whether this server keeps silhouette heights: wherever it offers silhouettes, and on a
    /// dedicated server measuring them (<see cref="MeasureSilhouettesVariable"/>) unless the
    /// config switched silhouettes off.
    /// </summary>
    public bool SilhouetteHeightsKept(bool isDedicated, bool measuring) =>
        SilhouettesEnabled(isDedicated) || isDedicated && measuring && EnableSilhouettes != false;

    /// <summary>Clamp to values that cannot wedge the server, whatever the file says.</summary>
    public void Sanitize()
    {
        if (ServeRadiusBlocks < 0) ServeRadiusBlocks = 0;
        // 256 chunks is a 4096-block radius. Past that the disk cost stops being something
        // an admin can absorb by accident.
        PregenRadiusChunks = Math.Clamp(PregenRadiusChunks, 0, 256);
        PregenColumnsPerSecond = Math.Clamp(PregenColumnsPerSecond, 1, 64);
        // A wider ceiling than pregen's, because the cost scales with terrain that exists
        // rather than with the radius: examining a position that was never generated is an
        // index lookup, so a large radius over a small world is nearly free.
        SweepRadiusChunks = Math.Clamp(SweepRadiusChunks, 0, 512);
        SweepColumnsPerSecond = Math.Clamp(SweepColumnsPerSecond, 1, 64);
        // Snow campaigns get a lower ceiling than the finite startup pass. Sixteen is the
        // already-tested startup cadence; the ordinary background default remains one.
        RefreshColumnsPerSecond = Math.Clamp(RefreshColumnsPerSecond, 0, 16);
        // A trickle by design. Past 256 the audit stops being background work and starts
        // competing with the things it exists to protect.
        AuditColumnsPerSecond = Math.Clamp(AuditColumnsPerSecond, 0, 256);
        // Same ceiling as pregen: past 256 chunks the time cost stops being something an
        // admin grasps from a chat reply. The default clamps against the ceiling, not
        // against 256 - a lowered ceiling must not leave a default the command refuses.
        GenerateMaxRadiusChunks = Math.Clamp(GenerateMaxRadiusChunks, 0, 256);
        GenerateDefaultRadiusChunks = GenerateMaxRadiusChunks == 0
            ? 0 : Math.Clamp(GenerateDefaultRadiusChunks, 1, GenerateMaxRadiusChunks);
        GenerateColumnsPerSecond = Math.Clamp(GenerateColumnsPerSecond, 1, 64);
        GenerateMaxInFlight = Math.Clamp(GenerateMaxInFlight, 1, 256);
        WorldGenMaxRadiusChunks = Math.Clamp(WorldGenMaxRadiusChunks, 0, 256);
        WorldGenDefaultRadiusChunks = WorldGenMaxRadiusChunks == 0
            ? 0 : Math.Clamp(WorldGenDefaultRadiusChunks, 1, WorldGenMaxRadiusChunks);
        // The advertised Maximum preset is 900, leaving more than half the engine's
        // 2,000-entry queue free for neighbour expansion. Kept columns have a separate
        // 1,200 ceiling; they are already out of the generation queue while capture drains.
        WorldGenMaxInFlight = Math.Clamp(WorldGenMaxInFlight, 1, 1200);
        WorldGenQueueTarget = Math.Clamp(WorldGenQueueTarget, 1, 900);
        WorldGenQueueTarget = Math.Min(WorldGenQueueTarget, WorldGenMaxInFlight);
        WorldGenDirtySectionLimit = Math.Clamp(WorldGenDirtySectionLimit, 16, 4096);
        WorldGenOldestDirtySeconds = Math.Clamp(WorldGenOldestDirtySeconds, 10, 600);
        SilhouetteReachBlocks = Math.Clamp(SilhouetteReachBlocks,
            Silhouette.LodSilhouetteBuilder.MinReachBlocks,
            Silhouette.LodSilhouetteBuilder.MaxReachBlocks);
        SilhouettePreviewsPerSecond = Math.Clamp(SilhouettePreviewsPerSecond, 1, 64);
        // Ceilings derived from measurement, not taste: a served section costs ~0.9ms of
        // main-thread SQLite blob read (415 sections, 348ms, on a warm cache). So 128/s is
        // ~115ms per second, around 11% of a core, which is the most an admin should be
        // able to hand to this by editing a file. The original 1024 would have been ~920ms
        // per second - a server wedged by its own config.
        MaxSectionsPerSecondPerPlayer = Math.Clamp(MaxSectionsPerSecondPerPlayer, 1, 64);
        MaxSectionsPerSecondTotal = Math.Clamp(MaxSectionsPerSecondTotal, 1, 128);
    }

    /// <summary>True when the sweep is configured to actually do something.</summary>
    public bool SweepEnabled => SweepSavegame && SweepRadiusChunks > 0;

    /// <summary>Snow campaigns depend on the generated-column map built by the sweep.</summary>
    public bool RefreshEnabled => SweepEnabled && RefreshColumnsPerSecond > 0;

    /// <summary>The audit needs the sweep's safety rules and its savegame read path.</summary>
    public bool AuditEnabled => SweepEnabled && AuditColumnsPerSecond > 0;

    /// <summary>True when /vhgen is configured to accept a start.</summary>
    public bool GenerateEnabled => EnableGenerateCommand && GenerateMaxRadiusChunks > 0;

    /// <summary>True when /vhworldgen is configured to accept a new job.</summary>
    public bool WorldGenerationEnabled => EnableWorldGeneration && WorldGenMaxRadiusChunks > 0;

    public string Describe() =>
        $"capture {(EnableCapture ? "on" : "off")}, serving {(EnableServing ? "on" : "off")}, "
        + $"radius {(ServeRadiusBlocks > 0 ? ServeRadiusBlocks + " blocks" : "unlimited")}, "
        + $"{MaxSectionsPerSecondPerPlayer}/s per player, {MaxSectionsPerSecondTotal}/s total, "
        + $"sweep {(SweepEnabled ? SweepRadiusChunks + " chunks" : "off")}, "
        + $"refresh {(RefreshEnabled ? RefreshColumnsPerSecond + "/s" : "off")}, "
        + $"audit {(AuditEnabled ? AuditColumnsPerSecond + "/s" : "off")}, "
        + $"generate {(GenerateEnabled ? "on request up to " + GenerateMaxRadiusChunks + " chunks" : "off")}, "
        + $"world generation {(WorldGenerationEnabled ? "on request up to " + WorldGenMaxRadiusChunks
            + " chunks, queue " + WorldGenQueueTarget : "off")}, "
        + $"silhouettes {(EnableSilhouettes == false ? "off" : EnableSilhouettes == true
            ? "on (dedicated reach up to " + SilhouetteReachBlocks + " blocks)"
            : "automatic (singleplayer and LAN only)")}, "
        + $"silhouette accelerators {(SilhouetteUseAccelerators ? "on" : "off")}, "
        + $"previews {SilhouettePreviewsPerSecond}/s, "
        + $"chunk thread rest {(RestIdleChunkThread ? "on" : "off")}";
}

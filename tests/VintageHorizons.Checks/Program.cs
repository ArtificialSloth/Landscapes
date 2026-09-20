using System.Diagnostics;

namespace VintageHorizons.Checks;

/// <summary>
/// The fast tier of scripts/check.sh: everything that can be proven without a game process.
/// Runs sequentially and in-process, which is not a limitation to work around - LodWorld
/// carries mutable static state (DetailDistance) that several checks set, so sequential is
/// the only correct order anyway.
/// </summary>
public static class Program
{
    static readonly (string Category, string Name, Action<Check> Run)[] Suites =
    {
        ("probe", "assembly loading", ProbeChecks.Run),
        ("pure", "key math", KeyMathChecks.Run),
        ("pure", "section runs", SectionChecks.Run),
        ("pure", "section properties", SectionPropertyChecks.Run),
        ("pure", "section merge", SectionMergeChecks.Run),
        ("pure", "mip downsample", MipChecks.Run),
        ("pure", "residency", ResidencyChecks.Run),
        ("pure", "change locality", ChangeLocalityChecks.Run),
        ("pure", "render dirty scheduling", RenderDirtySchedulerChecks.Run),
        ("pure", "radial cache demand", RadialDemandChecks.Run),
        ("pure", "terrain tint readiness", TintReadinessChecks.Run),
        ("pure", "vanilla render readiness", VanillaReadinessChecks.Run),
        ("pure", "handoff gap log", HandoffGapChecks.Run),
        ("pure", "sky witness", SkyWitnessChecks.Run),
        ("static", "late range check", LateRangeCheckChecks.Run),
        ("fixture", "vanilla draw range", VanillaDrawRangeChecks.Run),
        ("fixture", "terrain sampler layout", TerrainSamplerChecks.Run),
        ("fixture", "water depth pass", WaterDepthPassChecks.Run),
        ("fixture", "water OIT stage", WaterOitChecks.Run),
        ("pure", "water capture", WaterCaptureChecks.Run),
        ("pure", "water depth repair", WaterDepthRepairChecks.Run),
        ("pure", "water junction depth", WaterJunctionChecks.Run),
        ("pure", "seam distance field", SeamFieldChecks.Run),
        ("pure", "band shape", BandShapeChecks.Run),
        ("pure", "water footprint", WaterFootprintChecks.Run),
        ("pure", "water edge", WaterEdgeChecks.Run),
        ("pure", "phase timing", PhaseCostChecks.Run),
        ("fixture", "savegame read path", SavegameReaderChecks.Run),
        ("fixture", "savegame change detection", SavegameScanChecks.Run),
        ("pure", "vanilla horizon effects", HorizonEffectsChecks.Run),
        ("pure", "GPU renderer policy", GpuRendererChecks.Run),
        ("pure", "GPU regional arenas", GpuArenaChecks.Run),
        ("pure", "GPU indirect layout", GpuIndirectChecks.Run),
        ("pure", "packed opaque quads", PackedQuadChecks.Run),
        ("pure", "packed opaque clusters", PackedClusterChecks.Run),
        ("pure", "depth pyramid", HzbChecks.Run),
        ("pure", "hzb projection", HzbProjectionChecks.Run),
        ("pure", "hzb field harness", HzbFieldChecks.Run),
        ("pure", "tick allowances", AllowanceChecks.Run),
        ("pure", "drain budgets", DrainBudgetChecks.Run),
        ("pure", "texture slot backfill", TextureSlotBackfillChecks.Run),
        ("fixture", "persistence acknowledgements", PersistenceChecks.Run),
        ("pure", "far distance", FarDistanceChecks.Run),
        ("pure", "benchmark routes", BenchRouteChecks.Run),
        ("pure", "mesher", MesherChecks.Run),
        ("pure", "server config", ConfigChecks.Run),
        ("pure", "lod mod deferral", DeferralChecks.Run),
        ("fixture", "blob format", StoreChecks.Run),
        ("fixture", "frustum", FrustumChecks.Run),
        ("fixture", "visibility traversal", TraversalChecks.Run),
        ("pure", "subtree height aggregate", SubtreeHeightChecks.Run),
        ("pure", "cave culling", CaveCullChecks.Run),
        ("pure", "cave span graph", CaveSpanChecks.Run),
        ("fixture", "block policy", PolicyChecks.Run),
        ("fixture", "chiselled block stand-ins", ChiselStandInChecks.Run),
        ("pure", "top soil colour", TopSoilColorChecks.Run),
        ("pure", "tint slot keys", SlotKeyChecks.Run),
        ("pure", "interior changes", InteriorChangeChecks.Run),
        ("pure", "season blend weight", SeasonWeightChecks.Run),
        ("pure", "climate probe geometry", ClimateProbeChecks.Run),
        ("pure", "climate field", ClimateFieldChecks.Run),
        ("pure", "climate memory", ClimateMemoryChecks.Run),
        ("pure", "climate rainfall", ClimateRainfallChecks.Run),
        ("pure", "season colour column", SeasonColorChecks.Run),
        ("pure", "remote keys", RemoteKeyChecks.Run),
        ("pure", "chunk generation", GenerateChecks.Run),
        ("pure", "savegame world generation", WorldGenChecks.Run),
        ("fixture", "server assist", ServerAssistChecks.Run),
        ("pure", "wire format", WireFormatChecks.Run),
        ("pure", "horizon silhouettes", SilhouetteChecks.Run),
        ("fixture", "silhouette wiring", SilhouetteChecks.RunWiring),
        ("pure", "distant anti-aliasing", DistantAntiAliasingChecks.Run),
        ("static", "assets and constants", StaticAssetChecks.Run),
    };

    public static int Main(string[] args)
    {
        // Only a bare word filters; anything dash-prefixed is a host flag that leaked
        // through `dotnet run` rather than something a caller meant for us. Treating one
        // as a suite filter silently runs nothing and still exits zero, which is the
        // worst possible failure mode for a check runner.
        string? only = args.FirstOrDefault(a => !a.StartsWith('-'));

        // Not a suite: it reports numbers and asserts nothing, so it must not be reachable
        // by the suite filter, where a name that matches no suite is deliberately an error.
        if (string.Equals(only, "bench", StringComparison.OrdinalIgnoreCase))
        {
            MesherBench.Run();
            return 0;
        }

        // Also not a suite, and for the same reason: it reports numbers over a real cache
        // rather than asserting anything. HzbFieldChecks covers the machinery it uses.
        if (string.Equals(only, "hzbfield", StringComparison.OrdinalIgnoreCase))
        {
            return HzbField.Run(args);
        }

        // Also not a suite, for the same reason again: it reports how much of the cached
        // terrain is sealed inside rock, over a real cache, and asserts nothing.
        if (string.Equals(only, "cavefield", StringComparison.OrdinalIgnoreCase))
        {
            return CaveField.Run(args);
        }

        // And again: it reports what half-block terrain costs, over a real cache.
        if (string.Equals(only, "slabfield", StringComparison.OrdinalIgnoreCase))
        {
            return SlabField.Run(args);
        }

        // And again: it reports what it costs to stop a crop hiding the terrain behind it.
        if (string.Equals(only, "cutoutfield", StringComparison.OrdinalIgnoreCase))
        {
            return CutoutField.Run(args);
        }

        // And again: it reports what it costs to darken submerged ground by its water depth.
        if (string.Equals(only, "waterdepthfield", StringComparison.OrdinalIgnoreCase))
        {
            return WaterDepthField.Run(args);
        }

        // And again: it compares a client cache with its server-side sibling, and times the
        // offer scanner's queries, over real caches.
        if (string.Equals(only, "siblingfield", StringComparison.OrdinalIgnoreCase))
        {
            return SiblingField.Run(args);
        }

        Console.WriteLine();
        Console.WriteLine("  VintageHorizons fast checks");
        Console.WriteLine("  game: " + GameAssemblies.GamePath);
        Console.WriteLine();

        var total = new Stopwatch();
        total.Start();

        int assertions = 0, failed = 0, ran = 0;

        foreach ((string category, string name, Action<Check> run) in Suites)
        {
            if (only != null && !category.Contains(only, StringComparison.OrdinalIgnoreCase)
                             && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;

            ran++;
            var check = new Check();
            string? crash = null;

            var watch = Stopwatch.StartNew();
            try
            {
                run(check);
            }
            catch (Exception e)
            {
                // A suite that throws is itself a failure, not a reason to abandon the run:
                // one unloadable game type must not hide every check that needs no game type.
                crash = e.ToString();
            }
            watch.Stop();

            assertions += check.Passed + check.Failures.Count;
            failed += check.Failures.Count + (crash != null ? 1 : 0);

            string label = ("  " + category).PadRight(15) + name + " ";
            string dots = new string('.', Math.Max(3, 44 - label.Length));
            string result = crash != null
                ? "CRASHED"
                : check.Failures.Count == 0
                    ? $"{check.Passed} ok"
                    : $"{check.Failures.Count} FAILED of {check.Passed + check.Failures.Count}";

            Console.WriteLine($"{label}{dots} {result}  ({watch.ElapsedMilliseconds}ms)");

            foreach (string failure in check.Failures) Console.WriteLine("      x " + failure);
            if (crash != null) Console.WriteLine("      x suite threw:\n        " + crash.Replace("\n", "\n        "));
        }

        total.Stop();

        Console.WriteLine();

        // A filter that matched nothing must not look like success. Exiting zero on an
        // empty run is how a check suite quietly stops checking.
        if (ran == 0)
        {
            Console.WriteLine($"  no suite matched '{only}' - nothing ran");
            Console.WriteLine();
            return 2;
        }

        Console.WriteLine($"  {assertions} assertions, {failed} failures, {total.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine();

        return failed == 0 ? 0 : 1;
    }
}

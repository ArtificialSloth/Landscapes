using System.Text;
using System.Text.RegularExpressions;

namespace VintageHorizons.Checks;

/// <summary>
/// Invariants that live across file boundaries, where no compiler or runtime check can
/// reach. Everything here reads committed files off disk and touches no game type at all.
/// </summary>
public static class StaticAssetChecks
{
    public static void Run(Check c)
    {
        AsciiOnly(c);
        TextFilesAreUtf8(c);
        TintSlotAgreement(c);
        AlphaPacking(c);
        LodFallbackAndStableColour(c);
        VanillaLightingWiring(c);
        ReadinessShadowWiring(c);
        ShaderLoadReportsSuccessNotOnlyFailure(c);
        ReadinessTelemetryContract(c);
        OwnershipMaskWiring(c);
        WaterSurfaceWiring(c);
        OcclusionCullingWiring(c);
        PersistenceCadenceWiring(c);
        CaveCullingDefaultsOn(c);
        HorizonSuppressionWiring(c);
        VersionAgreement(c);
        AssistServeLoopDoesNotLogProgress(c);
        ChatCommandNamesAreUnique(c);
        ConfigDialogWiring(c);
        NoIntegerVectorUniforms(c);
        SourceHasNoControlCharacters(c);
        IndirectShaderVariant(c);
        SubtreeBoundsStayOutOfTheCoverageGate(c);
        SamplerUploadWiring(c);
    }

    /// <summary>
    /// Every texture this renderer uploads goes through one helper that makes the destination
    /// texture unit current first.
    ///
    /// `LoadOrUpdateTextureFromBgra` reaches GL.BindTexture with no GL.ActiveTexture of its own
    /// and never restores what it displaced, so the texture it writes stays bound to whatever
    /// unit happened to be current. An upload written inline in the middle of a binding sequence
    /// therefore steals a unit from the sampler that was just bound to it - no error, nothing in
    /// a diff, and a wrong picture that reads as a colour bug rather than a binding one. The
    /// tint ramp's upload sat one line after the block atlas bind, and the blend band drew the
    /// ramp as its block texture: several times too bright, and only inside the band, because
    /// the band is the only place the atlas is sampled.
    ///
    /// The rule this pins is the only thing that can catch the fourth upload someone adds.
    /// </summary>
    static void SamplerUploadWiring(Check c)
    {
        string renderer = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons",
            "src", "Render", "LodTerrainRenderer.cs"));

        c.Eq(1, Regex.Matches(renderer, @"capi\.Render\.LoadOrUpdateTextureFrom").Count,
            "the renderer uploads textures from exactly one place");

        int activate = renderer.IndexOf("GL.ActiveTexture(TextureUnit.Texture0 + unit);",
            StringComparison.Ordinal);
        int upload = renderer.IndexOf("capi.Render.LoadOrUpdateTextureFromBgra(",
            StringComparison.Ordinal);
        c.True(activate >= 0, "that place makes a texture unit current");
        c.True(upload > activate && upload - activate < 120,
            "and does so immediately before the upload, not somewhere else in the file");

        // Upload unit and bind unit are the same named constant, in both binding paths. They
        // were literals when they disagreed, which is exactly why they are not any more.
        c.True(renderer.Contains("UploadOwnTexture(tints.Ramp, TintRampUnit, ref texture)",
                StringComparison.Ordinal),
            "the tint ramp is uploaded onto its own unit");
        c.Eq(2, Regex.Matches(renderer,
                @"BindTexture2D\(""tintRamp"", tintRampTexture\.TextureId, TintRampUnit\)").Count,
            "and bound to the same one by the frame uniforms and by the sampler repair");
        c.True(renderer.Contains("UploadOwnTexture(seamField.Texels, SeamFieldUnit, ref texture)",
                StringComparison.Ordinal)
            && renderer.Contains(
                "UploadOwnTexture(readinessMask.Texels, ReadinessMaskUnit, ref texture)",
                StringComparison.Ordinal),
            "and the seam field and readiness mask go the same way");

        // One version counter, because the ramp is a texture and a texture is not program
        // state. Three counters plus a method that assigned a field was a guard that never
        // closed, and the ramp re-uploaded on every frame of every indirect pass.
        c.Eq(0, Regex.Matches(renderer, @"uploaded(Packed|Indirect)TintVersion").Count,
            "the ramp has one upload counter, not one per shader program");
    }

    /// <summary>
    /// Cached water's surface terms, which exist to make it meet vanilla's without a line.
    ///
    /// Every one of them is a uniform the renderer must actually upload. An uploaded-nowhere
    /// float uniform reads as zero with nothing in the log, and zero is a plausible-looking
    /// value for all of these - no glint, no foam, a flat surface - so the whole feature
    /// would silently not exist and look exactly like a tuning problem. That is G42's shape,
    /// and this is the only place it can be caught.
    /// </summary>
    static void WaterSurfaceWiring(Check c)
    {
        string renderer = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons",
            "src", "Render", "LodTerrainRenderer.cs"));
        string fragment = ShaderBody("fsh");

        // The opacity term. Vanilla grades water by view angle; cached water was one flat
        // constant, which at the seam let the cached lakebed show through a surface vanilla
        // draws solid. Transcribed rather than approximated, so the two agree by derivation.
        c.True(fragment.Contains("float fresnel = max(0.2, 0.01 + 4.5 * rise * rise * rise);",
                StringComparison.Ordinal)
            && fragment.Contains("outAlpha = clamp(0.8 * fresnel, 0.0, 1.0);", StringComparison.Ordinal),
            "cached water carries the engine's own view-angle opacity expression");
        // A dot product one ulp below -1 would cube a negative number. Vanilla writes the
        // bare add and gets away with it; the guard is free and this pins it.
        c.True(fragment.Contains("float rise = max(0.0, 1.0 + eyeFresnel.y);", StringComparison.Ordinal),
            "and cannot raise a negative base to an odd power");
        // Applied to every cached water fragment, not faded in over the band: it describes the
        // surface rather than correcting it, and a band-weighted opacity would BE a seam.
        c.False(fragment.Contains("waterFresnel", StringComparison.Ordinal)
            && fragment.Contains("bandWeight * clamp(bandStrength, 0.0, 1.0) * waterFresnel",
                StringComparison.Ordinal),
            "opacity is never weighted by the blend band");

        // Carried on every cached water fragment, NOT faded across the band. Band-weighting
        // them was tried for one build to bound the per-pixel cost and produced the defect it
        // was meant to avoid: the terms faded out and then stopped, drawing a line across
        // distant water exactly where the band ended. A term that ends nowhere has no edge.
        c.True(fragment.Contains("float waterAmount = band == 1 ? 1.0 : 0.0;",
                StringComparison.Ordinal),
            "glint and foam are carried everywhere cached water draws");
        c.False(fragment.Contains("band == 1 ? bandWeight", StringComparison.Ordinal),
            "and are never faded out across the blend band, which would put back the line");

        foreach (string uniform in new[]
        {
            "waterFresnel", "waterGlint", "waterFoam", "waterNearChunks",
            "camChunkX", "camChunkZ",
            "vhSunPosRel", "vhReflectColor", "vhSunSpecular",
            "vhWaterWave", "vhWindWave", "vhWaterFlow", "vhWindSpeed", "vhFoamOrigin",
        })
        {
            c.True(fragment.Contains($" {uniform};", StringComparison.Ordinal),
                $"the fragment shader declares {uniform}");
            c.True(renderer.Contains($"prog.Uniform(\"{uniform}\"", StringComparison.Ordinal),
                $"and the renderer uploads {uniform} - an unuploaded float reads as zero (G42)");
        }

        // Sampler bindings are GLOBAL GL state and the engine rewrites several of them on
        // every ShaderProgramBase.Use(), binding the shared includes' own samplers to whatever
        // unit the linker gave them in THAT program. So a program that is activated without
        // re-applying the frame uniforms draws with someone else's textures on this mod's
        // units. That is what put a flat square on water and thin cover for four builds: the
        // seam field sampled a shadow map, read ~0, and the shader read zero as "distance zero
        // from vanilla", which is full band strength everywhere inside the field window.
        //
        // Two rebind sites, and both matter. The one in the indirect bucket covers every path
        // downstream of a program switch; the one in the water pass covers the frame where no
        // bucket ran. Neither reports anything when it is missing, which is why this is here.
        c.True(renderer.Contains("void RebindOwnSamplers(IShaderProgram target)", StringComparison.Ordinal),
            "the renderer can re-bind its own samplers independently of the frame uniforms");
        c.Eq(2, CountOccurrences(renderer, "RebindOwnSamplers(prog"),
            "and does so after a program switch and again before the water pass");
        int bucketUse = renderer.IndexOf("drawProgram.Stop();", StringComparison.Ordinal);
        int bucketRebind = renderer.IndexOf("RebindOwnSamplers(prog);", StringComparison.Ordinal);
        c.True(bucketUse > 0 && bucketRebind > bucketUse,
            "the rebind follows the Use() that can clobber the units, never precedes it");

        // Engine state only, and only through public API. Reaching into the client for any of
        // this would tie the water terms to an internal the engine is free to move.
        c.True(renderer.Contains("capi.Render.ShaderUniforms", StringComparison.Ordinal)
            && renderer.Contains("capi.World.Calendar.ReflectColor", StringComparison.Ordinal),
            "the engine's own liquid inputs are read from public API");
    }

    static void HorizonSuppressionWiring(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string mod = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "VintageHorizonsModSystem.cs"));
        string effects = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "Render", "VanillaHorizonEffects.cs"));
        string policy = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "Render", "VanillaHorizonPolicy.cs"));

        int deferral = mod.IndexOf("if (deferringTo != null)", StringComparison.Ordinal);
        int install = mod.IndexOf("new VanillaHorizonEffects", StringComparison.Ordinal);
        c.True(deferral >= 0 && install > deferral,
            "horizon suppression installs only after the competing-LOD deferral return");
        c.True(mod.Contains("horizonEffects?.Dispose()", StringComparison.Ordinal),
            "mod disposal disables and removes horizon suppression");
        c.True(effects.Contains("nameof(ShaderProgramBase.Use)", StringComparison.Ordinal),
            "distance fade is changed once when a vanilla shader becomes active");
        c.False(effects.Contains("nameof(ShaderProgramBase.Uniform)", StringComparison.Ordinal),
            "the per-uniform hot path is not patched");
        foreach (string forbidden in new[]
        {
            "AmbientManager", "AmbientModifier", "BlendedFogDensity",
            "DensityWithoutVanillaBase", "RetainBaseFog",
        })
        {
            c.False((effects + policy).Contains(forbidden, StringComparison.Ordinal),
                "production horizon-effects source contains no " + forbidden + " policy");
        }
        c.True(effects.Contains("HarmonyPatchType.All, HarmonyId", StringComparison.Ordinal),
            "unload removes only Vintage Horizons' own hooks");
        c.False(effects.Contains("viewDistanceLod0", StringComparison.Ordinal),
            "vanilla terrain's independent LOD transition distance is untouched");

        int dispose = effects.IndexOf("public void Dispose()", StringComparison.Ordinal);
        int inert = dispose < 0 ? -1 : effects.IndexOf(
            "Interlocked.CompareExchange(ref active, null, this)", dispose,
            StringComparison.Ordinal);
        int unpatch = dispose < 0 ? -1 : effects.IndexOf(
            "UnpatchInstalledMethods()", dispose, StringComparison.Ordinal);
        c.True(dispose >= 0 && inert > dispose && unpatch > inert,
            "teardown makes terrain-wall callbacks inert before structural unpatching");
    }

    static void CaveCullingDefaultsOn(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "Render", "LodTerrainRenderer.cs"));
        string mod = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "VintageHorizonsModSystem.cs"));

        c.True(renderer.Contains(
                "Environment.GetEnvironmentVariable(\"VINTAGEHORIZONS_CAVE_CULLING\") != \"0\"",
                StringComparison.Ordinal),
            "cave culling starts on unless the environment explicitly disables it");
        c.True(mod.Contains("ChatCommands.Create(\"vhcavecull\")", StringComparison.Ordinal),
            "the cave-culling command uses the explicit .vhcavecull name");
        c.False(mod.Contains("ChatCommands.Create(\"vhcaves\")", StringComparison.Ordinal),
            "the ambiguous old .vhcaves command is retired");
        c.True(mod.Contains("On by default. Changing it rebuilds distant terrain",
                StringComparison.Ordinal),
            "the .vhcavecull help text reports the shipping default");
    }

    /// <summary>
    /// The aggregate subtree bound belongs to the draw walk and nowhere else.
    ///
    /// The trap it is kept out of: `AllVisibleChildrenCovered` does not decide what is
    /// drawn, it decides whether a parent may STOP drawing its own coarse mesh over a
    /// quadrant. The aggregate covers the meshes resident UNDER a child, not the parent's
    /// coarser rendition of the same ground, and those have different vertical extents. A
    /// child holding nothing but a deep cave mesh would be rejected vertically, the parent
    /// would descend believing the quadrant covered, and the surface it had been drawing
    /// there would become a hole - visible only from the angle nobody was testing.
    ///
    /// Passing the span there looks like an obvious consistency fix, which is exactly why
    /// this is pinned rather than left to the comment beside it.
    /// </summary>
    static void SubtreeBoundsStayOutOfTheCoverageGate(Check c)
    {
        string renderer = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot,
            "VintageHorizons", "src", "Render", "LodTerrainRenderer.cs"));

        c.Eq(1, Regex.Matches(renderer, @"SubtreeSpan\(key\)").Count,
            "exactly one traversal site culls a subtree with its aggregate");
        c.Eq(0, Regex.Matches(renderer, @"SubtreeSpan\(ck\)").Count,
            "and the child-coverage gate keeps the full-height box");
        c.True(renderer.Contains("LodHeightSpan subtree = SubtreeSpan(key);", StringComparison.Ordinal),
            "the walk reads the aggregate once per node rather than per test");
    }


    /// <summary>
    /// The indirect path's acceptance gate is that it draws pixels identical to the
    /// established path. That is only a real gate while the two cannot drift: they share
    /// one body file, and the only difference between the programs is one define.
    ///
    /// This holds four things a silent edit could break. That the wrappers differ by the
    /// define alone, so nobody can start "just tweaking" the fast variant. That the
    /// per-section attributes sit at the locations the draw backend points them at, since
    /// a mismatch there addresses the wrong value for every section at once and looks
    /// like a geometry bug. That both stages agree about the two values the vertex stage
    /// forwards. And that the vertex body no longer declares the per-section uniforms
    /// outside their variant block, which would shadow the defines and fail to compile
    /// only in the variant nobody builds by default.
    /// </summary>
    static void IndirectShaderVariant(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "Render", "LodTerrainRenderer.cs"));
        string mod = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "VintageHorizonsModSystem.cs"));
        string bench = File.ReadAllText(Path.Combine(root, "scripts", "bench-windows.ps1"));
        string packedBackend = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "Render", "Gpu", "LodGpuPackedDrawBackend.cs"));
        string injection = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "Render", "Gpu", "LodGpuFailureInjection.cs"));

        c.True(injection.Contains("VINTAGEHORIZONS_GPU_INJECT_FAILURE", StringComparison.Ordinal)
            && renderer.Contains("LodGpuFailureStage.ArenaSetup", StringComparison.Ordinal)
            && renderer.Contains("LodGpuFailureStage.FastShaders", StringComparison.Ordinal)
            && renderer.Contains("LodGpuFailureStage.DepthCopy", StringComparison.Ordinal)
            && renderer.Contains("LodGpuFailureStage.IndirectDraw", StringComparison.Ordinal),
            "Phase 9 can inject each runtime fallback boundary without changing normal defaults");

        // Default ON since 0.3.103, with an explicit environment override that still wins.
        // The direction flipped; the requirement did not. A phase gate is a controlled A/B,
        // a switch that can only be typed in game cannot be pinned for a route, and a
        // comparison whose switches were set by hand is how session 40 lost a run - so the
        // variable must still be able to take either side of the comparison from a script.
        c.True(renderer.Contains("Environment.GetEnvironmentVariable(\"VINTAGEHORIZONS_GPU_INDIRECT\") != \"0\"",
                StringComparison.Ordinal),
            "batched drawing is on unless the environment takes it away");
        // The staging commands are retired as of 0.3.103, and these assert they stay retired.
        // They existed to A/B an experiment; the experiment is now the product, and a switch
        // that can select a half-built stage is a way to file a bug against a configuration
        // nobody ships. The scripted environment overrides above remain the supported way to
        // take either side of a controlled comparison.
        foreach (string retired in new[]
        {
            "vhphase8", "vhindirect", "vhpacked", "vhclusters",
            "vhcull", "vhlate", "vhheight", "vhflicker",
        })
        {
            c.False(mod.Contains($"ChatCommands.Create(\"{retired}\")", StringComparison.Ordinal),
                $"the retired staging command .{retired} is gone");
        }

        // One in-game way back to the established renderer, and it is saved, so a client that
        // misbehaves can be returned to the legacy path without editing a file or restarting.
        c.True(mod.Contains("ChatCommands.Create(\"vhgpu\")", StringComparison.Ordinal),
            "the fast path keeps exactly one in-game off switch");
        c.True(mod.Contains("ChatCommands.Create(\"vhhzb\")", StringComparison.Ordinal),
            "and the depth report the owner reads stays available");
        c.True(bench.Contains("VINTAGEHORIZONS_GPU_INDIRECT", StringComparison.Ordinal)
            && bench.Contains("$GpuIndirect", StringComparison.Ordinal),
            "the benchmark runner can pin either side of the comparison for a whole run");
        c.True(renderer.Contains("VINTAGEHORIZONS_GPU_PACKED", StringComparison.Ordinal)
            && bench.Contains("$GpuPacked", StringComparison.Ordinal),
            "packed drawing keeps its whole-run comparison control");
        c.True(packedBackend.Contains("GL.MultiDrawElementsIndirect(", StringComparison.Ordinal)
            && packedBackend.Contains(
                "GL.BindBuffer(BufferTarget.ElementArrayBuffer, sharedIndexBuffer);",
                StringComparison.Ordinal)
            && !packedBackend.Contains("GL.MultiDrawArraysIndirect(", StringComparison.Ordinal),
            "the packed backend shades four indexed corners rather than six array vertices");

        // Delayed occlusion cannot run under batching, so the pair must stay comparable
        // only when it is off on both sides. The renderer enforces that itself.
        c.True(renderer.Contains("&& !indirectDrawingThisFrame", StringComparison.Ordinal),
            "delayed occlusion is suspended while batched drawing is on, in one place");

        string shaders = Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons", "shaders");

        foreach (string stage in new[] { "vsh", "fsh" })
        {
            string plain = File.ReadAllText(Path.Combine(shaders, "lodterrain." + stage));
            string indirect = File.ReadAllText(Path.Combine(shaders, "lodterrainindirect." + stage));
            string packed = File.ReadAllText(Path.Combine(shaders, "lodterrainpacked." + stage));

            c.True(plain.Contains("#include lodterrainbody." + stage, StringComparison.Ordinal),
                $"the established {stage} program includes the shared body");
            c.True(indirect.Contains("#include lodterrainbody." + stage, StringComparison.Ordinal),
                $"the indirect {stage} program includes the same shared body");
            c.False(plain.Contains("VH_INDIRECT", StringComparison.Ordinal),
                $"the established {stage} program does not define the indirect switch");
            c.True(indirect.Contains("#define VH_INDIRECT 1", StringComparison.Ordinal),
                $"the indirect {stage} program defines the indirect switch");
            c.True(packed.Contains("#include lodterrainbody." + stage, StringComparison.Ordinal)
                && packed.Contains("#define VH_INDIRECT 1", StringComparison.Ordinal)
                && packed.Contains("#define VH_PACKED 1", StringComparison.Ordinal),
                $"the packed {stage} program changes only both shared-body switches");

            // A wrapper is a version line, an extension line, comments, and an include.
            // Anything else in one is shader code that exists in only one variant.
            c.Eq(3, CodeLines(plain),
                $"the established {stage} wrapper is only a version, an extension and an include");
            c.Eq(4, CodeLines(indirect),
                $"the indirect {stage} wrapper adds only the define");
            c.Eq(4, CodeLines(packed),
                $"the packed {stage} wrapper is version, two defines, and the shared include");
        }

        string vertex = ShaderBody("vsh");
        string fragment = ShaderBody("fsh");

        // Attribute locations, against the offsets the draw backend binds them to.
        foreach ((int location, string name) in new[]
        {
            (2, "vhRecordOrigin"), (3, "vhRecordNoise"),
            (4, "vhRecordMask"), (5, "vhRecordOpenEdges"),
        })
        {
            c.True(vertex.Contains($"layout(location = {location}) in ", StringComparison.Ordinal)
                && vertex.Contains(name + ";", StringComparison.Ordinal),
                $"the indirect vertex body reads {name} from attribute {location}");
        }

        // The tint ramp is a texture whose addressing exists in two languages. The C# side is
        // pinned in SeasonWeightChecks; this is the GLSL half. Slot on X and height step on Y,
        // fetched rather than filtered - hardware filtering would blend along X and mix one
        // block's tint into its neighbour's, which is silent and looks like a colour bug.
        c.True(vertex.Contains("uniform sampler2D tintRamp;", StringComparison.Ordinal),
            "the vertex body reads the tint ramp as a texture, not a uniform array");
        c.True(vertex.Contains("texelFetch(tintRamp, ivec2(slot, rampStep), 0)", StringComparison.Ordinal)
            && vertex.Contains("texelFetch(tintRamp, ivec2(slot, rampStep + 1), 0)", StringComparison.Ordinal),
            "the tint ramp is indexed slot-on-X, step-on-Y, and fetched at two adjacent steps");
        foreach (string gone in new[] { "tintsLow", "tintsHigh", "tintYLow", "tintYHigh" })
            c.True(!vertex.Contains(gone, StringComparison.Ordinal),
                $"the replaced two-height tint uniform {gone} is gone from the vertex body");

        c.True(vertex.Contains("flat out float vhColumnBlocks;", StringComparison.Ordinal)
            && fragment.Contains("flat in float vhColumnBlocks;", StringComparison.Ordinal),
            "the column size is handed to the fragment stage as a flat varying");
        c.True(vertex.Contains("flat out ivec2 vhMaskSectionOrigin;", StringComparison.Ordinal)
            && fragment.Contains("flat in ivec2 vhMaskSectionOrigin;", StringComparison.Ordinal),
            "the chunk origin is handed over as flat integers, never interpolated floats");

        // Every per-section uniform must sit inside the variant block. A stray declaration
        // outside it collides with the define and only breaks the indirect build.
        foreach (string declaration in new[]
        {
            "uniform mat4 modelMatrix;", "uniform vec4 noiseOrigin;",
            "uniform vec4 openEdges;", "uniform float sectionSize;",
        })
        {
            c.Eq(1, Occurrences(vertex, declaration),
                $"the vertex body declares {declaration} exactly once, inside the variant block");
        }
        foreach (string declaration in new[]
        {
            "uniform int maskSectionOriginX;", "uniform int maskSectionOriginZ;",
            "uniform float columnBlocks;",
        })
        {
            c.Eq(1, Occurrences(fragment, declaration),
                $"the fragment body declares {declaration} exactly once, inside the variant block");
        }

        // The vertex body branches twice - once to choose uniforms or attributes, once at
        // the end of main to fill the varyings - and the fragment body once. A body that
        // branched in five places would be two shaders again, written in one file.
        c.Eq(2, Occurrences(vertex, "#ifdef VH_INDIRECT"),
            "the vertex body branches on the variant exactly twice");
        c.Eq(1, Occurrences(fragment, "#ifdef VH_INDIRECT"),
            "the fragment body branches on the variant exactly once");
        c.Eq(1, Occurrences(vertex, "#ifndef VH_PACKED"),
            "only the vertex inputs change for packed pulling");
        // Two now: one decodes the pulled quad, one declares the block texture table the
        // pulled path alone can address - only a packed quad carries a block slot. The count
        // is pinned rather than left open so the file cannot drift back into being two
        // shaders written in one place.
        c.Eq(2, Occurrences(vertex, "#ifdef VH_PACKED"),
            "the vertex body has one block to decode the pulled quad and one to declare its table");
        c.Eq(2, Occurrences(fragment, "#ifdef VH_PACKED"),
            "the fragment body has one block to declare the atlas and one to sample it");
        c.True(vertex.Contains("uint vhQuad = uint(gl_VertexID) / 4u;", StringComparison.Ordinal)
            && vertex.Contains("uint vhLogicalCorner = uint(gl_VertexID) % 4u;", StringComparison.Ordinal)
            && vertex.Contains("uint vhBase = vhQuad * 3u;", StringComparison.Ordinal)
            && vertex.Contains("layout(std430, binding = 0) readonly buffer VhPackedQuadBuffer",
                StringComparison.Ordinal),
            "the packed variant pulls three scalar words for four reusable corners");

        int earlyBackface = vertex.IndexOf("bool vhBackface = packedBackfaceCull != 0",
            StringComparison.Ordinal);
        int materialFetch = vertex.IndexOf("texelFetch(blockTable", StringComparison.Ordinal);
        c.True(renderer.Contains(
                "prog.Uniform(\"packedBackfaceCull\", OpaqueBackfaceCulling ? 1 : 0);",
                StringComparison.Ordinal)
            && vertex.Contains("vhFace == 2u && vhRecordOrigin.x + vhX0 <= 0.0",
                StringComparison.Ordinal)
            && vertex.Contains("vhFace == 3u && vhRecordOrigin.x + vhX1 >= 0.0",
                StringComparison.Ordinal)
            && vertex.Contains("vhFace == 4u && vhRecordOrigin.z + vhZ0 <= 0.0",
                StringComparison.Ordinal)
            && vertex.Contains("vhFace == 5u && vhRecordOrigin.z + vhZ1 >= 0.0",
                StringComparison.Ordinal),
            "the packed shader rejects each vertical orientation only from its back side");
        c.True(renderer.Contains("int halfBlockLevels = NearHalfBlockLevels;",
                StringComparison.Ordinal)
            && renderer.Contains("SurfaceHalfBlocksOnly { get; set; } = true;",
                StringComparison.Ordinal)
            && mod.Contains("'surface' is the default low-cost mode throughout level 0, independent of the blend band",
                StringComparison.Ordinal),
            "surface-only slabs are the shipping default throughout level 0, not only the blend band");
        c.True(earlyBackface >= 0 && materialFetch > earlyBackface
            && vertex.IndexOf("return;", earlyBackface, StringComparison.Ordinal) < materialFetch,
            "packed back faces return before block material and shared vertex work");

        // One #else per stage: the declaration block. The second vertex branch only fills
        // the varyings and has no established-path half, and the SSAO branches the engine
        // includes bring in are #if, not #ifdef, so they are not counted here.
        c.Eq(2, Occurrences(vertex, "#else"),
            "only the input and per-section declaration blocks have two halves");
        c.Eq(1, Occurrences(fragment, "#else"), "and the same in the fragment body");
    }

    /// <summary>Non-blank, non-comment lines: what a wrapper actually compiles.</summary>
    static int CodeLines(string source) => source
        .Split('\n')
        .Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

    static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// The shader body, which is where every rule below actually lives. The two programs
    /// under assets/.../shaders are three-line wrappers that include it: one plain, one
    /// with VH_INDIRECT defined. Checks read the body rather than a wrapper, because a
    /// rule that held in only one variant would be worse than no rule at all.
    /// </summary>
    static string ShaderBody(string stage) => File.ReadAllText(Path.Combine(
        GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons",
        "shaderincludes", "lodterrainbody." + stage));

    static void PersistenceCadenceWiring(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        string pipeline = File.ReadAllText(Path.Combine(src, "Lod", "LodPipeline.cs"));
        string storage = File.ReadAllText(Path.Combine(src, "Storage", "LodStorageThread.cs"));
        string renderer = File.ReadAllText(Path.Combine(src, "Render", "LodTerrainRenderer.cs"));
        string assist = File.ReadAllText(Path.Combine(src, "Net", "LodAssistServerSystem.cs"));
        string modSystem = File.ReadAllText(Path.Combine(src, "VintageHorizonsModSystem.cs"));

        c.True(pipeline.Contains("PersistenceCheckpointIntervalMs = 30_000", StringComparison.Ordinal),
            "LOD persistence uses a 30-second checkpoint interval");
        c.True(pipeline.Contains("autoCommitSaves: false", StringComparison.Ordinal),
            "the live pipeline keeps snapshots in RAM until checkpoint publication");
        c.True(storage.Contains("store.SaveBatch(batch)", StringComparison.Ordinal),
            "a published checkpoint uses the batched SQLite path");
        c.True(renderer.Contains("SeasonalRefreshIntervalMs = 30_000", StringComparison.Ordinal),
            "seasonal tint refresh uses a 30-second cadence");
        c.True(renderer.Contains("MeshEvictionChecksPerFrame", StringComparison.Ordinal),
            "mesh eviction is a rolling per-frame queue");
        c.False(renderer.Contains("EvictSweepInterval", StringComparison.Ordinal),
            "the renderer no longer performs periodic full mesh sweeps");
        c.True(renderer.Contains("TemporalOcclusionQueryIssuesPerFrame", StringComparison.Ordinal)
            && renderer.Contains("TemporalOcclusionResultChecksPerFrame", StringComparison.Ordinal),
            "render-context occlusion queries have fixed per-frame issue and result budgets");
        c.True(assist.Contains("MeasureOfferNewKeys(), 30000", StringComparison.Ordinal),
            "server manifest follow-up scans align with the 30-second checkpoint");
        c.True(modSystem.Contains("localOffers.RequestBlob", StringComparison.Ordinal)
            && modSystem.Contains("localOffers.TryTakeBlobResult", StringComparison.Ordinal),
            "singleplayer sibling-cache blob reads use the background I/O worker");
        c.False(modSystem.Contains("localOffers.Blob(", StringComparison.Ordinal),
            "the game tick never performs a synchronous sibling-cache SQLite read");
    }

    /// <summary>
    /// Shader source must be pure ASCII. OpenTK passes managed strings to GL by handing
    /// over a char count where the driver reads utf8 bytes, so a single non-ASCII
    /// character silently truncates the source by (utf8 bytes - chars) characters. The
    /// tail of the shader just disappears; there is no error, only wrong output.
    ///
    /// Scans the whole asset tree rather than a list of known shaders, so a file added
    /// later is covered without anyone remembering to add it here.
    /// </summary>
    /// <summary>
    /// Every tracked text file must decode as UTF-8. Windows tooling defaults to cp1252,
    /// so an editing pass that reads and rewrites a file without naming an encoding turns
    /// one em dash into a lone 0x97 byte. The round trip is byte-preserving for content it
    /// did not touch, which is exactly why it goes unnoticed: the file still opens, the
    /// documentation checks still pass, and only the characters that pass actually
    /// change. Introduced twice in one session before this check existed.
    /// </summary>
    static void TextFilesAreUtf8(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string[] extensions = [".md", ".cs", ".json", ".ps1", ".sh", ".py", ".vsh", ".fsh", ".ash", ".txt"];
        var offenders = new List<string>();
        int scanned = 0;

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(root, path);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || relative.StartsWith(".testdata", StringComparison.Ordinal)
                || relative.StartsWith(".git", StringComparison.Ordinal)
                || relative.StartsWith("dist", StringComparison.Ordinal)
                || relative.StartsWith("NuGetScratch", StringComparison.Ordinal)) continue;

            scanned++;
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                int at = FirstInvalidByte(bytes);
                offenders.Add($"{relative} byte {at} = 0x{bytes[at]:X2}");
            }
        }

        c.SeqEq(Array.Empty<string>(), offenders, $"all {scanned} tracked text files decode as UTF-8");
    }

    static int FirstInvalidByte(byte[] bytes)
    {
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] < 0x80) continue;
            try { strict.GetString(bytes, i, Math.Min(4, bytes.Length - i)); }
            catch (ArgumentException) { return i; }
        }
        return 0;
    }

    static void AsciiOnly(Check c)
    {
        string assets = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets");
        c.True(Directory.Exists(assets), "asset directory exists");

        var offenders = new List<string>();
        int scanned = 0;

        foreach (string path in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
        {
            // Binary assets are exempt: a PNG is full of high bytes by definition.
            if (Path.GetExtension(path) is ".png" or ".jpg" or ".ogg" or ".wav") continue;

            scanned++;
            byte[] bytes = File.ReadAllBytes(path);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] >= 0x80)
                {
                    offenders.Add($"{Path.GetRelativePath(GameAssemblies.RepoRoot, path)} byte {i} = 0x{bytes[i]:X2}");
                    break;
                }
            }
        }

        c.True(scanned > 0, "found asset files to scan");
        c.SeqEq(Array.Empty<string>(), offenders, $"all {scanned} text assets are pure ASCII");
    }

    /// <summary>
    /// The shaders carry their own `const int TINT_SLOTS` because this game version offers
    /// no way to inject a #define, and a mismatch decodes water as opaque and thin plants
    /// as water with no compile error.
    ///
    /// This used to be guarded at shader load by comparing MaxSlots against a second C#
    /// constant that mirrored the shader's value by hand - two constants in the same file,
    /// which cannot detect a shader being edited at all. The compiler said as much: that
    /// branch raised CS0162, unreachable code. Both the mirror and the dead guard are gone.
    ///
    /// Reading the shader files is the only check that can actually close it, and it also
    /// catches the .vsh and .fsh disagreeing with each other, which nothing did before.
    /// </summary>
    static void TintSlotAgreement(Check c)
    {
        string assets = Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons");

        var found = new Dictionary<string, int>();
        foreach (string path in Directory.EnumerateFiles(assets, "*.*sh", SearchOption.AllDirectories))
        {
            Match m = Regex.Match(File.ReadAllText(path), @"const\s+int\s+TINT_SLOTS\s*=\s*(\d+)\s*;");
            if (m.Success) found[Path.GetFileName(path)] = int.Parse(m.Groups[1].Value);
        }

        c.True(found.ContainsKey("lodterrainbody.vsh"), "the vertex body declares TINT_SLOTS");
        c.True(found.ContainsKey("lodterrainbody.fsh"), "the fragment body declares TINT_SLOTS");

        foreach ((string file, int value) in found)
        {
            c.Eq(LodTintRegistry.MaxSlots, value, $"{file} TINT_SLOTS matches LodTintRegistry.MaxSlots");
        }

        c.Eq(1, found.Values.Distinct().Count(), "the vertex and fragment shaders agree with each other");
    }

    /// <summary>
    /// LodMesher packs the tint slot into a vertex alpha byte in three bands: opaque at
    /// slot, water at MaxSlots + slot, thin at MaxSlots * 2 + slot. Alpha is a byte, so the
    /// largest encodable value is MaxSlots * 3 - 1. Raise MaxSlots past 85 and the thin band
    /// wraps into the opaque band with no error anywhere - thin plants would render as solid
    /// terrain of an arbitrary tint.
    /// </summary>
    static void AlphaPacking(Check c)
    {
        c.True(LodTintRegistry.MaxSlots * 3 <= 256,
            $"tint bands fit in a byte (MaxSlots {LodTintRegistry.MaxSlots} * 3 <= 256)");
    }

    /// <summary>
    /// Three rendering regressions are expressible only across the renderer and its two
    /// shader stages: the near handoff must retain cached fallback, the transition must
    /// not deform geometry, and cosmetic noise must use a world-stable coordinate rather
    /// than the camera-relative position required by projection and fog.
    /// </summary>
    static void LodFallbackAndStableColour(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string vertex = ShaderBody("vsh");
        string fragment = ShaderBody("fsh");
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render",
            "LodTerrainRenderer.cs"));

        c.False(fragment.Contains("dist < 0.0", StringComparison.Ordinal),
            "cached terrain is not discarded at the old outer handoff boundary");
        // One shared handoff distance for every cached fragment, water included. Water used to
        // take the larger of this and a cutoff of its own; that cutoff was a circle and is
        // gone. See WaterSurfaceWiring for what replaced it.
        c.True(fragment.Contains(
                "if (radialDistance < cacheHandoffDistance || dist > 1.0) discard;",
                StringComparison.Ordinal),
            "cached terrain hands the conservative close field back to vanilla terrain");
        c.True(renderer.Contains("prog.Uniform(\"cacheHandoffDistance\"", StringComparison.Ordinal),
            "the renderer uploads the conservative near handoff radius");
        // The rule this guards is not "nothing lowers geometry". The transition sink that
        // was removed was eight blocks deep and RAMPED, so it slid visibly as the player
        // walked toward it; what replaced it is a single constant sub-block offset with no
        // ramp and no edge anywhere, which exists to stop two opaque surfaces fighting for
        // depth in the vanilla overlap. So: exactly one place may lower geometry, it must be
        // that uniform on its own, and the renderer must actually upload it - an unuploaded
        // float uniform reads as zero with nothing in the log, the same silent shape as G42.
        c.Eq(1, vertex.Split("worldPos.y -=").Length - 1,
            "exactly one place lowers cached geometry");
        c.True(vertex.Contains("worldPos.y -= bandSinkBlocks;", StringComparison.Ordinal),
            "the only downward offset is the constant sink uniform, with no ramp applied to it");
        c.True(vertex.Contains("uniform float bandSinkBlocks", StringComparison.Ordinal),
            "the sink depth is a uniform rather than a baked shader constant");
        c.True(renderer.Contains("prog.Uniform(\"bandSinkBlocks\"", StringComparison.Ordinal),
            "the renderer uploads the sink depth");

        c.True(vertex.Contains("uniform vec4 noiseOrigin", StringComparison.Ordinal),
            "the vertex shader declares a stable section noise origin");
        c.True(renderer.Contains("prog.Uniform(\"noiseOrigin\"", StringComparison.Ordinal),
            "the renderer uploads the stable section noise origin");
        c.True(fragment.Contains("valuenoise(terrainPos / period)", StringComparison.Ordinal),
            "terrain colour noise uses the stable terrain coordinate");
        c.False(fragment.Contains("valuenoise(worldPos", StringComparison.Ordinal),
            "terrain colour noise never follows the camera-relative render position");

        BlendBandWiring(c, vertex, fragment, renderer);
    }

    /// <summary>
    /// The blend band's uniforms have the same silent failure shape as every other uniform
    /// in this shader pair: GLSL leaves an unwritten one at zero, so a missing upload turns
    /// the band permanently off - or, worse for the float ones, applies a zero-width band
    /// and a black gain - with no compile error and nothing in the log.
    ///
    /// Two facts are pinned beyond the wiring. The cell size the fragment shader divides by
    /// must equal the field's own, or every fragment reads a neighbouring cell's distance;
    /// and the band must not be expressible as a distance from the camera, which is the
    /// property that separates this design from the radial ring that was rejected.
    /// </summary>
    static void BlendBandWiring(Check c, string vertex, string fragment, string renderer)
    {
        string[] uploaded =
        {
            "bandEnabled", "bandDebug", "bandMinCellX", "bandMinCellZ", "bandWidthCells",
            "bandDepthCells", "bandCapacity", "bandWidthBlocks", "bandStepBlocks",
            "bandStrength", "bandInnerBlocks", "bandDarken", "bandSaturate", "bandSpeckle",
        };
        foreach (string name in uploaded)
        {
            c.True(fragment.Contains(name, StringComparison.Ordinal),
                $"the fragment shader reads {name}");
            c.True(renderer.Contains($"prog.Uniform(\"{name}\"", StringComparison.Ordinal),
                $"the renderer uploads {name}");
        }

        c.True(renderer.Contains("prog.BindTexture2D(\"seamField\"", StringComparison.Ordinal),
            "the renderer binds the seam distance field texture");
        c.True(fragment.Contains("texelFetch(seamField", StringComparison.Ordinal),
            "the fragment shader point-samples the seam distance field");

        // The shader reconstructs the field address from an integer section origin in whole
        // vanilla chunks, so it needs both the cells-per-chunk multiplier and the cell size.
        c.Eq(4, VanillaSeamDistanceField.CellsPerChunk,
            "the field stores four cells per vanilla chunk on each axis");
        c.Eq(8, VanillaSeamDistanceField.CellBlocks, "field cells are eight blocks");
        c.True(fragment.Contains("maskSectionOriginX * 4 +", StringComparison.Ordinal),
            "the fragment shader scales the section origin by cells per chunk");
        c.True(fragment.Contains("sectionLocal.x / 8.0", StringComparison.Ordinal),
            "the fragment shader divides by the field's own cell size");

        // Never finer than one world block. A ramp below the world's own grid reads as a GPU
        // effect painted over the terrain rather than as terrain, which is what sank the
        // earlier smooth radial transition.
        c.True(fragment.Contains("max(8.0, bandStepBlocks)", StringComparison.Ordinal),
            "the band's step size can never fall below one stored cell");

        // No radius anywhere in it. The band's shape is vanilla's own footprint.
        c.False(fragment.Contains("radialDistance", StringComparison.Ordinal)
            && fragment.Contains("bandWeight = clamp(1.0 - stepped", StringComparison.Ordinal)
            && fragment.Contains("bandWeight = clamp(radial", StringComparison.Ordinal),
            "the band weight is not a function of distance from the camera");

        // Water keeps its own surface height. A lake surface is a flat reference plane, so an
        // offset applied to it and not to vanilla's reads as a lip running across the water.
        c.True(vertex.Contains("if (slotRaw / TINT_SLOTS != 1) worldPos.y -= bandSinkBlocks;",
                StringComparison.Ordinal),
            "the sink exempts water surfaces");

        BlockTextureWiring(c, vertex, fragment, renderer);
    }

    /// <summary>
    /// Real vanilla block textures. Two failures here are silent and expensive.
    ///
    /// A textured quad carries a block slot in the field its colour would occupy, so the
    /// vertex stage MUST be able to read the table to get that colour back - and it must be
    /// bound whether or not textures are switched on, or turning them off would leave every
    /// textured quad reading its colour out of whatever texture sits on unit zero.
    ///
    /// And meshes must not carry slots before a shader that understands the flag exists, or
    /// the slot number draws as a colour: near-black ground.
    /// </summary>
    static void BlockTextureWiring(Check c, string vertex, string fragment, string renderer)
    {
        string root = GameAssemblies.RepoRoot;
        string pipeline = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Lod",
            "LodPipeline.cs"));
        string modSystem = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "VintageHorizonsModSystem.cs"));

        c.True(vertex.Contains("uniform sampler2D blockTable", StringComparison.Ordinal),
            "the vertex stage declares the block table it reads a textured quad's colour from");
        c.True(vertex.Contains("(vhGeometry & 0x80000000u) != 0u", StringComparison.Ordinal),
            "the vertex stage tests the flag that says word two is a slot");
        c.True(vertex.Contains("flat out vec4 vhBlockRect", StringComparison.Ordinal),
            "the atlas rectangle is carried flat rather than interpolated across the quad");

        // An explicit level, computed from gradients the shader works out itself. Two reasons,
        // and both matter: fract() tears once per block, so a GPU left to infer the level picks
        // the coarsest mip on every block boundary and draws a grid of grey lines; and the
        // level has to be chosen sharper than the footprint asks for, because correct
        // minification averages a distant tile down to its own mean and leaves the texture
        // doing nothing at all.
        c.True(fragment.Contains("textureLod(blockAtlas", StringComparison.Ordinal),
            "the fragment stage samples the atlas at a level it chooses");
        c.True(fragment.Contains("max(mipWanted - blockTextureDetail, 0.0)", StringComparison.Ordinal),
            "that level can reach sharper than the distance warrants");
        c.False(fragment.Contains("texture(blockAtlas", StringComparison.Ordinal),
            "the atlas is never sampled with inferred derivatives");

        foreach (string name in new[]
        {
            "blockTextureEnabled", "blockTextureStrength", "blockTextureMipLimit",
            "blockTextureDetail", "blockAtlasSize",
        })
        {
            c.True(fragment.Contains(name, StringComparison.Ordinal),
                $"the fragment shader reads {name}");
            c.True(renderer.Contains($"prog.Uniform(\"{name}\"", StringComparison.Ordinal),
                $"the renderer uploads {name}");
        }

        c.True(renderer.Contains("prog.BindTexture2D(\"blockTable\"", StringComparison.Ordinal),
            "the renderer binds the block table");
        c.True(renderer.Contains("prog.BindTexture2D(\"blockAtlas\"", StringComparison.Ordinal),
            "the renderer binds the game's own block atlas");
        c.True(renderer.Contains("if (blockTexturesLive && blockTableTexture != 0)", StringComparison.Ordinal),
            "the table is bound independently of whether textures are switched on");

        // Emission is an immutable job fact gated on the table reaching the GPU. The matching
        // revision check prevents a job queued before a live toggle from publishing afterward.
        c.True(renderer.Contains("EmitTextureSlots = includeTextureSlots", StringComparison.Ordinal),
            "each mesh job snapshots whether texture slots are ready");
        c.True(renderer.Contains("TextureModeRevision = textureModeRevision", StringComparison.Ordinal),
            "each mesh job snapshots the texture-mode identity");
        c.True(renderer.Contains("result.TextureModeRevision != textureModeRevision",
                StringComparison.Ordinal),
            "a result from the old side of the blend toggle is rejected");
        c.True(renderer.Contains("WorldEpoch = currentWorldEpoch()", StringComparison.Ordinal)
            && renderer.Contains("result.WorldEpoch != currentEpoch", StringComparison.Ordinal),
            "mesh work carries world identity and stale-world results are rejected");
        c.True(renderer.Contains("LodWorld.KeyLevel(key) != 0", StringComparison.Ordinal),
            "the toggle remesh is restricted to L0, where texture slots can exist");
        c.True(renderer.Contains("liveTexturedMeshes > 0", StringComparison.Ordinal)
            && renderer.Contains("DisposeBlockTableTexture();", StringComparison.Ordinal),
            "the block table survives the transition and is released after the last textured mesh");
        c.True(renderer.Contains("replacementTexture = GL.GenTexture()", StringComparison.Ordinal)
            && renderer.Contains("blockTableTexture = replacementTexture", StringComparison.Ordinal)
            && renderer.Contains("blockTexturesLive = blockTableTexture != 0 && blockTexturesLive",
                StringComparison.Ordinal),
            "a failed table refresh preserves the previous live table until its meshes retire");

        c.True(pipeline.Contains("tintSlot, ResolveTextureSlot(block)", StringComparison.Ordinal)
            && pipeline.Contains("TextureSlotsEnabled && block != null", StringComparison.Ordinal),
            "live palette registration resolves slots only while the pipeline is enabled");
        c.True(renderer.Contains("SectionSnapshot.Of(section, includeTextureSlots)",
                StringComparison.Ordinal)
            && renderer.Contains("EstimateRetainedBytes(section, includeTextureSlots)",
                StringComparison.Ordinal),
            "the renderer omits the texture-slot snapshot array while the pipeline is off");
        c.True(modSystem.Contains("textureSlotsEnabled: config.BlendPipeline", StringComparison.Ordinal)
            && modSystem.Contains("pipeline.SetTextureSlotsEnabled", StringComparison.Ordinal)
            && modSystem.Contains("renderer.TextureSlotSourceReady = pipeline.TextureSlotsPrepared",
                StringComparison.Ordinal),
            "the saved master switch gates palette work and delays textured publication until ready");
        c.True(modSystem.Contains("renderer.ResetBlockTextureStateForWorld();", StringComparison.Ordinal)
            && modSystem.Contains("blockTextures.Clear();", StringComparison.Ordinal)
            && pipeline.Contains("Worker.ClearMeshWork();", StringComparison.Ordinal),
            "world leave clears queued mesh work, render-table state, and the block-slot registry");
    }

    /// <summary>
    /// The four lighting corrections are each an `int` uniform read by the fragment shader
    /// and written by the renderer. A missing upload is silent: GLSL leaves the uniform at
    /// zero, so the switch reads permanently off and the correction simply never happens,
    /// with no compile error and nothing in the log. Same failure shape as G42.
    ///
    /// The formulas are pinned as literals because they are transcriptions of the engine's
    /// own `fogandlight.fsh` and `chunkopaque.vsh`, not values of ours to tune. If a game
    /// update changes vanilla's numbers, this check will not notice - but a silent edit on
    /// our side, which is the likelier accident, fails here.
    /// </summary>
    static void VanillaLightingWiring(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string fragment = ShaderBody("fsh");
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render",
            "LodTerrainRenderer.cs"));

        foreach (string name in new[] { "flatTopLight", "lightMoonDir", "lightVanillaRamp",
                                        "lightAmbientColor", "lightDayBoost", "lightSkyDayLight" })
        {
            c.True(fragment.Contains($"uniform int {name};", StringComparison.Ordinal),
                $"the fragment shader declares the {name} switch");
            c.True(renderer.Contains($"prog.Uniform(\"{name}\"", StringComparison.Ordinal),
                $"the renderer uploads the {name} switch");
        }

        // The ambient colour is the only one of vanilla's lighting inputs the engine does
        // not already push into any program including fogandlight.fsh.
        c.True(fragment.Contains("uniform vec3 rgbaAmbientIn;", StringComparison.Ordinal),
            "the fragment shader declares vanilla's ambient colour");
        c.True(renderer.Contains("prog.Uniform(\"rgbaAmbientIn\", capi.Ambient.BlendedAmbientColor)",
                StringComparison.Ordinal),
            "the renderer uploads the engine's blended ambient colour unaltered");

        // chunkopaque.vsh: nb = max(max(intensity, 0.5 + 0.5 * dot(normal, lightPosition)),
        // normal.y * 0.95), with intensity 0.45 when shadows are off - which is the case a
        // LOD section is always in, having no shadow map.
        c.True(fragment.Contains("max(0.45, 0.5 + 0.5 * sunAngle)", StringComparison.Ordinal),
            "the shade ramp and floor are vanilla's");
        c.True(fragment.Contains("clamp(normal.y, 0.0, 1.0) * 0.95", StringComparison.Ordinal),
            "the up-facing floor is vanilla's");

        // fogandlight.vsh applyLight, reduced for sky light and no block light. The
        // reduction this check used to encode dropped the sun light level as though it
        // cancelled: it does not. bMax renormalisation cancels the ambient colour's own
        // mean, and `lightColor.a` survives it, so vanilla's terrain is
        // `1.05 * lightColor.a * ambientColor`. Reading that factor as 1 made every cached
        // surface 1.24x too bright at every hour; the assertion is spelled out here because
        // a check that agrees with the bug is worse than no check.
        c.True(fragment.Contains("1.05 * sunLightLevel * rgbaAmbientIn", StringComparison.Ordinal),
            "the light colour is vanilla's ambient times its contrast constant and the sun light level");

        // fogandlight.fsh applyFogAndShadowFromBrightness, verbatim.
        c.True(fragment.Contains("1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5",
                StringComparison.Ordinal),
            "the daylight brightening matches vanilla's");

        // SystemRenderSky.OnRenderFrame3D, transcribed. DefaultShaderUniforms.SkyDaylight is
        // internal, so the value the engine computed cannot be read; only rebuilt. Every
        // engine shader that calls getSkyColorAt passes this, never DayLightStrength.
        c.True(renderer.Contains("1.25f * Math.Max(calendar.DayLightStrength - calendar.MoonLightStrength / 2f, 0.05f)",
                StringComparison.Ordinal),
            "the sky daylight reconstruction matches the engine's formula");
        c.True(renderer.Contains("capi.World.Player.Entity.Pos.Y - capi.World.SeaLevel - 1000.0) / 30000.0",
                StringComparison.Ordinal),
            "the sky daylight reconstruction keeps the engine's high-altitude attenuation");
        c.True(fragment.Contains("clamp(skyLight, 0.0, 1.0), horizonFog", StringComparison.Ordinal),
            "the far sky fade uses the corrected sky daylight");
        // The mod's own night glow dimming is calibrated against DayLightStrength, which
        // reaches zero; sky daylight floors at 0.0625 and would leave a glow burning.
        c.True(fragment.Contains("skyGlow.y *= clamp((dayLight - 0.05)", StringComparison.Ordinal),
            "the glow clamp keeps the daylight strength it was calibrated against");

        // These two arrive free with the fogandlight.fsh include; uploading them again
        // would be harmless but would hide the fact that the engine already owns them.
        c.False(renderer.Contains("prog.Uniform(\"lightPosition\"", StringComparison.Ordinal)
            || renderer.Contains("prog.Uniform(\"shadowIntensity\"", StringComparison.Ordinal),
            "the light vector and shadow intensity are left to the engine's own upload");
    }

    /// <summary>
    /// The shader load path must be able to say that it WORKED, not only that it failed.
    ///
    /// <para>
    /// G68 established this in session 42 and only half of it was built: the indirect variant
    /// reported its success while the main program - the one whose failure means no distant
    /// terrain at all - could only ever log a failure. The engine replays the expected
    /// first-attempt link errors in its own startup summary, so a reader sees three shader
    /// errors and no statement that anything compiled, and concludes the shaders are broken.
    /// That has cost reading time twice now, which is exactly the cost G68 exists to prevent.
    /// </para>
    /// <para>
    /// Asserted structurally rather than by wording, so the sentence can be rewritten freely:
    /// what must survive is that a success path exists, that it is gated to fire once, and
    /// that the first provisional attempt still cannot report a failure at full volume.
    /// </para>
    /// </summary>
    static void ShaderLoadReportsSuccessNotOnlyFailure(Check c)
    {
        string renderer = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render", "LodTerrainRenderer.cs"));

        // Bounded by the next member rather than by a character count. The first version of
        // this used a 6000-character window and the assertion it needed sat at 6028 - a check
        // that fails for a reason having nothing to do with what it is checking.
        int load = renderer.IndexOf("public bool LoadShader()", StringComparison.Ordinal);
        int end = renderer.IndexOf("void WarnIfIncludeMissing(", StringComparison.Ordinal);
        c.True(load > 0 && end > load, "the shader load path is identifiable");
        if (load <= 0 || end <= load) return;
        string body = renderer.Substring(load, end - load);

        c.True(body.Contains("shaderOk && !reportedShadersOk", StringComparison.Ordinal),
            "the main shader program reports its success, once, and not only its failure");
        c.True(body.Contains("shaders ready after", StringComparison.Ordinal),
            "and the report says so in a line a reader can search for");
        c.True(body.Contains("reportedShadersOk = true", StringComparison.Ordinal),
            "the success report is latched so a reload does not repeat it every time");

        // Re-armed on a real failure: a driver that breaks on one reload and recovers on the
        // next has to say so, or the log's last word on shaders is a stale success.
        c.True(body.Contains("if (!shaderOk && !ShaderLoadIsProvisional) reportedShadersOk = false;",
            StringComparison.Ordinal),
            "a genuine failure re-arms the report so a later recovery is announced");

        // The other half of G68, which was built and must stay: the first attempt runs before
        // the engine has filled its include table, always fails, and must not shout about it.
        c.True(renderer.Contains("bool ShaderLoadIsProvisional => shaderLoadAttempts <= 1",
            StringComparison.Ordinal),
            "the first shader attempt is still treated as provisional");
        c.True(body.Contains("if (!shaderOk && !ShaderLoadIsProvisional)\n            capi.Logger.Error",
                StringComparison.Ordinal)
            || Regex.IsMatch(body, @"if \(!shaderOk && !ShaderLoadIsProvisional\)\s*capi\.Logger\.Error"),
            "and the failure is reported at full volume only from the second attempt on");

        // `scripts/check-log.py` treats the failure sentence as fatal by its mere presence, so
        // the wording of THAT one is load-bearing and is pinned here rather than left to luck.
        c.True(renderer.Contains("lodterrain shader failed to compile", StringComparison.Ordinal),
            "the failure sentence the log scanner greps for is unchanged");
    }

    static void ReadinessShadowWiring(Check c)
    {
        string renderDir = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render");
        string renderer = File.ReadAllText(Path.Combine(renderDir, "LodTerrainRenderer.cs"));
        string model = File.ReadAllText(Path.Combine(renderDir, "VanillaRenderReadiness.cs"));

        c.True(renderer.Contains("capi.Event.ChunkDirty += OnReadinessChunkDirty", StringComparison.Ordinal),
            "the shadow tracker receives event-fed client chunk candidates");
        c.True(renderer.Contains("capi.Event.ChunkDirty -= OnReadinessChunkDirty", StringComparison.Ordinal),
            "renderer teardown unsubscribes the readiness event");
        c.True(renderer.Contains("capi.IsChunkRendered(readinessProbePos)", StringComparison.Ordinal),
            "readiness probes use the supported public engine query");
        // Emptiness may be counted and must never decide ownership: a column counts as
        // owned only when every one of its chunks does, and every column has sky, so
        // refusing air its cell removes ownership from the entire world at once (G40).
        // Returning void is the structural half of that guarantee - a measurement with no
        // result cannot be branched on, whatever a later edit intends.
        c.True(Regex.IsMatch(renderer, @"void MeasureDrawnChunk\(VanillaChunkCell cell"),
            "the empty-chunk question is measured rather than acted on");
        c.True(Regex.IsMatch(renderer,
                @"if \(rendered && atOwnershipDecision\)\s*\{[^}]*GetChunk\(cell\.X, cell\.Y, cell\.Z\)"),
            "the measurement runs only for chunks the engine claims it drew");
        // The chunk lookup takes the client's chunk lock. Doing it for every probe doubles
        // that traffic on the render thread while a world is loading, which is when the
        // probe queue is longest and the loader threads want the same lock.
        c.True(renderer.Contains("bool atOwnershipDecision = decisionState is VanillaReadinessState.ObservedRendered",
            StringComparison.Ordinal),
            "the chunk lookup happens only where an observation can change ownership");
        // ONE lookup for the whole decision. Three rules need the chunk - the measurement, the
        // exact range clause and the geometry rule - and each used to fetch it for itself, so
        // a single ownership decision could take that lock three times. They share it now, and
        // the sharing is structural: both consumers take the chunk as a parameter, so neither
        // can quietly reintroduce a lookup of its own (G45). Counted inside the probe loop
        // only: the standing diagnostics look chunks up too, and they run when a player asks.
        int probeLoop = renderer.IndexOf("while (probed < maxProbes)", StringComparison.Ordinal);
        int probeEnd = renderer.IndexOf("SweepRangeLosses(frameCounter);", StringComparison.Ordinal);
        c.True(probeLoop > 0 && probeEnd > probeLoop, "the probe loop is identifiable");
        string probeBody = probeLoop > 0 && probeEnd > probeLoop
            ? renderer.Substring(probeLoop, probeEnd - probeLoop)
            : string.Empty;
        c.Eq(1, CountOccurrences(probeBody, "GetChunk(cell.X, cell.Y, cell.Z)"),
            "the probe loop takes the client's chunk lock at most once per ownership decision");
        c.True(Regex.IsMatch(renderer, @"void MeasureDrawnChunk\(VanillaChunkCell cell, IWorldChunk\? chunk"),
            "the measurement is handed the chunk rather than fetching its own");
        c.True(Regex.IsMatch(renderer, @"bool VanillaChunkHoldsDrawableGround\(IWorldChunk\? chunk\)"),
            "the geometry rule is handed the chunk rather than fetching its own");

        ExactDrawRangeWiring(c, renderer, model,
            File.ReadAllText(Path.Combine(renderDir, "VanillaChunkGeometry.cs")));
        c.False(renderer.Contains("IsVanillaChunkEmpty(cell)) rendered = false", StringComparison.Ordinal),
            "an empty-chunk reading cannot decide ownership");
        c.True(renderer.Contains("ReadinessProbeMaxItemsPerFrame", StringComparison.Ordinal)
            && renderer.Contains("ReadinessProbeMaxMillisecondsPerFrame", StringComparison.Ordinal),
            "readiness probing has both item and elapsed-time ceilings");
        c.True(renderer.Contains("ReadinessProbeCatchUpItemsPerFrame", StringComparison.Ordinal)
            && renderer.Contains("ReadinessProbeCatchUpMillisecondsPerFrame", StringComparison.Ordinal),
            "a deep queue raises both ceilings rather than only the item count");
        c.True(renderer.Contains("readiness.PendingCandidates >= ReadinessProbeCatchUpQueueDepth", StringComparison.Ordinal),
            "the raised budget is entered only on measured backlog");
        c.True(model.Contains("PromoteObserved", StringComparison.Ordinal),
            "first true observations wait in a deferred render-frame queue");
        // Classification now reaches the draw path, but only to skip a section the mask
        // would have discarded entirely, and only while the mask is live. A skip decided
        // without the texture behind it would remove terrain the GPU still had to draw.
        c.True(renderer.Contains("readiness.Classify(key) != VanillaSectionOwnership.VanillaOnly", StringComparison.Ordinal),
            "only a wholly vanilla-owned section is skipped");
        int skipMethod = renderer.IndexOf("bool SkipVanillaOwnedSection(long key)", StringComparison.Ordinal);
        c.True(skipMethod > 0, "the whole-mesh skip has its own guard");
        string skipBody = skipMethod > 0
            ? renderer.Substring(skipMethod, Math.Min(420, renderer.Length - skipMethod))
            : string.Empty;
        c.True(skipBody.Contains("if (!readinessMaskActive", StringComparison.Ordinal),
            "a section is never skipped unless the GPU mask is live");
        c.Eq(2, CountOccurrences(renderer, "if (SkipVanillaOwnedSection(key)) continue;"),
            "both opaque comparison paths share the same skip decision");
        // Opaque only, still. Ownership hides cached water per fragment and only inside
        // waterMaskRadius, because the engine's liquid alpha can reach zero before its ground
        // stops drawing and cached water has to keep filling that outer ring - G124's dry
        // riverbeds. A whole-section decision cannot express that; the per-fragment test can.
        int waterMethod = renderer.IndexOf("void DrawLegacyWater()", StringComparison.Ordinal);
        c.True(waterMethod > 0, "the water pass has its own submission loop");
        string waterBody = waterMethod > 0
            ? renderer.Substring(waterMethod, Math.Min(3200, renderer.Length - waterMethod))
            : string.Empty;
        c.False(waterBody.Contains("SkipVanillaOwnedSection", StringComparison.Ordinal),
            "water submission is never skipped whole for vanilla ownership");

        // The root fix for cached water hiding vanilla's, and the one thing in this file that
        // cannot be observed anywhere else. This mod draws in the Opaque stage; the engine
        // draws its water in the LATER OIT stage with depth writes already off, testing
        // against the depth this stage left behind. A cached water fragment that writes depth
        // therefore depth-rejects the real animated surface, silently, with nothing missing
        // and nothing in the log - G126. Both halves are pinned: an unbalanced disable would
        // leak into every renderer registered after this one.
        c.True(waterBody.Contains("GL.DepthMask(false);", StringComparison.Ordinal),
            "the water pass disables depth writes so vanilla's later water pass survives it");
        c.True(waterBody.Contains("GL.DepthMask(true);", StringComparison.Ordinal),
            "and restores them before returning");
        int disable = waterBody.IndexOf("GL.DepthMask(false);", StringComparison.Ordinal);
        int restore = waterBody.IndexOf("GL.DepthMask(true);", StringComparison.Ordinal);
        c.True(disable > 0 && restore > disable, "in that order");
        c.True(waterBody.Contains("finally", StringComparison.Ordinal)
            && restore > waterBody.IndexOf("finally", StringComparison.Ordinal),
            "and the restore is in the finally block, so a throwing draw cannot leak it");
        c.False(skipBody.Contains("Evict", StringComparison.Ordinal)
            || skipBody.Contains("RenderDirty", StringComparison.Ordinal)
            || skipBody.Contains("Remove", StringComparison.Ordinal),
            "skipping a draw does not touch residency or dirty obligations");
        c.True(renderer.Contains("nearHandoff.Update(", StringComparison.Ordinal)
            && renderer.Contains("readinessOwnsHandoff", StringComparison.Ordinal),
            "the near handoff is driven by measured readiness with an explicit fallback flag");
        c.True(renderer.Contains("LodNearHandoff.InnerDiscardRadius(viewDistance)", StringComparison.Ordinal),
            "the established radial constant remains available when readiness is unavailable");
    }

    /// <summary>
    /// Structural guarantees for the exact draw-range rule. Every one of these is a property
    /// no runtime check can see failing: the mod would simply own slightly the wrong ground,
    /// and the only symptom would be a seam somebody has to be standing at.
    /// </summary>
    static void ExactDrawRangeWiring(Check c, string renderer, string model, string geometry)
    {
        // Public API, not reflection. The culler is reachable through IRenderAPI, so if this
        // ever became a private-field read it would be a silent supported-version hazard for
        // a value that has a supported accessor.
        c.True(renderer.Contains("capi.Render.DefaultFrustumCuller", StringComparison.Ordinal),
            "the engine's own culler is read through the supported render API");
        c.False(renderer.Contains("GetField(\"frustumCuller\"", StringComparison.Ordinal),
            "and never by reaching into ClientMain for it");

        // The anchor and the distance are the two values this rule gets from somewhere other
        // than the rest of the renderer, and both differences are deliberate. Pinning them
        // stops a later tidy-up from "unifying" them with camPos and ApprovedViewDistance,
        // which would silently reintroduce a third-person offset and a server-capped bound.
        int capture = renderer.IndexOf("VanillaDrawRangeState CaptureDrawRange()", StringComparison.Ordinal);
        c.True(capture > 0, "the frame's range inputs are captured in one place");
        string captureBody = capture > 0
            ? renderer.Substring(capture, Math.Min(1200, renderer.Length - capture))
            : string.Empty;
        c.True(captureBody.Contains("culler.ViewDistanceSq", StringComparison.Ordinal),
            "the bound is the culler's own view distance, not the server-approved one");
        c.False(captureBody.Contains("ApprovedViewDistance()", StringComparison.Ordinal),
            "the server-approved distance does not decide what the engine draws");
        c.True(captureBody.Contains("capi.World.Player.Entity.Pos", StringComparison.Ordinal),
            "the anchor is the player's position, which is what the engine measures from");
        c.False(captureBody.Contains("camPos", StringComparison.Ordinal),
            "the camera does not decide it: in third person the two are blocks apart");

        // Read once per frame. Reading it per decision would let the player cross a block
        // boundary mid-batch and settle two halves of one frontier against two anchors.
        c.Eq(1, CountOccurrences(renderer, "drawRange = CaptureDrawRange();"),
            "the range inputs are captured exactly once per frame");

        // The fallback is the whole safety story. If the binding, the culler or the switch is
        // unavailable, every cell must land on 0.3.152's conservative bound rather than on a
        // guess - so the bounded call has to still exist and still carry the overlap margin.
        c.True(renderer.Contains("VanillaRenderReadiness.BeyondVanillaDrawRange(\n                            cell.X, cell.Z, camPos.X, camPos.Z, viewDistance, OverlapMarginBlocks)",
                StringComparison.Ordinal)
            || Regex.IsMatch(renderer,
                @"BeyondVanillaDrawRange\(\s*cell\.X, cell\.Z, camPos\.X, camPos\.Z, viewDistance, OverlapMarginBlocks\)"),
            "an unreadable chunk falls back to the conservative column bound");
        c.True(renderer.Contains("bool ExactDrawRange { get; set; } = true", StringComparison.Ordinal),
            "the exact rule is a live switch, so the two seams can be compared in one place");

        // The deadlock, pinned shut. A first sighting commits nothing, so refusing it achieves
        // nothing - but it leaves the cell Pending, and a Pending cell is not an ownership
        // decision, so it never reaches the branch that reads the real sphere. The inaccurate
        // rule then permanently prevents the accurate one from ever running.
        //
        // Shipped in 0.3.154 and measured in 0.3.157: at a 64-block vanilla view distance the
        // bounded gate is `64 - 46` = 18 blocks, and chunks 28 blocks away that the engine was
        // plainly drawing sat Pending forever. At 32 blocks the gate is zero and no cell can
        // ever be owned at all. The range block must stay inside the decision guard.
        c.True(renderer.Contains("if (rendered && ChunkMaskEnabled && atOwnershipDecision)",
            StringComparison.Ordinal),
            "the range rule runs only where an observation can change ownership");
        c.False(Regex.IsMatch(renderer,
                @"bool exact = ExactDrawRange && atOwnershipDecision"),
            "and the exact branch is not separately gated on it, which is what created the deadlock");

        // The counter every probe reads first, and the one the all-signals report omitted while
        // two sessions were spent reasoning about which gate had refused a chunk.
        c.True(geometry.Contains("drawn={drawn}", StringComparison.Ordinal),
            "the all-signals report prints the drawn counter that IsChunkRendered reads");

        // Fast loss, cautious gain. The sweep may only ever withdraw ownership: granting from
        // stored state would hand ground to vanilla without re-confirming the mesh, the hide
        // flag or the culler, which are the other three terms of the engine's predicate.
        int sweep = renderer.IndexOf("void SweepRangeLosses(long frameCounter)", StringComparison.Ordinal);
        c.True(sweep > 0, "the per-frame range sweep exists");
        string sweepBody = sweep > 0
            ? renderer.Substring(sweep, Math.Min(1800, renderer.Length - sweep))
            : string.Empty;
        c.True(sweepBody.Contains("rendered: false", StringComparison.Ordinal),
            "the sweep only ever proposes loss");
        c.False(sweepBody.Contains("rendered: true", StringComparison.Ordinal),
            "the sweep never grants ownership from stored state");
        c.True(sweepBody.Contains("ResolvePublication(publication, accepted: true)", StringComparison.Ordinal),
            "sweep losses go through the same atomic publication handshake as probe losses");
        c.True(sweepBody.Contains("drawRange.DiffersFrom(lastSweptDrawRange)", StringComparison.Ordinal),
            "the sweep runs only when one of the engine's quantised inputs actually moved");

        // Whole-set, never cursored. A cursored revalidation that fails to reach ground the
        // camera moved away from is exactly G39, and it left the mask hiding cached terrain
        // nothing else drew.
        c.False(sweepBody.Contains("cursor", StringComparison.Ordinal),
            "the sweep covers the whole committed set rather than a moving slice of it");

        // The ring is mirrored state, so a slot changing hands has to forget it - the same
        // shape of bug as G44, with the sphere as the payload instead of the ownership bit.
        int clearSlot = model.IndexOf("void ClearSlot(int slot)", StringComparison.Ordinal);
        c.True(clearSlot > 0, "the tracker funnels every wipe through one method");
        string clearBody = clearSlot > 0
            ? model.Substring(clearSlot, Math.Min(2400, model.Length - clearSlot))
            : string.Empty;
        c.True(clearBody.Contains("Array.Clear(lodMasks, baseIndex, verticalChunks)", StringComparison.Ordinal)
            && clearBody.Contains("Array.Fill(sphereCentreX, float.NaN, baseIndex, verticalChunks)", StringComparison.Ordinal)
            && clearBody.Contains("Array.Fill(sphereCentreZ, float.NaN, baseIndex, verticalChunks)", StringComparison.Ordinal),
            "an evicted ring slot forgets the sphere it was holding");

        // Everything derived from the refused ring has to follow it to zero, or it describes a
        // boundary that is no longer there. Water is the one that shows.
        c.True(renderer.Contains("float RefusedOwnershipRingBlocks()", StringComparison.Ordinal),
            "the refused ring has one definition");
        int ring = renderer.IndexOf("float RefusedOwnershipRingBlocks()", StringComparison.Ordinal);
        string ringBody = ring > 0
            ? renderer.Substring(ring, Math.Min(400, renderer.Length - ring))
            : string.Empty;
        // Under the exact rule the ring is the early handover ALONE - the 46 blocks of
        // sphere-placement slack are what reading the sphere deletes, and the margin covers a
        // different thing (an unload a probe has not caught up with yet) that reading the
        // sphere does not answer.
        c.True(ringBody.Contains("ExactDrawRange && drawRange.Available && VanillaChunkGeometry.Available", StringComparison.Ordinal)
            && ringBody.Contains("return margin", StringComparison.Ordinal),
            "the exact rule drops the sphere slack and keeps only the early handover");
        c.True(ringBody.Contains("VanillaRenderReadiness.SphereCentreSlackBlocks + margin", StringComparison.Ordinal),
            "and the fallback still carries both");
        c.True(renderer.Contains("waterFootprintDilation != dilation", StringComparison.Ordinal),
            "a change to the growth rebuilds the water footprint without waiting for ownership to move");

        // The band's inner offset is the other thing derived from the ring, and the one that
        // shows immediately: it is how far the blend starts INSIDE the seam a player sees, so
        // a fixed 46 under the exact rule would start the blend 46 blocks past the seam it
        // exists to hide.
        c.True(renderer.Contains("bandInnerBlocks >= 0f ? bandInnerBlocks : RefusedOwnershipRingBlocks()",
            StringComparison.Ordinal),
            "the band's inner offset follows the refused ring unless it is pinned");
        c.True(renderer.Contains("float reach = Math.Max(0f, BandWidthBlocks) + Math.Max(0f, BandInnerBlocks)",
            StringComparison.Ordinal),
            "and the seam field's margin is sized from the offset that is actually in force");
        c.True(renderer.Contains("seamFieldBuiltMarginCells != marginCells", StringComparison.Ordinal),
            "a changed margin rebuilds the seam field rather than serving distances for the old reach");

        // The derived consumers must not keep their own copy of the arithmetic. Water's growth
        // had one, and a copy is how a derived quantity keeps describing a boundary after the
        // boundary has moved.
        int dilation = renderer.IndexOf("int WaterFootprintDilationChunks()", StringComparison.Ordinal);
        c.True(dilation > 0, "water's growth has one definition");
        string dilationBody = dilation > 0
            ? renderer.Substring(dilation, Math.Min(500, renderer.Length - dilation))
            : string.Empty;
        c.True(dilationBody.Contains("RefusedOwnershipRingBlocks()", StringComparison.Ordinal),
            "water's growth is derived from the ring rather than recomputing it");
        c.False(dilationBody.Contains("SphereCentreSlackBlocks", StringComparison.Ordinal),
            "and keeps no copy of the slack it used to add up itself");
    }

    /// <summary>
    /// The shader reconstructs the same wrapped atlas address that C# writes. Nothing at
    /// runtime can catch a divergence - it would simply read another chunk's ownership and
    /// hide the wrong ground - so the two addressings are held together here, along with
    /// the ordering and fallback rules the mask depends on.
    /// </summary>
    static void OwnershipMaskWiring(Check c)
    {
        string renderDir = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render");
        string renderer = File.ReadAllText(Path.Combine(renderDir, "LodTerrainRenderer.cs"));
        string maskSource = File.ReadAllText(Path.Combine(renderDir, "VanillaReadinessMask.cs"));
        string fragment = ShaderBody("fsh");

        // Row = wrapped Z plus a whole capacity-sized block per Y level; column = wrapped X.
        c.True(maskSource.Contains("int row = (chunkZ & mask) + chunkY * capacity;", StringComparison.Ordinal)
            && maskSource.Contains("return row * capacity + (chunkX & mask);", StringComparison.Ordinal),
            "the mask addresses a cell as wrapped X across a Y-stacked wrapped Z row");
        c.True(fragment.Contains("ivec2(cellX & wrap, (cellZ & wrap) + cellY * maskCapacity)", StringComparison.Ordinal),
            "the shader reconstructs that same wrapped address");
        c.True(fragment.Contains("int wrap = maskCapacity - 1;", StringComparison.Ordinal),
            "the shader wraps with the same power-of-two mask the ring uses");

        // Ownership must come from an integer section origin plus a small local offset. A
        // summed world coordinate is not exact in float32: at 512k blocks a fragment 0.03
        // blocks below a chunk edge rounds onto the next chunk and takes its ownership.
        c.True(fragment.Contains("maskSectionOriginX + int(floor((sectionLocal.x", StringComparison.Ordinal)
            && fragment.Contains("maskSectionOriginZ + int(floor((sectionLocal.z", StringComparison.Ordinal),
            "ownership addressing uses an integer section origin plus the section-local offset");
        c.False(fragment.Contains("floor(terrainPos.x / 32.0)", StringComparison.Ordinal),
            "ownership is never derived from a summed world coordinate");

        // A wall is emitted IN the plane between two ownership cells, at its own block's
        // face - the near edge going west or north, the FAR edge going east or south. So an
        // east wall standing on a chunk boundary is drawn at the next chunk's origin, and a
        // plain floor() charges it to a neighbour that may not hold its block at all. Where
        // that neighbour was owned and the wall's own chunk was not, the wall was discarded
        // and vanilla drew nothing in its place. Asking every candidate is what closes it,
        // and nothing at runtime can catch a regression here: the picture simply loses a
        // face, on two of the four sides of the seam, wherever the ground steps down across
        // one particular plane.
        c.True(fragment.Contains("float maskPlaneOffset(float localBlocks)", StringComparison.Ordinal)
            && fragment.Contains("return localBlocks - 32.0 * floor(localBlocks / 32.0 + 0.5);", StringComparison.Ordinal),
            "the shader measures how far a fragment is from the nearest chunk plane");
        c.True(fragment.Contains("int edgeX = abs(planeOffsetX) < MASK_PLANE_SLACK ? 1 : 0;", StringComparison.Ordinal)
            && fragment.Contains("int edgeZ = abs(planeOffsetZ) < MASK_PLANE_SLACK ? 1 : 0;", StringComparison.Ordinal),
            "and records that a fragment lying in one has two candidate cells, not one");
        c.True(fragment.Contains("if (owned && edgeX == 1) owned = vanillaOwnsCell(cellX - 1, cellY, cellZ, ownershipBand);", StringComparison.Ordinal)
            && fragment.Contains("if (owned && edgeZ == 1) owned = vanillaOwnsCell(cellX, cellY, cellZ - 1, ownershipBand);", StringComparison.Ordinal),
            "a face in a chunk plane is hidden only when BOTH cells that could hold its block are owned");
        c.True(fragment.Contains("owned = vanillaOwnsCell(cellX - 1, cellY, cellZ - 1, ownershipBand);", StringComparison.Ordinal),
            "and the chunk-corner line asks the fourth candidate too");
        // The slack absorbs interpolation slop at a plane and nothing else. Larger than a
        // quarter block would swallow the thin-cover mat, the shortest surface drawn here,
        // and start treating ordinary faces as boundaries.
        c.True(fragment.Contains("const float MASK_PLANE_SLACK = 0.03125;", StringComparison.Ordinal),
            "the plane slack stays far below the shortest surface the mesher emits");
        // Two scalars, and never the Vec2i overload: that one reaches glUniform2f, which an
        // integer uniform rejects outright, so the origin never leaves the CPU. G42.
        // Matched against the source with its whitespace collapsed, so wrapping the call
        // over two lines stays legal while dropping the cast still fails.
        string flattened = Regex.Replace(renderer, @"\s+", " ");
        c.True(flattened.Contains("prog.Uniform(\"maskSectionOriginX\", (int)", StringComparison.Ordinal)
            && flattened.Contains("prog.Uniform(\"maskSectionOriginZ\", (int)", StringComparison.Ordinal),
            "the renderer supplies that integer origin per draw");
        c.False(renderer.Contains("new Vec2i(", StringComparison.Ordinal),
            "no uniform is set through the integer-vector overload the driver rejects");

        int maskCheck = fragment.IndexOf("maskEnabled == 1", StringComparison.Ordinal);
        int shading = fragment.IndexOf("normalize(cross(", StringComparison.Ordinal);
        c.True(maskCheck > 0 && shading > 0 && maskCheck < shading,
            "ownership is decided before normals, lighting, noise, fog and output work");
        c.Eq(1, CountOccurrences(fragment, "texelFetch(readinessMask"),
            "opaque and water pass through exactly one shared ownership lookup");
        c.True(fragment.Contains("if (maskEnabled == 1)", StringComparison.Ordinal),
            "a disabled mask executes no readiness sample");
        // Water is under the mask again, which is what gives it a chunk-shaped edge rather
        // than the circle a per-pixel radius draws - but bounded outward, because ownership
        // asks whether the engine draws a CHUNK and its ground answers for that. Vanilla's
        // water can stop before its ground does, and suppressing ours inside that ring is
        // G124's dry riverbed.
        // ONE ownership test, for every band, at every distance. Water carried an outward
        // allowance for two builds - ownership barred from hiding it past a fraction of the
        // view distance - and past that fraction cached water drew straight into chunks the
        // engine was already drawing. Measured in play, twice. The allowance was a radius, and
        // a radius cannot express a chunk boundary, so it is gone rather than retuned.
        c.False(fragment.Contains("maskMaySuppress", StringComparison.Ordinal)
            || fragment.Contains("waterMaskRadius", StringComparison.Ordinal)
            || fragment.Contains("waterMaskBounded", StringComparison.Ordinal),
            "no outward allowance exempts cached water from the ownership mask");
        c.True(fragment.Contains("if (maskEnabled == 1)", StringComparison.Ordinal),
            "cached water obeys the ownership mask exactly as solid ground does");
        // Two channels of the same texel, one lookup. Red is ground's footprint, green is
        // water's - the same footprint grown by whole chunks, because ground keeps drawing
        // through the ring ownership refuses as a hole guard and water must not.
        c.True(fragment.Contains(
                "if ((ownershipBand == 1 ? ownership.g : ownership.r) > 0.5)",
                StringComparison.Ordinal),
            "water reads its own ownership channel and ground reads its own");
        c.Eq(1, CountOccurrences(fragment, "texelFetch(readinessMask"),
            "and both still come from exactly one ownership lookup");
        c.True(renderer.Contains("readinessMask.RebuildWaterFootprint(", StringComparison.Ordinal)
            && renderer.Contains("WaterFootprintDilationChunks()", StringComparison.Ordinal),
            "the renderer rebuilds water's footprint from the ground one every time it moves");
        c.False(renderer.Contains("WaterMaskRadius", StringComparison.Ordinal)
            || renderer.Contains("WaterMaskBounded", StringComparison.Ordinal),
            "and the renderer has no view-distance bound left to upload");
        // NO RADIUS. Two builds gave cached water a near cutoff measured against the camera -
        // first a share of the view distance, then an absolute distance - and both drew a
        // visible ring across every lake, because a per-pixel distance test is a circle and
        // the world contains no circles. The only near rule left is a Chebyshev count of
        // 32-block chunk columns, whose boundary can only fall on a chunk edge, and it is off
        // by default. Pinned as source text because the failure is invisible to every other
        // check here: it compiles, it runs, and it looks like a GPU effect painted on the sea.
        c.False(fragment.Contains("waterNearRadius", StringComparison.Ordinal),
            "no camera-distance cutoff survives anywhere in the water path");
        c.True(fragment.Contains(
                "max(abs(cellX - camChunkX), abs(cellZ - camChunkZ)) < waterNearChunks",
                StringComparison.Ordinal),
            "the near water rule is a chunk count, so its boundary lands on chunk edges");
        c.True(renderer.Contains("prog.Uniform(\"waterNearChunks\", waterNearChunks);", StringComparison.Ordinal),
            "and the renderer uploads it as whole chunks");
        c.Eq(0, LodNearHandoff.WaterNearChunks, "with no near rule at all by default");
        c.False(renderer.Contains("WaterNearFraction", StringComparison.Ordinal)
            || renderer.Contains("WaterNearBlocks", StringComparison.Ordinal),
            "and neither block nor view-distance form of the old cutoff survives");

        c.True(renderer.Contains("prog.Uniform(\"maskEnabled\"", StringComparison.Ordinal)
            && renderer.Contains("prog.Uniform(\"maskCapacity\"", StringComparison.Ordinal)
            && renderer.Contains("prog.BindTexture2D(\"readinessMask\"", StringComparison.Ordinal),
            "the renderer uploads the ring uniforms and binds the mask itself");
        // A healthy mask must be the only suppressor. If the radius stayed live it would
        // hide cached terrain in cells the mask still assigns to the cache.
        int handoffUniform = renderer.IndexOf("prog.Uniform(\"cacheHandoffDistance\"", StringComparison.Ordinal);
        c.True(handoffUniform > 0, "the renderer still sets the handoff distance uniform");
        string handoffAssignment = handoffUniform > 0
            ? renderer.Substring(handoffUniform, Math.Min(260, renderer.Length - handoffUniform))
            : string.Empty;
        c.True(handoffAssignment.Contains("maskOwnsPixels", StringComparison.Ordinal)
            && handoffAssignment.Contains("MaskNearFloor()", StringComparison.Ordinal),
            "a healthy mask replaces the radial handoff with its own near-field floor");

        // The floor exists to keep coarse cached geometry out of the player's face, but it
        // must never suppress terrain where vanilla has not proven it draws.
        int floorMethod = renderer.IndexOf("float MaskNearFloor()", StringComparison.Ordinal);
        c.True(floorMethod > 0, "the near floor is computed in one place");
        string floorBody = floorMethod > 0
            ? renderer.Substring(floorMethod, Math.Min(520, renderer.Length - floorMethod))
            : string.Empty;
        // The radius is a knob now, defaulting to nothing, because it also hid cached terrain
        // from a player who turned the vanilla view distance down far enough to want to look
        // at it. Whatever it is set to, it must still only ever apply where vanilla has proven
        // it draws - an unproven cell keeps its cached terrain.
        c.True(floorBody.Contains("VanillaReadinessState.VanillaReady", StringComparison.Ordinal)
            && floorBody.Contains("NearSuppressionBlocks", StringComparison.Ordinal)
            && floorBody.Contains("0f", StringComparison.Ordinal),
            "the near radius applies only while the camera's own cell is committed ready");
        c.Eq(0f, LodTerrainRenderer.MaskNearFloorDefaultBlocks,
            "and defaults to suppressing nothing at all");

        // Removing the radius must not touch the per-cell mask. The radius is one distance
        // around the camera; the mask is what hides cached terrain wherever vanilla is
        // actually drawing, which is the whole handoff and a different mechanism entirely.
        c.True(fragment.Contains("if (maskEnabled == 1)", StringComparison.Ordinal),
            "per-cell ownership still decides every fragment, independently of the radius");
        c.True(renderer.Contains("readiness.Classify(key) != VanillaSectionOwnership.VanillaOnly",
                StringComparison.Ordinal),
            "wholly owned sections are still skipped before drawing");
        // The mask became the default in 0.3.17, so the old opt-in gate is gone. What has to
        // survive is the override that lets the benchmark harness pin either path: without a
        // way to force the mask off, the radial path it is measured against is unreachable
        // and every "off" run would silently measure the mask instead.
        c.True(renderer.Contains("VINTAGEHORIZONS_CHUNK_MASK", StringComparison.Ordinal)
            && renderer.Contains("!= \"0\"", StringComparison.Ordinal),
            "the mask can still be forced off for a controlled comparison");
        c.True(renderer.Contains("DisposeReadinessMaskTexture();", StringComparison.Ordinal),
            "the mask texture is released on teardown");
    }

    static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// The benchmark runner's readiness gate reads one log line that the renderer formats.
    /// A rename on either side would leave the gate matching nothing, which is only
    /// discovered after a ten-minute game run, so the two are matched here instead: the
    /// renderer's interpolated literal is rendered with sample values and must satisfy the
    /// runner's own pattern. PowerShell uses this exact regex engine.
    /// </summary>
    static void ReadinessTelemetryContract(Check c)
    {
        string renderer = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render", "LodTerrainRenderer.cs"));
        string runner = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "scripts", "bench-windows.ps1"));

        c.True(runner.Contains("RequireReadinessConvergence", StringComparison.Ordinal),
            "the benchmark runner exposes the readiness convergence gate");

        Match describe = Regex.Match(renderer,
            @"public string DescribeReadiness\(\).*?(?<statement>return \$""shadow .*?;)",
            RegexOptions.Singleline);
        c.True(describe.Success, "the renderer still formats a readiness line to parse");
        if (!describe.Success) return;

        var line = new StringBuilder();
        foreach (Match part in Regex.Matches(describe.Groups["statement"].Value, @"\$""(?<text>[^""]*)"""))
            line.Append(part.Groups["text"].Value);
        // Holes formatted to one decimal print a fractional number; the rest are counts.
        string sample = Regex.Replace(line.ToString(), @"\{[^{}]*\}",
            m => m.Value.Contains(":0.0", StringComparison.Ordinal) ? "342.3" : "7");
        c.False(sample.Contains('{'), "every readiness hole was substituted with a sample value");

        Match pattern = Regex.Match(runner,
            @"\$shadowPattern =(?<parts>(?:\s*'[^']*'\s*\+?)+)");
        c.True(pattern.Success, "the runner still declares its readiness pattern");
        if (!pattern.Success) return;

        var expression = new StringBuilder();
        foreach (Match part in Regex.Matches(pattern.Groups["parts"].Value, @"'(?<text>[^']*)'"))
            expression.Append(part.Groups["text"].Value);

        Match parsed = Regex.Match("  vanilla readiness: " + sample, expression.ToString());
        c.True(parsed.Success, "the runner's readiness pattern matches the renderer's own line");
        if (!parsed.Success) return;
        foreach (string group in new[] { "ready", "unknown", "age", "probes", "errors", "dropped" })
            c.True(parsed.Groups[group].Success && parsed.Groups[group].Value.Length > 0,
                $"the readiness gate can read its {group} value");
    }

    /// <summary>
    /// scripts/package.sh names the release zip from modinfo.json, while the assembly
    /// identity comes from the csproj. Drift between them ships an artifact whose filename
    /// disagrees with the version the game reports.
    /// </summary>
    static void VersionAgreement(Check c)
    {
        CheckPair(c, "VintageHorizons", Path.Combine("VintageHorizons", "VintageHorizons.csproj"),
            Path.Combine("VintageHorizons", "modinfo.json"));
        CheckPair(c, "VintageHorizonsBench",
            Path.Combine("bench", "VintageHorizonsBench", "VintageHorizonsBench.csproj"),
            Path.Combine("bench", "VintageHorizonsBench", "modinfo.json"));
    }

    static void CheckPair(Check c, string label, string csprojRel, string modinfoRel)
    {
        string csproj = Path.Combine(GameAssemblies.RepoRoot, csprojRel);
        string modinfo = Path.Combine(GameAssemblies.RepoRoot, modinfoRel);

        if (!File.Exists(csproj) || !File.Exists(modinfo))
        {
            c.True(false, $"{label}: both csproj and modinfo.json exist");
            return;
        }

        Match fromCsproj = Regex.Match(File.ReadAllText(csproj), @"<Version>([^<]+)</Version>");
        Match fromModinfo = Regex.Match(File.ReadAllText(modinfo), @"""version""\s*:\s*""([^""]+)""");

        c.True(fromCsproj.Success, $"{label}: csproj declares a Version");
        c.True(fromModinfo.Success, $"{label}: modinfo.json declares a version");

        if (fromCsproj.Success && fromModinfo.Success)
        {
            c.Eq(fromCsproj.Groups[1].Value, fromModinfo.Groups[1].Value,
                $"{label}: csproj Version matches modinfo.json version");
        }
    }

    /// <summary>
    /// The old every-200-sections progress notification ran inside the 50 ms owning-thread
    /// assist callback. The synchronous logger reproduced multi-millisecond service tails;
    /// cumulative progress already belongs to the stats callback and /vhserver.
    /// </summary>
    static void AssistServeLoopDoesNotLogProgress(Check c)
    {
        string path = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Net",
            "LodAssistServerSystem.cs");
        string source = File.ReadAllText(path);
        int start = source.IndexOf("void AdmitPending(", StringComparison.Ordinal);
        int end = start < 0 ? -1 : source.IndexOf("LodAssistBlobReader? EnsureBlobReader(",
            start, StringComparison.Ordinal);

        c.True(start >= 0 && end > start, "the assist admission method is found for static inspection");
        if (start >= 0 && end > start)
        {
            string admission = source[start..end];
            c.False(admission.Contains("Mod.Logger.", StringComparison.Ordinal),
                "the 50 ms assist admission loop contains no synchronous logger call");
        }
    }

    /// <summary>
    /// Two commands registered under one name throw at the second registration, and the
    /// throw aborts StartClientSide part-way: every command declared after the collision
    /// silently does not exist, and the mod reports as a failed system while still
    /// running. The compiler cannot see it because the names are strings, and the game
    /// only says so in a log line nobody reads during a playtest.
    /// </summary>
    static void ChatCommandNamesAreUnique(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (string path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(GameAssemblies.RepoRoot, path);
            foreach (Match m in Regex.Matches(File.ReadAllText(path),
                @"ChatCommands\s*\.\s*Create\s*\(\s*""(?<name>[^""]+)"""))
            {
                string name = m.Groups["name"].Value;
                if (seen.TryGetValue(name, out string? first)) duplicates.Add($"{name} in {first} and {rel}");
                else seen[name] = rel;
            }
        }

        c.True(seen.Count > 0, "found chat command registrations to scan");
        c.SeqEq(Array.Empty<string>(), duplicates,
            $"all {seen.Count} chat command names are registered exactly once");
    }

    static void ConfigDialogWiring(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        string mod = File.ReadAllText(Path.Combine(src, "VintageHorizonsModSystem.cs"));
        string dialog = File.ReadAllText(Path.Combine(src, "Gui", "VintageHorizonsConfigDialog.cs"));
        string scale = File.ReadAllText(Path.Combine(src, "Gui", "LodThresholdScaleElement.cs"));

        c.True(mod.Contains("ChatCommands.Create(\"vhconfig\")", StringComparison.Ordinal),
            "the player-facing config command remains registered");
        c.True(dialog.Contains("new LodThresholdScaleElement", StringComparison.Ordinal),
            "the config window uses one shared multi-marker LOD scale");
        c.True(dialog.Contains("AddButton(\"Defaults\"", StringComparison.Ordinal)
            && dialog.Contains("AddButton(\"Save\"", StringComparison.Ordinal)
            && dialog.Contains("AddButton(\"Cancel\"", StringComparison.Ordinal),
            "the config window retains Defaults, Save and Cancel actions");
        c.True(scale.Contains("values[dragging - 1] + LodWorld.ThresholdStepBlocks", StringComparison.Ordinal)
            && scale.Contains("values[dragging + 1] - LodWorld.ThresholdStepBlocks", StringComparison.Ordinal),
            "each LOD marker remains constrained by both neighbours");
        c.True(scale.Contains("$\"L{i + 1}\"", StringComparison.Ordinal)
            && scale.Contains("Render2DLoadedTexture(markerLabels[i]", StringComparison.Ordinal),
            "the moving LOD handles render their L1-L6 names inside the boxes");
        c.True(dialog.Contains("const int MaxDrawDistance = 32768", StringComparison.Ordinal)
            && dialog.Contains("const int DrawDistanceStep = 512", StringComparison.Ordinal),
            "the cached draw-distance slider stops at 32,768 in 512-block increments");
        c.True(dialog.Contains("ToString(\"N0\", CultureInfo.InvariantCulture)", StringComparison.Ordinal),
            "the config window displays full block values instead of abbreviated thousands");
        c.True(dialog.Contains("AddSwitch(OnBlendPipelineChanged", StringComparison.Ordinal)
            && dialog.Contains("Seam blending and L0 block textures", StringComparison.Ordinal),
            "the config window exposes the saved blend-pipeline switch");
        c.True(mod.Contains("public bool BlendPipeline = true", StringComparison.Ordinal)
            && mod.Contains("config.BlendPipeline = renderer.BlendPipelineEnabled",
                StringComparison.Ordinal),
            "the blend choice defaults on and is persisted from the renderer request");
        c.True(mod.Contains("ChatCommands.Create(\"vhblend\")", StringComparison.Ordinal),
            "the same saved master switch is available as a simple command");
    }

    /// <summary>
    /// Our shaders may not declare an integer vector uniform, because the client cannot
    /// set one. Its only pair-of-integers setter is
    /// <c>ShaderProgramBase.Uniform(string, Vec2i)</c>, which reaches
    /// <c>GL.Uniform2(location, float, float)</c>; against an <c>ivec</c> uniform the
    /// driver answers GL_INVALID_OPERATION and the uniform keeps its previous value.
    /// Nothing throws, the shader compiles, the check tier's own re-implementation of the
    /// arithmetic still agrees - and the value never arrives. That shipped: the ownership
    /// mask addressed chunk (0,0) for every section in the world while its error was
    /// buried in a per-frame log line. Scalar int uniforms are set correctly, so split the
    /// vector.
    /// </summary>
    static void NoIntegerVectorUniforms(Check c)
    {
        string shaders = Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons", "shaders");
        var offenders = new List<string>();
        int scanned = 0;

        foreach (string path in Directory.EnumerateFiles(shaders, "*.*sh"))
        {
            scanned++;
            foreach (Match m in Regex.Matches(File.ReadAllText(path),
                @"^\s*uniform\s+(?<type>[iu]vec[234])\s+(?<name>\w+)", RegexOptions.Multiline))
            {
                offenders.Add($"{Path.GetFileName(path)}: {m.Groups["type"].Value} {m.Groups["name"].Value}");
            }
        }

        c.True(scanned > 0, "found shader files to scan for integer vector uniforms");
        c.SeqEq(Array.Empty<string>(), offenders,
            "no shader declares an integer vector uniform the client cannot set");
    }

    /// <summary>
    /// The experimental culler relies only on render order: vanilla terrain populates the
    /// depth buffer at 0.37, then cached terrain follows at 0.38. The engine sorts a
    /// renderer only when it is registered, so a live toggle must unregister and register
    /// again rather than merely change the property returned by RenderOrder.
    /// </summary>
    static void OcclusionCullingWiring(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render",
            "LodTerrainRenderer.cs"));
        string mod = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "VintageHorizonsModSystem.cs"));

        c.True(mod.Contains("ChatCommands.Create(\"vhocclusion\")", StringComparison.Ordinal),
            "the live occlusion toggle is registered");
        c.True(mod.Contains("VINTAGEHORIZONS_OCCLUSION_CULLING\") != \"0\"", StringComparison.Ordinal),
            "post-vanilla depth rejection is default-on with an explicit off override");
        c.True(renderer.Contains("bool postVanillaDepthCulling = true", StringComparison.Ordinal),
            "direct renderer construction also starts at the accepted post-vanilla order");
        c.True(renderer.Contains("PreVanillaRenderOrder = 0.36", StringComparison.Ordinal)
            && renderer.Contains("PostVanillaRenderOrder = 0.38", StringComparison.Ordinal),
            "the experiment brackets vanilla terrain's established 0.37 order");
        c.True(renderer.Contains("capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque)",
                StringComparison.Ordinal)
            && CountOccurrences(renderer,
                "capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque") == 1,
            "changing order re-registers the renderer through one shared registration path");
        c.False(renderer.Contains("BeginConditionalRender", StringComparison.Ordinal)
            || renderer.Contains("lodocclusion", StringComparison.Ordinal)
            || renderer.Contains("LodOcclusionProbe", StringComparison.Ordinal),
            "the rejected proxy/conditional-render path is absent");
        c.True(mod.Contains("ChatCommands.Create(\"vhtemporal\")", StringComparison.Ordinal)
            && mod.Contains("ChatCommands.Create(\"vhtemporalprofile\")", StringComparison.Ordinal)
            && renderer.Contains("RenderOpaqueMesh(key, mesh)", StringComparison.Ordinal)
            && renderer.Contains("QueryTarget.AnySamplesPassed", StringComparison.Ordinal)
            && renderer.Contains("readiness.Classify(key) == VanillaSectionOwnership.Mixed", StringComparison.Ordinal),
            "the accepted delayed exact-geometry path is wired behind its own live toggle");
        c.True(renderer.Contains("VINTAGEHORIZONS_TEMPORAL_OCCLUSION\") != \"0\"", StringComparison.Ordinal)
            && renderer.Contains("TemporalOcclusionProfileName { get; private set; } = \"aggressive\"", StringComparison.Ordinal)
            && renderer.Contains("float temporalOcclusionRotationMatrixLimit = float.PositiveInfinity;", StringComparison.Ordinal),
            "accepted aggressive temporal occlusion, including turn persistence, is default-on with an explicit off override");
        // A fourth reason since 0.3.53: switching batched drawing on or off. Under batching
        // no per-section queries are issued at all, so every stored answer is from the
        // other path and from an older camera; carrying them across the switch would show
        // as terrain missing after switching back.
        c.Eq(4, CountOccurrences(renderer, "InvalidateTemporalOcclusionScene();"),
            "only profile, render-order, camera/projection and draw-path changes globally "
            + "invalidate temporal results");
        c.True(renderer.Contains("query.State.Invalidate(temporalOcclusionEpoch)", StringComparison.Ordinal),
            "a replaced cached mesh invalidates its own query rather than every hidden section");
    }

    /// <summary>
    /// No source file may contain a control character. Shader assets are already covered by
    /// the ASCII rule, but C#, PowerShell and Markdown were not, and an editing pipeline
    /// that re-parses backslash escapes turns a regex `` into a literal backspace. The
    /// result is invisible in a terminal, compiles without complaint, and silently changes
    /// what a pattern matches. It has happened twice in this repository.
    /// </summary>
    static void SourceHasNoControlCharacters(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        var offenders = new List<string>();
        int scanned = 0;

        foreach (string dir in new[] { "VintageHorizons", "tests", "scripts", "dev", "bench" })
        {
            string path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                if (Path.GetExtension(file) is not (".cs" or ".ps1" or ".sh" or ".md" or ".json" or ".txt" or ".py")) continue;

                scanned++;
                string text = File.ReadAllText(file);
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    // Tab, CR and LF by code point: writing them as escapes is how the
                    // stray characters this check exists to catch got here in the first place.
                    if (ch >= ' ' || ch == (char)9 || ch == (char)13 || ch == (char)10) continue;
                    offenders.Add($"{Path.GetRelativePath(root, file)} offset {i} = 0x{(int)ch:X2}");
                    break;
                }
            }
        }

        c.True(scanned > 0, "found source files to scan for control characters");
        c.SeqEq(Array.Empty<string>(), offenders, $"none of {scanned} source files contain a control character");
    }
}

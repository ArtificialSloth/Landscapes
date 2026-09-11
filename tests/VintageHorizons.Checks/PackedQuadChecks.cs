namespace VintageHorizons.Checks;

/// <summary>Exact packed-quad geometry, before any GPU path is allowed to draw it.</summary>
public static class PackedQuadChecks
{
    public static void Run(Check c)
    {
        LayoutAndLimits(c);
        AllFacesMatchExpandedMesh(c);
        LevelsAndCoordinateLimits(c);
        TextureSlotSharesTheColourWord(c);
        TextureSlotEmissionFollowsTheJob(c);
        TableColourMatchesTheVertexByteOrder(c);
        TopSoilOverlayUsesTheRightHalf(c);
        WallsUseTheirOwnTextureRows(c);
        LogSidesResolveToBark(c);
        MaterialPayloadsAndDegenerates(c);
        Rejections(c);
    }

    static void LayoutAndLimits(Check c)
    {
        c.Eq(12, LodPackedQuadFormat.StrideBytes, "one exact packed quad is twelve bytes");
        c.Eq(3, LodPackedQuadFormat.WordsPerQuad, "the byte layout is three scalar words");
        c.Eq(6, LodPackedQuadFormat.VerticesPerQuad, "expanded parity covers two triangles");
        c.Eq(4, LodPackedQuadFormat.PulledVerticesPerQuad,
            "the indexed shader decodes each unique corner once");
        c.Eq(6, LodPackedQuadFormat.IndicesPerQuad,
            "the reusable topology still emits two triangles");
        c.Eq(LodSection.GridSize, LodPackedQuadFormat.MaximumGridCoordinate,
            "section endpoints include the far edge");
        c.Eq(88L, LodPackedQuadFormat.ExpandedBytes(1),
            "the comparison includes four vertices and six indices");
        c.Eq(12L, LodPackedQuadFormat.Bytes(1), "packed storage has no index payload");
        c.True(LodPackedQuadFormat.Bytes(100) * 2 < LodPackedQuadFormat.ExpandedBytes(100),
            "the selected format clears the phase's fifty-percent byte gate");
    }

    static void AllFacesMatchExpandedMesh(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(IsolatedRun(10, 4)));
        c.Eq(6, mesh.PackedOpaqueQuadCount, "an isolated solid emits all six packed faces");
        var faces = new HashSet<LodPackedFace>();
        for (int quad = 0; quad < mesh.PackedOpaqueQuadCount; quad++)
            faces.Add(LodPackedQuadFormat.FaceOf(mesh.PackedOpaqueQuads, quad));
        foreach (LodPackedFace face in Enum.GetValues<LodPackedFace>())
            c.True(faces.Contains(face), face + " winding is represented");

        AssertMatchesExpanded(c, mesh, columnBlocks: 1, "isolated L0 run");
    }

    static void LevelsAndCoordinateLimits(Check c)
    {
        foreach (int level in new[] { 0, 1, 3, LodWorld.MaxLevel })
        {
            MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(
                IsolatedRun(0x3FFF, 0x3FFE), LodWorld.SectionKey(level, 0, 0)));
            AssertMatchesExpanded(c, mesh, 1 << level, $"L{level} maximum-height run");
        }

        var words = new uint[LodPackedQuadFormat.WordsPerQuad];
        LodPackedQuadFormat.Encode(words, LodPackedFace.Top,
            0, LodSection.GridSize, 1, 1, 0, LodSection.GridSize,
            0x00332211, 63);
        LodPackedQuadFormat.DecodeVertex(words, 0, 2, 64,
            out float x, out float y, out float z, out uint color);
        c.Eq(4096f, x, "L6 reaches the exact far X edge");
        c.Eq(1f, y, "Y never scales with LOD");
        c.Eq(4096f, z, "L6 reaches the exact far Z edge");
        c.Eq(0x3F332211u, color, "the full colour/tint word survives");
    }

    /// <summary>
    /// Word 2 carries either an RGB colour or a block texture slot, and word 0's last spare
    /// bit says which. The two can share one field because at level 0 a quad's colour already
    /// is its block's colour, so the slot recovers the same colour from the block table and
    /// brings the texture rectangle with it - which is what makes real vanilla textures cost
    /// no extra memory. Everything below is the seam where that could silently go wrong:
    /// geometry must survive the extra bit, the face must not read it, and an untextured quad
    /// must be bit-identical to what it was before the flag existed.
    /// </summary>
    static void TextureSlotSharesTheColourWord(Check c)
    {
        var plain = new uint[LodPackedQuadFormat.WordsPerQuad];
        var textured = new uint[LodPackedQuadFormat.WordsPerQuad];
        LodPackedQuadFormat.Encode(plain, LodPackedFace.Top,
            0, LodSection.GridSize, 1, 1, 0, LodSection.GridSize, 0x00332211, 63);
        LodPackedQuadFormat.Encode(textured, LodPackedFace.Top,
            0, LodSection.GridSize, 1, 1, 0, LodSection.GridSize, 0x00332211, 63,
            textureSlot: 517);

        c.False(LodPackedQuadFormat.IsTextured(plain[0]),
            "a quad with no slot is not marked textured");
        c.True(LodPackedQuadFormat.IsTextured(textured[0]),
            "a quad with a slot is marked textured");
        c.Eq(plain[0] | 0x8000_0000u, textured[0],
            "the flag is the only geometry difference, so nothing else can be disturbed");
        c.Eq(plain[1], textured[1], "heights are untouched by the flag");
        c.Eq(517, LodPackedQuadFormat.TextureSlot(textured[2]),
            "the slot survives the colour word exactly");
        c.Eq(0x3F000000u | 517u, textured[2],
            "the alpha byte keeps its tint slot and band beside the texture slot");

        // The face lives in bits 28-30 and the flag in 31. A face that read four bits would
        // decode South as something else the moment a quad became textured.
        foreach (LodPackedFace face in Enum.GetValues<LodPackedFace>())
        {
            var words = new uint[LodPackedQuadFormat.WordsPerQuad];
            LodPackedQuadFormat.Encode(words, face, 1, 2, 3, 4, 5, 6, 0, 0, textureSlot: 1);
            LodPackedQuadFormat.DecodeVertex(words, 0, 0, 1,
                out float x, out float y, out float z, out _);
            var reference = new uint[LodPackedQuadFormat.WordsPerQuad];
            LodPackedQuadFormat.Encode(reference, face, 1, 2, 3, 4, 5, 6, 0, 0);
            LodPackedQuadFormat.DecodeVertex(reference, 0, 0, 1,
                out float rx, out float ry, out float rz, out _);
            c.Eq(rx, x, $"{face} X is unchanged by the textured flag");
            c.Eq(ry, y, $"{face} Y is unchanged by the textured flag");
            c.Eq(rz, z, $"{face} Z is unchanged by the textured flag");
        }

        bool refused = false;
        try
        {
            LodPackedQuadFormat.Encode(new uint[LodPackedQuadFormat.WordsPerQuad],
                LodPackedFace.Top, 0, 1, 0, 1, 0, 1, 0, 0,
                textureSlot: LodPackedQuadFormat.MaximumTextureSlot + 1);
        }
        catch (ArgumentOutOfRangeException) { refused = true; }
        c.True(refused, "a slot too large for the field is refused rather than truncated");
    }

    /// <summary>
    /// The player-facing blend switch crosses a worker boundary. Each queued job therefore
    /// owns the format choice and its identity; a process-wide flag could flip while a worker
    /// was half way through a section and produce a mixed mesh.
    /// </summary>
    static void TextureSlotEmissionFollowsTheJob(Check c)
    {
        LodSection section = IsolatedRun(10, 4, textureSlot: 517);
        MeshResult plain = LodMesher.BuildMesh(Fixtures.Job(
            section, emitTextureSlots: false, textureModeRevision: 11, worldEpoch: 21,
            contentRevision: 31, renderRevision: 41));
        MeshResult textured = LodMesher.BuildMesh(Fixtures.Job(
            section, emitTextureSlots: true, textureModeRevision: 12, worldEpoch: 22));
        MeshResult coarse = LodMesher.BuildMesh(Fixtures.Job(
            section, LodWorld.SectionKey(1, 0, 0),
            emitTextureSlots: true, textureModeRevision: 13));

        c.False(plain.HasTextureSlots, "an off job emits ordinary RGB packed quads");
        c.True(textured.HasTextureSlots, "an on L0 job emits block-texture slots");
        c.False(coarse.HasTextureSlots, "coarse LOD never receives one block's texture slot");
        c.Eq(11L, plain.TextureModeRevision, "the plain result carries its toggle identity");
        c.Eq(12L, textured.TextureModeRevision, "the textured result carries its toggle identity");
        c.Eq(21L, plain.WorldEpoch, "the plain result carries its world identity");
        c.Eq(22L, textured.WorldEpoch, "the textured result carries its world identity");
        c.Eq(31L, plain.ContentRevision, "the result carries its section-content identity");
        c.Eq(41L, plain.RenderRevision, "the result carries its render-obligation identity");

        for (int vertex = 0; vertex < textured.VertexCount; vertex++)
        {
            int at = vertex * 4;
            c.Eq((byte)(517 & 0xFF), textured.Rgba[at],
                "the visible legacy mesh carries the texture slot low byte");
            c.Eq((byte)(517 >> 8), textured.Rgba[at + 1],
                "the visible legacy mesh carries the texture slot high byte");
            c.Eq((byte)0, textured.Rgba[at + 2],
                "the visible legacy mesh retains all twenty-four slot bits");
            c.Eq((byte)(LodMesher.LegacyTextureBase + 63), textured.Rgba[at + 3],
                "the expanded comparison stream's fourth band marks a texture while preserving tint 63");
        }
        for (int vertex = 0; vertex < plain.VertexCount; vertex++)
        {
            int at = vertex * 4;
            c.Eq((byte)0x11, plain.Rgba[at], "plain legacy red remains the stored colour");
            c.Eq((byte)0x22, plain.Rgba[at + 1], "plain legacy green remains the stored colour");
            c.Eq((byte)0x33, plain.Rgba[at + 2], "plain legacy blue remains the stored colour");
            c.Eq((byte)63, plain.Rgba[at + 3], "plain legacy alpha/tint remains unchanged");
        }

        for (int quad = 0; quad < textured.PackedOpaqueQuadCount; quad++)
        {
            int word = quad * LodPackedQuadFormat.WordsPerQuad;
            c.False(LodPackedQuadFormat.IsTextured(plain.PackedOpaqueQuads[word]),
                "every quad in the off mesh retains RGB");
            c.True(LodPackedQuadFormat.IsTextured(textured.PackedOpaqueQuads[word]),
                "every solid L0 quad in the on mesh carries a slot");
            c.Eq(517, LodPackedQuadFormat.TextureSlot(textured.PackedOpaqueQuads[word + 2]),
                "the job-selected slot survives emission");
        }
    }

    /// <summary>
    /// A textured quad's colour comes out of the block table rather than out of its vertex,
    /// so the table must split a stored colour exactly the way the vertex path reads one.
    ///
    /// The low byte is RED, which is not the usual convention. Getting it backwards swapped
    /// red and blue on every textured surface and turned tan sand and brown soil blue across
    /// the whole of level 0 in 0.3.131. This ties the two decoders to one another rather than
    /// to a remembered convention.
    /// </summary>
    static void TableColourMatchesTheVertexByteOrder(Check c)
    {
        const int stored = 0x00405060;
        (float red, float green, float blue) = LodBlockTextureRegistry.Channels(stored);
        c.Eq(0x60 / 255f, red, "the table reads the low byte as red");
        c.Eq(0x50 / 255f, green, "the table reads the middle byte as green");
        c.Eq(0x40 / 255f, blue, "the table reads the high byte as blue");

        // And the same value, through the encoder the shader actually decodes.
        var words = new uint[LodPackedQuadFormat.WordsPerQuad];
        LodPackedQuadFormat.Encode(words, LodPackedFace.Top, 0, 1, 0, 1, 0, 1, stored, 0);
        c.Eq((uint)(red * 255f), words[2] & 0xFFu,
            "the packed word's low byte is the same channel the table calls red");
        c.Eq((uint)(green * 255f), (words[2] >> 8) & 0xFFu,
            "the packed word's middle byte is the same channel the table calls green");
        c.Eq((uint)(blue * 255f), (words[2] >> 16) & 0xFFu,
            "the packed word's high byte is the same channel the table calls blue");
    }

    /// <summary>
    /// TopSoil packs two logical tiles into one atlas rectangle: the side fringe on the left
    /// and the top overlay on the right. Forest floor has no separate `particletop`, so this
    /// is the path that prevents its side texture from being painted across the LOD top.
    /// </summary>
    static void TopSoilOverlayUsesTheRightHalf(Check c)
    {
        var full = new Vintagestory.API.Client.TextureAtlasPosition
        {
            atlasTextureId = 17,
            atlasNumber = 3,
            x1 = 0.20f,
            y1 = 0.30f,
            x2 = 0.60f,
            y2 = 0.70f,
        };

        Vintagestory.API.Client.TextureAtlasPosition top =
            LodBlockTextureRegistry.RightHalf(full);
        c.Eq(0.40f, top.x1, "the top overlay starts at the strip midpoint");
        c.Eq(0.60f, top.x2, "the top overlay retains the strip's right edge");
        c.Eq(full.y1, top.y1, "the top overlay retains the vertical origin");
        c.Eq(full.y2, top.y2, "the top overlay retains the vertical edge");
        c.Eq(full.atlasTextureId, top.atlasTextureId, "the sub-rectangle stays on its atlas");
        c.Eq(full.atlasNumber, top.atlasNumber, "the sub-rectangle stays on its atlas page");
        c.Eq(0.20f, full.x1, "the engine-owned full rectangle is not mutated");
    }

    /// <summary>
    /// A standing log names bark for its sides and the cut end for its top and bottom, and the
    /// engine draws each face from its own name. One texture per block put the cut end on every
    /// face of every distant trunk. Walls now carry rows of their own; a block with nothing
    /// different to say for its walls keeps the top's rows there and draws what it did before.
    /// </summary>
    static void WallsUseTheirOwnTextureRows(Check c)
    {
        static Vintagestory.API.Client.TextureAtlasPosition Rect(float x1, byte page = 0) => new()
        {
            atlasTextureId = 5,
            atlasNumber = page,
            x1 = x1,
            y1 = 0.10f,
            x2 = x1 + 0.05f,
            y2 = 0.15f,
        };

        var trunk = Rect(0.10f);
        var bark = Rect(0.30f);
        var barkAcross = Rect(0.50f);
        var registry = new LodBlockTextureRegistry(block => block.BlockId switch
        {
            1 => new(trunk, null, 0x00102030, 0x00102030,
                new LodBlockTextureRegistry.FaceTexture(bark, 0x00405060),
                new LodBlockTextureRegistry.FaceTexture(barkAcross, 0x00708090)),
            3 => new(trunk, null, 0x00102030, 0x00102030,
                new LodBlockTextureRegistry.FaceTexture(Rect(0.70f, page: 1), 0x00405060)),
            _ => new(trunk, null, 0x00102030, 0x00102030),
        });

        const int f = LodBlockTextureRegistry.FloatsPerSlot;
        c.Eq(8, LodBlockTextureRegistry.RowsPerSlot,
            "a slot is the top, colour, mean and overlay rows plus a rectangle and mean per wall axis");
        int log = registry.SlotFor(new Vintagestory.API.Common.Block { BlockId = 1 }, 0x00AABBCC);
        int plain = registry.SlotFor(new Vintagestory.API.Common.Block { BlockId = 2 }, 0x00AABBCC);
        int otherPage = registry.SlotFor(new Vintagestory.API.Common.Block { BlockId = 3 }, 0x00AABBCC);
        IReadOnlyList<float> t = registry.Table;

        c.Eq(trunk.x1, t[log * f + 0], "a log's top keeps the texture named for its up face");
        c.Eq(bark.x1, t[log * f + 16], "faces along X take the texture named for them");
        c.Eq(barkAcross.x1, t[log * f + 24], "faces along Z take theirs");
        c.Eq(0x60 / 255f, t[log * f + 20], "a wall normalises by its own texture's mean");
        c.Eq(0f, t[log * f + 23], "a wall with its own texture takes no top overlay");

        for (int i = 0; i < 4; i++)
        {
            c.Eq(t[plain * f + i], t[plain * f + 16 + i],
                "a block with no wall texture copies the top rectangle to its X walls");
            c.Eq(t[plain * f + i], t[plain * f + 24 + i], "and to its Z walls");
            c.Eq(t[plain * f + 8 + i], t[plain * f + 20 + i],
                "with the top's mean and overlay flag on X");
            c.Eq(t[plain * f + 8 + i], t[plain * f + 28 + i], "and on Z");
            c.Eq(t[otherPage * f + i], t[otherPage * f + 16 + i],
                "a wall texture on another atlas page falls back to the top");
        }
        c.Eq(2, registry.WallsAccepted, "both wall axes of the log are taken");
        c.Eq(1, registry.WallsOtherPage, "the wall on another page is counted as refused");
    }

    /// <summary>
    /// The face names behind those rows, against the game's own log definitions: a standing log
    /// names bark under "all" and the cut end under "up" and "down"; a log lying north-south
    /// names the cut end under "north" and "south" and sideways bark under "all".
    /// </summary>
    static void LogSidesResolveToBark(Check c)
    {
        static Vintagestory.API.Client.CompositeTexture Tex(int id) => new()
        {
            Baked = new Vintagestory.API.Client.BakedCompositeTexture { TextureSubId = id },
        };
        static bool Usable(int id) => id > 0;
        const int Bark = 11, Trunk = 12, BarkAcross = 13;

        var standing = new Dictionary<string, Vintagestory.API.Client.CompositeTexture>
        {
            ["all"] = Tex(Bark), ["up"] = Tex(Trunk), ["down"] = Tex(Trunk),
        };
        c.Eq(Trunk, VintageHorizonsModSystem.PickFaceTexture(standing,
                VintageHorizonsModSystem.UpFaceTextureNames, Usable, out _),
            "a standing log's top is the cut end");
        c.Eq(Bark, VintageHorizonsModSystem.PickFaceTexture(standing,
                VintageHorizonsModSystem.XWallTextureNames, Usable, out _),
            "its east and west faces are bark");
        c.Eq(Bark, VintageHorizonsModSystem.PickFaceTexture(standing,
                VintageHorizonsModSystem.ZWallTextureNames, Usable, out _),
            "its north and south faces are bark");

        var lying = new Dictionary<string, Vintagestory.API.Client.CompositeTexture>
        {
            ["all"] = Tex(BarkAcross), ["up"] = Tex(Bark), ["down"] = Tex(Bark),
            ["north"] = Tex(Trunk), ["south"] = Tex(Trunk),
        };
        c.Eq(Bark, VintageHorizonsModSystem.PickFaceTexture(lying,
                VintageHorizonsModSystem.UpFaceTextureNames, Usable, out _),
            "a north-south log's top is bark");
        c.Eq(BarkAcross, VintageHorizonsModSystem.PickFaceTexture(lying,
                VintageHorizonsModSystem.XWallTextureNames, Usable, out _),
            "its east and west faces are sideways bark");
        c.Eq(Trunk, VintageHorizonsModSystem.PickFaceTexture(lying,
                VintageHorizonsModSystem.ZWallTextureNames, Usable, out _),
            "and its cut end faces north and south");

        // A redwood quarter-trunk: its block file names the cut end for "north" and "up" and
        // bark for the other sides. Walls pass over the top's own texture, as the resolver does.
        var redwood = new Dictionary<string, Vintagestory.API.Client.CompositeTexture>
        {
            ["north"] = Tex(Trunk), ["up"] = Tex(Trunk), ["down"] = Tex(Bark),
            ["east"] = Tex(Bark), ["south"] = Tex(Bark), ["west"] = Tex(Bark),
        };
        int redwoodTop = VintageHorizonsModSystem.PickFaceTexture(redwood,
            VintageHorizonsModSystem.UpFaceTextureNames, Usable, out _);
        c.Eq(Trunk, redwoodTop, "a redwood trunk's top is the cut end");
        c.Eq(Bark, VintageHorizonsModSystem.PickFaceTexture(redwood,
                VintageHorizonsModSystem.ZWallTextureNames, Usable, out _, skipId: redwoodTop),
            "its north and south walls are bark, not the cut end its block file names for north");
        c.Eq(Bark, VintageHorizonsModSystem.PickFaceTexture(redwood,
                VintageHorizonsModSystem.XWallTextureNames, Usable, out _, skipId: redwoodTop),
            "its east and west walls are bark");
        c.Eq(Trunk, VintageHorizonsModSystem.PickFaceTexture(lying,
                VintageHorizonsModSystem.ZWallTextureNames, Usable, out _, skipId: Bark),
            "passing over the top's texture still leaves a lying log's cut end facing north");

        var offAtlas = new Dictionary<string, Vintagestory.API.Client.CompositeTexture>
        {
            ["all"] = Tex(0),
        };
        c.Eq(-1, VintageHorizonsModSystem.PickFaceTexture(offAtlas,
                VintageHorizonsModSystem.XWallTextureNames, Usable, out _),
            "a texture that is not on the atlas is not taken");
    }

    static void MaterialPayloadsAndDegenerates(Check c)
    {
        foreach (byte alpha in new byte[] { 0, 63, 64, 127, 128, 191, 255 })
        {
            var words = new uint[LodPackedQuadFormat.WordsPerQuad];
            LodPackedQuadFormat.Encode(words, LodPackedFace.North,
                5, 5, 4.25f, 4.25f, 7, 7, 0x00CCBBAA, alpha);
            LodPackedQuadFormat.DecodeVertex(words, 0, 5, 8,
                out float x, out float y, out float z, out uint color);
            c.Eq(40f, x, $"degenerate X survives for alpha {alpha}");
            c.Eq(4.25f, y, $"quarter-block height survives for alpha {alpha}");
            c.Eq(56f, z, $"degenerate Z survives for alpha {alpha}");
            c.Eq(0x00CCBBAAu | (uint)alpha << 24, color,
                $"material/tint alpha {alpha} survives exactly");
        }
    }

    static void Rejections(Check c)
    {
        var words = new uint[LodPackedQuadFormat.WordsPerQuad];
        c.Throws<ArgumentOutOfRangeException>(() => LodPackedQuadFormat.Encode(
            words, LodPackedFace.Top, -1, 1, 0, 0, 0, 1, 0, 0),
            "negative grid coordinates are refused");
        c.Throws<ArgumentOutOfRangeException>(() => LodPackedQuadFormat.Encode(
            words, LodPackedFace.Top, 0, 65, 0, 0, 0, 1, 0, 0),
            "coordinates past the section edge are refused");
        c.Throws<ArgumentOutOfRangeException>(() => LodPackedQuadFormat.Encode(
            words, LodPackedFace.Top, 0, 1, 0.1f, 1, 0, 1, 0, 0),
            "non-quarter heights are refused rather than rounded");
        c.Throws<ArgumentException>(() => LodPackedQuadFormat.Encode(
            words, LodPackedFace.Top, 0, 1, 2, 1, 0, 1, 0, 0),
            "inverted height ranges are refused");
    }

    static void AssertMatchesExpanded(Check c, MeshResult mesh, int columnBlocks, string what)
    {
        c.Eq(mesh.VertexCount / 4, mesh.PackedOpaqueQuadCount,
            what + " has one record per expanded quad");
        c.Eq(mesh.PackedOpaqueQuadCount * LodPackedQuadFormat.WordsPerQuad,
            mesh.PackedOpaqueQuads.Length, what + " has no unused packed words");

        for (int quad = 0; quad < mesh.PackedOpaqueQuadCount; quad++)
        {
            for (int emitted = 0; emitted < LodPackedQuadFormat.VerticesPerQuad; emitted++)
            {
                int expandedVertex = mesh.Indices[quad * 6 + emitted];
                LodPackedQuadFormat.DecodeVertex(mesh.PackedOpaqueQuads, quad, emitted,
                    columnBlocks, out float x, out float y, out float z, out uint color);
                c.Eq(mesh.Xyz[expandedVertex * 3], x, what + " X parity");
                c.Eq(mesh.Xyz[expandedVertex * 3 + 1], y, what + " Y parity");
                c.Eq(mesh.Xyz[expandedVertex * 3 + 2], z, what + " Z parity");
                uint expandedColor = (uint)mesh.Rgba[expandedVertex * 4]
                    | (uint)mesh.Rgba[expandedVertex * 4 + 1] << 8
                    | (uint)mesh.Rgba[expandedVertex * 4 + 2] << 16
                    | (uint)mesh.Rgba[expandedVertex * 4 + 3] << 24;
                c.Eq(expandedColor, color, what + " RGBA/tint parity");
            }
        }
    }

    static LodSection IsolatedRun(int yTop, int yBottom, ushort textureSlot = 0)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(1, 0x00332211, 0, 63, textureSlot);
        section.SetColumn(LodSection.ColumnIndex(5, 7),
            new[] { LodSection.PackRun(0, yTop, yBottom) });
        return section;
    }
}

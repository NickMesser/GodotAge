namespace AAEmu.GodotViewer;

/// <summary>
/// Reads a cell's <c>client/terrain/heightmap.dat</c> into a 513 x 513 grid of heights in metres, 2 m apart,
/// indexed [x, y] in CryEngine axes. The file layout is the one AAEmu's Hmap and NodeCell parse, and heights use
/// the same 5 cm quantisation. Sectors stored at reduced resolution (nSize 17, 5 ...) are resampled bilinearly
/// here: NodeCell.UpScale only ever samples a sector's first 2 x 2 raw points and keeps the old nSize as the row
/// stride, which flattens those sectors.
/// </summary>
internal static class TerrainCellReader
{
    public const int Samples = 513;
    public const float UnitMeters = 2f;
    private const int SectorsPerCell = 16;
    private const int SectorUnits = 32;

    public static float[,] Read(Stream source, out Dictionary<int, int> sectorsBySize)
    {
        using var ms = new MemoryStream();
        source.CopyTo(ms);
        ms.Position = 0;
        using var br = new BinaryReader(ms);

        var version = br.ReadByte();
        br.ReadBytes(3);
        var chunkSize = br.ReadInt32();
        br.ReadInt32(); // heightmap size in units
        br.ReadInt32(); // unit size in metres
        var sectorMeters = br.ReadInt32();
        br.ReadInt32(); // sector table size
        br.ReadSingle(); // z ratio
        br.ReadSingle(); // ocean level
        if (version >= 24)
            br.ReadBytes(128);

        // The file is a quadtree; only the 64 m leaf nodes carry height data.
        var sectors = new List<Sector>();
        while (ms.Position < chunkSize && ms.Position < ms.Length)
        {
            var sector = ReadNode(br);
            if (sector.Size > 0)
                sectors.Add(sector);
        }
        if (sectors.Count != SectorsPerCell * SectorsPerCell)
            throw new InvalidDataException($"expected {SectorsPerCell * SectorsPerCell} sectors with height data, found {sectors.Count}");

        var minX = sectors.Min(s => s.MinX);
        var minY = sectors.Min(s => s.MinY);
        var grid = new Sector[SectorsPerCell, SectorsPerCell];
        foreach (var s in sectors)
        {
            var sx = (int)MathF.Round((s.MinX - minX) / sectorMeters);
            var sy = (int)MathF.Round((s.MinY - minY) / sectorMeters);
            if (sx is < 0 or >= SectorsPerCell || sy is < 0 or >= SectorsPerCell || grid[sx, sy] != null)
                throw new InvalidDataException($"sector at ({s.MinX}, {s.MinY}) does not fit the 16 x 16 grid");
            grid[sx, sy] = s;
        }
        sectorsBySize = sectors.GroupBy(s => s.Size).ToDictionary(g => g.Key, g => g.Count());

        var heights = new float[Samples, Samples];
        for (var ux = 0; ux < Samples; ux++)
        for (var uy = 0; uy < Samples; uy++)
        {
            var sx = Math.Min(ux / SectorUnits, SectorsPerCell - 1);
            var sy = Math.Min(uy / SectorUnits, SectorsPerCell - 1);
            heights[ux, uy] = grid[sx, sy].HeightAt(ux - sx * SectorUnits, uy - sy * SectorUnits);
        }
        return heights;
    }

    private static Sector ReadNode(BinaryReader br)
    {
        var version = br.ReadByte();
        br.ReadBytes(3);
        var minX = br.ReadSingle();
        var minY = br.ReadSingle();
        br.ReadBytes(16); // box min z, box max xyz
        br.ReadByte(); // has holes
        var offset = br.ReadSingle();
        var range = br.ReadSingle();
        var size = br.ReadInt32();
        var extraCount = br.ReadInt32();
        var raw = new ushort[size * size];
        for (var i = 0; i < raw.Length; i++)
            raw[i] = br.ReadUInt16();
        br.ReadBytes(4 + 16 + 36 + extraCount);
        return new Sector(minX, minY, size, Decode(version, offset, range, raw));
    }

    /// <summary>Same arithmetic as NodeCell.Init, RescaleToInt and RawDataToHeight.</summary>
    private static float[] Decode(byte version, float offset, float range, ushort[] raw)
    {
        var fMin = offset;
        var fMax = fMin + 0xFFF0 * range;
        var iOffset = (int)(fMin * 20);
        var iRange = (int)((fMax - fMin) * 20);
        var iStep = iRange > 0 ? (iRange + 4094) / 4095 : 1;

        var heights = new float[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var data = raw[i];
            if (version < 7)
            {
                var height = fMin + (0xFFF0 & data) * range;
                var hdec = (ushort)((int)((height - fMin) * 20) / iStep);
                data = (ushort)((data & 0xF) | (hdec << 4));
            }
            heights[i] = 0.05f * iOffset + (data >> 4) * iStep * 0.05f;
        }
        return heights;
    }

    private sealed record Sector(float MinX, float MinY, int Size, float[] Heights)
    {
        /// <summary>Height at unit (lx, ly), 0..32 inclusive; unit 32 is the first unit of the next sector.</summary>
        public float HeightAt(int lx, int ly)
        {
            if (Size == SectorUnits + 1)
                return Heights[lx * Size + ly];

            var scale = (Size - 1) / (float)SectorUnits;
            var fx = lx * scale;
            var fy = ly * scale;
            var x0 = (int)fx;
            var y0 = (int)fy;
            var x1 = Math.Min(x0 + 1, Size - 1);
            var y1 = Math.Min(y0 + 1, Size - 1);
            var tx = fx - x0;
            var ty = fy - y0;
            var h0 = Heights[x0 * Size + y0] + (Heights[x1 * Size + y0] - Heights[x0 * Size + y0]) * tx;
            var h1 = Heights[x0 * Size + y1] + (Heights[x1 * Size + y1] - Heights[x0 * Size + y1]) * tx;
            return h0 + (h1 - h0) * ty;
        }
    }
}

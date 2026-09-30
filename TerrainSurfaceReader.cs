namespace AAEmu.GodotViewer;

/// <summary>
/// Reads a cell's <c>client/terrain/heightmap.dat</c> into a 513 x 513 grid of global terrain surface ids,
/// indexed [x, y] in CryEngine axes. Each height sample's low four bits select a global id from the version 24
/// heightmap header. Slot 15 is the empty / hole surface and is returned as <see cref="None"/>.
/// </summary>
internal static class TerrainSurfaceReader
{
    public const int Samples = TerrainCellReader.Samples;
    public const int None = -1;

    private const int SectorsPerCell = 16;
    private const int SectorUnits = 32;
    private const int GlobalSurfaceIds = 32;

    public static int[,] ReadSurfaceIds(byte[] heightmapDat)
    {
        ArgumentNullException.ThrowIfNull(heightmapDat);
        using var ms = new MemoryStream(heightmapDat, writable: false);
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
        if (version < 24)
            throw new NotSupportedException($"heightmap version {version} has no global surface id table");

        var globalIds = new uint[GlobalSurfaceIds];
        for (var i = 0; i < globalIds.Length; i++)
            globalIds[i] = br.ReadUInt32();

        // The file is a quadtree; only the 64 m leaf nodes carry surface samples.
        var sectors = new List<Sector>();
        while (ms.Position < chunkSize && ms.Position < ms.Length)
        {
            var sector = ReadNode(br, globalIds);
            if (sector.Size > 0)
                sectors.Add(sector);
        }
        if (sectors.Count != SectorsPerCell * SectorsPerCell)
            throw new InvalidDataException($"expected {SectorsPerCell * SectorsPerCell} sectors with surface data, found {sectors.Count}");

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

        var ids = new int[Samples, Samples];
        for (var ux = 0; ux < Samples; ux++)
        for (var uy = 0; uy < Samples; uy++)
        {
            var sx = Math.Min(ux / SectorUnits, SectorsPerCell - 1);
            var sy = Math.Min(uy / SectorUnits, SectorsPerCell - 1);
            ids[ux, uy] = grid[sx, sy].SurfaceAt(ux - sx * SectorUnits, uy - sy * SectorUnits);
        }
        return ids;
    }

    /// <summary>Returns the distinct, non-empty global surface ids in a surface grid.</summary>
    public static IReadOnlyCollection<int> UsedIds(int[,] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var used = new HashSet<int>();
        foreach (var id in ids)
            if (id != None)
                used.Add(id);
        return used;
    }

    private static Sector ReadNode(BinaryReader br, uint[] globalIds)
    {
        br.ReadByte(); // node version
        br.ReadBytes(3);
        var minX = br.ReadSingle();
        var minY = br.ReadSingle();
        br.ReadBytes(16); // box min z, box max xyz
        br.ReadByte(); // has holes
        br.ReadBytes(8); // height offset and range
        var size = br.ReadInt32();
        var extraCount = br.ReadInt32();
        if (size < 0 || extraCount < 0 || (long)size * size > int.MaxValue)
            throw new InvalidDataException($"invalid terrain sector dimensions ({size}, {extraCount})");

        var surfaces = new int[size * size];
        for (var i = 0; i < surfaces.Length; i++)
        {
            var localId = br.ReadUInt16() & 0xF;
            var globalId = globalIds[localId];
            if (localId == 15)
                surfaces[i] = None;
            else if (globalId > int.MaxValue)
                throw new InvalidDataException($"global surface id {globalId} cannot fit in an Int32");
            else
                surfaces[i] = (int)globalId;
        }
        br.ReadBytes(20 + extraCount + 36);
        return new Sector(minX, minY, size, surfaces);
    }

    private sealed record Sector(float MinX, float MinY, int Size, int[] Surfaces)
    {
        /// <summary>Surface at unit (lx, ly), 0..32 inclusive; reduced sectors use their nearest stored sample.</summary>
        public int SurfaceAt(int lx, int ly)
        {
            if (Size == SectorUnits + 1)
                return Surfaces[lx * Size + ly];

            var scale = (Size - 1) / (float)SectorUnits;
            var x = Math.Clamp((int)MathF.Round(lx * scale, MidpointRounding.AwayFromZero), 0, Size - 1);
            var y = Math.Clamp((int)MathF.Round(ly * scale, MidpointRounding.AwayFromZero), 0, Size - 1);
            return Surfaces[x * Size + y];
        }
    }
}

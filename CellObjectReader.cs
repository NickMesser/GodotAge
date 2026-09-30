#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;

namespace AAEmu.GodotViewer;

// Placement data of an ArcheAge world cell (game/worlds/<world>/cells/XXX_YYY/client/*).
//
// Coordinates: CryEngine axes (X east, Y north, Z up), metres, relative to the cell's south-west corner
// (the viewer adds XXX * 1024, YYY * 1024). Transforms are System.Numerics matrices in its row-vector
// convention: world = Vector3.Transform(local, Transform); translation is in M41..M43.
//
// Paths: model and material paths are pak paths ("game/objects/..."), lower-case, forward slashes.
// Material paths carry no extension; the file is path + ".mtl".
//
// Files and what they hold (all verified against the 10.0.2.13 client):
//   object.dat      Octree of every render node of the cell: brushes, trees/bushes, decals, water, roads,
//                   distance clouds, voxel meshes. Starts with the model table (= statobjs.dat) and the
//                   material table (= materials.dat).
//   vegetation.dat  4 x 4 buckets of 256 m holding the painted small vegetation (grass, flowers, plants).
//                   None of these instances are in object.dat; they must be drawn in addition to it.
//   brush.dat       16 x 16 buckets of 64 m holding the same brushes as object.dat (sector-local copy).
//   big_object.dat  Objects whose bounds cross the cell border (mostly lakes), in THIS cell's coordinates,
//                   including those owned by neighbour cells. Own material table.
//   statobjs.dat / materials.dat   Copies of object.dat's two tables (u32 count + count x char[256]).
//   material_list.dat  Material preload list (u32 count + count x {u32, char[256]}), no placement.
//   visareas.dat    VisAreas / portals / occluder areas (culling volumes), no visible geometry.

/// <summary>Record type ids used by object.dat and big_object.dat.</summary>
public enum CellRecordType
{
    Brush = 1,
    Vegetation = 2,
    Voxel = 6,
    Decal = 9,
    WaterVolume = 11,
    Road = 13,
    DistanceCloud = 14,
}

/// <summary>CryEngine 3 render-node flags (ERF_*) as stored at record offset 0x24.</summary>
[Flags]
public enum CellRenderFlags : uint
{
    None = 0,
    GoodOccluder = 1u << 0,
    CastShadowMaps = 1u << 3,
    Hidden = 1u << 8,
    OutdoorOnly = 1u << 11,
    /// <summary>Invisible collision helper (box.cgf, collision_proxy materials). Do not draw.</summary>
    CollisionProxy = 1u << 22,
    /// <summary>Invisible ray-cast helper. Do not draw.</summary>
    RaycastProxy = 1u << 27,
    MinSpecMask = 7u << 24,
}

/// <summary>Source file of a placement.</summary>
public enum CellPlacementSource
{
    ObjectDat,
    BigObjectDat,
    VegetationDat,
    BrushDat,
}

/// <summary>Fields shared by every record (the CryEngine render-node header, offsets 0x00..0x2A).</summary>
public abstract class CellRenderNode
{
    public CellRecordType Type { get; internal set; }
    public CellPlacementSource Source { get; internal set; }
    /// <summary>World-space bounds stored in the record (cell-local). Vegetation bounds are rounded outwards to 128/255 m.</summary>
    public Vector3 BoundsMin { get; internal set; }
    public Vector3 BoundsMax { get; internal set; }
    /// <summary>Precomputed maximum view distance in metres (radius x view-distance ratio).</summary>
    public float MaxViewDistance { get; internal set; }
    public CellRenderFlags Flags { get; internal set; }
    public byte ViewDistRatio { get; internal set; }
    public byte LodRatio { get; internal set; }
    /// <summary>Minimum config spec (0 = any). Vegetation copies the group's minConfigSpec here.</summary>
    public int MinSpec => (int)(((uint)Flags >> 24) & 7);
    /// <summary>False for collision / ray-cast proxies and hidden nodes.</summary>
    public bool IsVisible => (Flags & (CellRenderFlags.CollisionProxy | CellRenderFlags.RaycastProxy | CellRenderFlags.Hidden)) == 0;
    /// <summary>Byte offset of the record in its source file.</summary>
    public int FileOffset { get; internal set; }

    internal void ReadHeader(ReadOnlySpan<byte> r)
    {
        BoundsMin = CellBin.V3(r, 0x04);
        BoundsMax = CellBin.V3(r, 0x10);
        MaxViewDistance = CellBin.F(r, 0x20);
        Flags = (CellRenderFlags)CellBin.U(r, 0x24);
        ViewDistRatio = r[0x28];
        LodRatio = r[0x29];
    }
}

/// <summary>Type 1, 132 bytes: a static mesh (.cgf) with a full 3x4 transform.</summary>
public sealed class CellBrush : CellRenderNode
{
    /// <summary>Index into <see cref="CellObjects.ModelPaths"/> (record offset 0x7F).</summary>
    public int ModelIndex { get; internal set; }
    public string? ModelPath { get; internal set; }
    /// <summary>Index into <see cref="CellObjects.MaterialPaths"/> (record offset 0x77). Always set: the editor writes the model's
    /// own material when there is no override, so a brush never relies on the .cgf's material.</summary>
    public int MaterialIndex { get; internal set; }
    public string? MaterialPath { get; internal set; }
    /// <summary>Model space to cell space (rotation and scale, possibly non-uniform, plus translation). Apply it to the mesh data
    /// as stored: the stored bounds only match when the .cgf root node's own matrix is ignored (child nodes sit relative to the
    /// root, node translations are in cm).</summary>
    public Matrix4x4 Transform { get; internal set; }
}

/// <summary>Type 2, 68 bytes (64 in vegetation.dat): a vegetation instance of a world vegetation.xml group.</summary>
public sealed class CellVegetation : CellRenderNode
{
    /// <summary>vegetation.xml group id (record offset 0x3B, u16).</summary>
    public int GroupId { get; internal set; }
    /// <summary>Model of the group ("game/" + modelFileName), filled when a <see cref="CellVegetationTable"/> is given.</summary>
    public string? ModelPath { get; internal set; }
    /// <summary>Group material override (vegetation.xml matName) or null.</summary>
    public string? MaterialPath { get; internal set; }
    public Vector3 Position { get; internal set; }
    public float Scale { get; internal set; }
    /// <summary>Rotation bytes; angle = byte * 2 pi / 255. X and Y are signed and non-zero only for terrain-aligned groups.</summary>
    public sbyte AngleX { get; internal set; }
    public sbyte AngleY { get; internal set; }
    public byte AngleZ { get; internal set; }
    /// <summary>Record byte 0x3F. Only two values per cell (e.g. 119 / 197), random per instance: looks uninitialised.</summary>
    public byte Unknown3F { get; internal set; }
    /// <summary>Record bytes 0x41..0x43 (default 7F 7F 7F). Not a consistent tint; kept raw.</summary>
    public uint Unknown41 { get; internal set; }
    /// <summary>Group brightness (vegetation.xml fBrightness), 1 when unknown.</summary>
    public float Brightness { get; internal set; } = 1f;
    /// <summary>Group requests terrain colour modulation. Preserved for renderers that can sample the cover texture.</summary>
    public bool UseTerrainColor { get; internal set; }
    /// <summary>Group has bAlignToTerrain. The tilt is already in AngleX / AngleY (quantised from the terrain normal at export);
    /// using the heightmap normal instead fits the stored bounds worse.</summary>
    public bool AlignToTerrain { get; internal set; }
    /// <summary>scale x RotX(AngleX) x RotY(AngleY) x RotZ(AngleZ) x translate(Position), i.e. CryEngine's Rz*Ry*Rx.</summary>
    public Matrix4x4 Transform { get; internal set; }
}

/// <summary>Type 9, 115 bytes: a projected or planar decal.</summary>
public sealed class CellDecal : CellRenderNode
{
    /// <summary>0 planar quad, 1 project on static objects, 2 project on terrain, 3 terrain and static objects.</summary>
    public byte ProjectionType { get; internal set; }
    /// <summary>Byte 0x2C: 1 on 97% of planar decals, 0 on projected ones; probably CryEngine's m_deferred.</summary>
    public byte Deferred { get; internal set; }
    public Vector3 Position { get; internal set; }
    /// <summary>Decal Z axis scaled by <see cref="Radius"/> (stored separately at 0x3B).</summary>
    public Vector3 Normal { get; internal set; }
    /// <summary>Orthonormal basis: rows are right, up and normal (CryEngine explicitRightUpFront columns).</summary>
    public Matrix4x4 Rotation { get; internal set; }
    /// <summary>Half size of the decal square in metres.</summary>
    public float Radius { get; internal set; }
    public int MaterialIndex { get; internal set; }
    public string? MaterialPath { get; internal set; }
    /// <summary>Draw order among overlapping decals (editor default 16).</summary>
    public byte SortPriority { get; internal set; }
    /// <summary>Half depth of the projection volume: Radius for projected decals, Radius / 2 for planar ones (matches the stored bounds).</summary>
    public float HalfDepth => ProjectionType == 0 ? Radius * 0.5f : Radius;
    /// <summary>Maps the unit cube [-1,1]^3 to the decal volume (X right, Y up, Z along the normal). A planar decal is the z = 0 face.</summary>
    public Matrix4x4 Transform { get; internal set; }
}

public enum CellWaterVolumeType
{
    Unknown = 0,
    Ocean = 1,
    /// <summary>Lake / pond: a polygon at the surface height.</summary>
    Area = 2,
    /// <summary>One river segment: a quad of 4 vertices, the river's outline rides on one segment as the physics contour.</summary>
    River = 3,
}

/// <summary>Type 11, variable size: a water volume (lake polygon or one river segment).</summary>
public sealed class CellWaterVolume : CellRenderNode
{
    /// <summary>Low byte of the u32 at 0x2B. (AAEmu's WaterObjectVolumeType numbering is off by one: 2 is Area, 3 is River.)</summary>
    public CellWaterVolumeType VolumeType { get; internal set; }
    public uint TypeAndMiscBits { get; internal set; }
    /// <summary>Id shared by all segments of one river / by the copies of one lake in neighbouring cells' big_object.dat.</summary>
    public ulong VolumeId { get; internal set; }
    public int MaterialIndex { get; internal set; }
    /// <summary>Water material, or null when the name is empty (many rivers): use a default water material.</summary>
    public string? MaterialPath { get; internal set; }
    public float FogDensity { get; internal set; }
    public Vector3 FogColor { get; internal set; }
    /// <summary>Fog plane (normal xyz, d). Areas: the flat surface z = -D. Rivers: one plane per river (its highest point),
    /// the segment's own surface is the vertices' z.</summary>
    public Plane FogPlane { get; internal set; }
    public float UTexCoordBegin { get; internal set; }
    public float UTexCoordEnd { get; internal set; }
    public float SurfaceUScale { get; internal set; }
    public float SurfaceVScale { get; internal set; }
    public float Depth { get; internal set; }
    public float StreamSpeed { get; internal set; }
    /// <summary>Render vertices: polygon (Area; z follows the terrain under the points, the surface is <see cref="SurfaceHeight"/>)
    /// or quad start-left, start-right, end-left, end-right (River; z is the surface).</summary>
    public List<Vector3> Vertices { get; internal set; } = [];
    /// <summary>Physics contour (outline of the whole volume); equals Vertices for areas, empty on most river segments.</summary>
    public List<Vector3> PhysicsContour { get; internal set; } = [];
    /// <summary>Top of the volume (= stored bounds max z); the volume reaches down <see cref="Depth"/> metres.</summary>
    public float SurfaceHeight => FogPlane.Normal.Z != 0 ? -FogPlane.D / FogPlane.Normal.Z : BoundsMax.Z;

    /// <summary>Triangulated water surface: cell-local positions, UVs (already scaled by the surface scales) and triangle indices
    /// (counter-clockwise seen from above).</summary>
    public (Vector3[] Positions, Vector2[] Uvs, int[] Indices) BuildSurfaceMesh()
    {
        if (VolumeType == CellWaterVolumeType.River && Vertices.Count == 4)
        {
            var p = Vertices.ToArray();
            var uv = new[]
            {
                new Vector2(UTexCoordBegin * SurfaceUScale, 0), new Vector2(UTexCoordBegin * SurfaceUScale, SurfaceVScale),
                new Vector2(UTexCoordEnd * SurfaceUScale, 0), new Vector2(UTexCoordEnd * SurfaceUScale, SurfaceVScale),
            };
            return (p, uv, CellGeometry.OrientUp(p, [0, 1, 2, 2, 1, 3]));
        }
        // Area points keep the editor's (terrain-following) heights; the surface itself is flat at the fog plane.
        var h = SurfaceHeight;
        var pts = Vertices.Select(v => new Vector3(v.X, v.Y, h)).ToArray();
        var uvs = pts.Select(v => new Vector2(v.X * SurfaceUScale, v.Y * SurfaceVScale)).ToArray();
        return (pts, uvs, CellGeometry.TriangulatePolygon(pts));
    }
}

/// <summary>Type 13, variable size: one piece of a road spline, draped on the terrain.</summary>
public sealed class CellRoad : CellRenderNode
{
    /// <summary>Vertex pairs across the road: [2i] one edge, [2i+1] the other edge.</summary>
    public List<Vector3> Vertices { get; internal set; } = [];
    public float TexCoordBegin { get; internal set; }
    public float TexCoordEnd { get; internal set; }
    public float TexCoordBeginGlobal { get; internal set; }
    public float TexCoordEndGlobal { get; internal set; }
    public int MaterialIndex { get; internal set; }
    public string? MaterialPath { get; internal set; }
    /// <summary>Last byte of the material field (0..5 seen, mostly 0): draw order among overlapping roads.</summary>
    public byte SortPriority { get; internal set; }
    public float Width => Vertices.Count >= 2 ? Vector3.Distance(Vertices[0], Vertices[1]) : 0;

    /// <summary>Triangle strip over the vertex pairs. U runs from TexCoordBegin to TexCoordEnd along the centre line, V is 0 / 1 across.
    /// The stored heights follow the terrain at the vertices only; lift the mesh a few cm or drape it on the heightmap.</summary>
    public (Vector3[] Positions, Vector2[] Uvs, int[] Indices) BuildMesh()
    {
        var n = Vertices.Count / 2;
        var pos = Vertices.Take(n * 2).ToArray();
        var uv = new Vector2[pos.Length];
        var len = new float[n];
        for (var i = 1; i < n; i++)
            len[i] = len[i - 1] + Vector3.Distance((pos[2 * i] + pos[2 * i + 1]) * 0.5f, (pos[2 * i - 2] + pos[2 * i - 1]) * 0.5f);
        var total = n > 1 ? len[n - 1] : 1f;
        for (var i = 0; i < n; i++)
        {
            var u = TexCoordBegin + (TexCoordEnd - TexCoordBegin) * (total > 0 ? len[i] / total : 0);
            uv[2 * i] = new Vector2(u, 0);
            uv[2 * i + 1] = new Vector2(u, 1);
        }
        var idx = new List<int>();
        for (var i = 0; i + 1 < n; i++)
            idx.AddRange([2 * i, 2 * i + 1, 2 * i + 2, 2 * i + 2, 2 * i + 1, 2 * i + 3]);
        return (pos, uv, CellGeometry.OrientUp(pos, idx.ToArray()));
    }
}

/// <summary>Type 14, 83 bytes: a distant cloud billboard in the sky (AAEmu calls this type AutoCubeMap).</summary>
public sealed class CellDistanceCloud : CellRenderNode
{
    public Vector3 Position { get; internal set; }
    /// <summary>Size in metres (the editor object's scale); the stored bounds are only +-1 m.</summary>
    public float SizeX { get; internal set; }
    public float SizeY { get; internal set; }
    public Quaternion Rotation { get; internal set; }
    public int MaterialIndex { get; internal set; }
    public string? MaterialPath { get; internal set; }
}

/// <summary>Type 6, variable size (~200 KB + mesh): a voxel object (cave / overhang terrain patch), 64 m cube.</summary>
public sealed class CellVoxel : CellRenderNode
{
    public int[] Header { get; internal set; } = [];
    /// <summary>Surface type names (surface.xml SurfaceType Name), positional: the mesh's per-vertex surface index selects the slot (unused slots are "").</summary>
    public List<string> SurfaceNames { get; internal set; } = [];
    /// <summary>Voxel space (0..64 m) to cell space.</summary>
    public Matrix4x4 Transform { get; internal set; }
    /// <summary>One entry per LOD: {u32 uncompressed size, zlib stream of a CryTek 0x745 chunk file}.</summary>
    public List<byte[]> LodData { get; internal set; } = [];

    /// <summary>Decodes LOD 0 (or <paramref name="lod"/>) into a mesh in voxel space (apply <see cref="Transform"/>).</summary>
    public CellVoxelMesh? DecodeMesh(int lod = 0) => lod < LodData.Count ? CellVoxelMesh.Decode(LodData[lod]) : null;
}

/// <summary>Mesh of a voxel object: CryTek 0x745 file with CGF data streams.</summary>
public sealed class CellVoxelMesh
{
    public Vector3[] Positions { get; private set; } = [];
    public Vector3[] Normals { get; private set; } = [];
    /// <summary>Vertex colours as stored (4 bytes per vertex, stream COLORS), or empty.</summary>
    public uint[] Colors { get; private set; } = [];
    /// <summary>Per-vertex index into <see cref="CellVoxel.SurfaceNames"/> (stream VERT_MATS), or empty.</summary>
    public int[] SurfaceIndices { get; private set; } = [];
    public int[] Indices { get; private set; } = [];

    public static CellVoxelMesh? Decode(byte[] lodRange)
    {
        if (lodRange.Length < 8)
            return null;
        var expected = BitConverter.ToInt32(lodRange, 0);
        byte[] file;
        using (var z = new ZLibStream(new MemoryStream(lodRange, 4, lodRange.Length - 4), CompressionMode.Decompress))
        using (var ms = new MemoryStream(Math.Max(expected, 0)))
        {
            z.CopyTo(ms);
            file = ms.ToArray();
        }
        if (file.Length < 20 || Encoding.ASCII.GetString(file, 0, 6) != "CryTek")
            return null;
        var version = BitConverter.ToUInt32(file, 12);
        var tableOffset = BitConverter.ToInt32(file, 16);
        var count = BitConverter.ToInt32(file, tableOffset);
        var entrySize = version == 0x745 ? 20 : 16;
        var mesh = new CellVoxelMesh();
        for (var i = 0; i < count; i++)
        {
            var e = tableOffset + 4 + i * entrySize;
            var type = BitConverter.ToUInt32(file, e);
            var offset = BitConverter.ToInt32(file, e + 8);
            if (type != 0xCCCC0016)
                continue;
            // STREAM_DATA_CHUNK_DESC_0800 after the 16-byte chunk header copy: flags, streamType, count, elementSize, reserved[2].
            var d = offset + 16;
            var streamType = BitConverter.ToInt32(file, d + 4);
            var n = BitConverter.ToInt32(file, d + 8);
            var size = BitConverter.ToInt32(file, d + 12);
            var p = d + 24;
            switch (streamType)
            {
                case 0 when size == 12: mesh.Positions = ReadV3(file, p, n); break;
                case 1 when size == 12: mesh.Normals = ReadV3(file, p, n); break;
                case 3 when size == 4: mesh.Colors = Enumerable.Range(0, n).Select(k => BitConverter.ToUInt32(file, p + k * 4)).ToArray(); break;
                case 11 when size == 4: mesh.SurfaceIndices = Enumerable.Range(0, n).Select(k => BitConverter.ToInt32(file, p + k * 4)).ToArray(); break;
                case 5 when size == 2: mesh.Indices = Enumerable.Range(0, n).Select(k => (int)BitConverter.ToUInt16(file, p + k * 2)).ToArray(); break;
                case 5 when size == 4: mesh.Indices = Enumerable.Range(0, n).Select(k => BitConverter.ToInt32(file, p + k * 4)).ToArray(); break;
            }
        }
        return mesh;
    }

    private static Vector3[] ReadV3(byte[] b, int p, int n)
    {
        var a = new Vector3[n];
        for (var i = 0; i < n; i++)
            a[i] = new Vector3(BitConverter.ToSingle(b, p + i * 12), BitConverter.ToSingle(b, p + i * 12 + 4), BitConverter.ToSingle(b, p + i * 12 + 8));
        return a;
    }
}

/// <summary>Everything read from one object.dat (or big_object.dat).</summary>
public sealed class CellObjects
{
    /// <summary>Model table (.cgf paths), identical to statobjs.dat.</summary>
    public List<string> ModelPaths { get; } = [];
    /// <summary>Material table (paths without .mtl), identical to materials.dat.</summary>
    public List<string> MaterialPaths { get; } = [];
    public List<CellBrush> Brushes { get; } = [];
    public List<CellVegetation> Vegetation { get; } = [];
    public List<CellDecal> Decals { get; } = [];
    public List<CellWaterVolume> WaterVolumes { get; } = [];
    public List<CellRoad> Roads { get; } = [];
    public List<CellDistanceCloud> DistanceClouds { get; } = [];
    public List<CellVoxel> Voxels { get; } = [];
    /// <summary>Records per type id, including types without a decoder (none are known to exist).</summary>
    public Dictionary<int, int> RecordCounts { get; } = [];
    public int OctreeNodeCount { get; internal set; }
    /// <summary>Parse problems (unknown record type, overrun); empty for every file of the 10.0.2.13 client.</summary>
    public List<string> Warnings { get; } = [];

    public IEnumerable<CellRenderNode> All =>
        Brushes.Cast<CellRenderNode>().Concat(Vegetation).Concat(Decals).Concat(WaterVolumes).Concat(Roads).Concat(DistanceClouds).Concat(Voxels);
}

/// <summary>A cell's placements assembled from all per-cell files (see <see cref="CellObjectReader.ReadCell"/>).</summary>
public sealed class CellContent
{
    public CellObjects Objects { get; internal set; } = new();
    /// <summary>vegetation.dat instances (grass and small plants), cell-local.</summary>
    public List<CellVegetation> PaintedVegetation { get; internal set; } = [];
    /// <summary>big_object.dat records owned by neighbour cells (not in this object.dat), in this cell's coordinates.</summary>
    public List<CellRenderNode> NeighbourBigObjects { get; internal set; } = [];
    public CellEntities? Entities { get; internal set; }
}

/// <summary>Pure .NET readers for the per-cell placement files.</summary>
public static class CellObjectReader
{
    public const float CellSize = 1024f;
    private const int NodeHeaderSize = 33;
    private const int VoxelLodTableOffset = 0x2B + 32 + 65536 + 2048 + 131072 + 48; // 198779

    /// <summary>Reads object.dat: model table, material table and the octree of records.</summary>
    /// <param name="vegetation">World vegetation.xml table, used to fill vegetation model paths; may be null.</param>
    public static CellObjects ReadObjectDat(byte[] data, CellVegetationTable? vegetation = null)
    {
        var result = new CellObjects();
        if (data.Length <= 8)
            return result;
        var o = ReadTable(data, 0, result.ModelPaths);
        o = ReadTable(data, o, result.MaterialPaths);
        while (o + NodeHeaderSize <= data.Length)
            o = ReadNode(data, o, result, vegetation);
        return result;
    }

    /// <summary>Reads big_object.dat: u32 model count + table, u32 material count + table, then records back to back (no octree).
    /// Coordinates are relative to the cell the file belongs to, even for objects owned by a neighbour.</summary>
    public static CellObjects ReadBigObjectDat(byte[] data, CellVegetationTable? vegetation = null)
    {
        var result = new CellObjects();
        if (data.Length < 8)
            return result;
        var o = ReadTable(data, 0, result.ModelPaths);
        o = ReadTable(data, o, result.MaterialPaths);
        ReadRecords(data, o, data.Length, result, vegetation, CellPlacementSource.BigObjectDat);
        return result;
    }

    /// <summary>Reads vegetation.dat: u32 version (1), 16 offsets, 16 sizes, then 64-byte vegetation records (the object.dat record
    /// without its type field) bucketed by 256 m block, index = bx * 4 + by, positions and bounds relative to the block.
    /// The result is converted to cell-local coordinates.</summary>
    public static List<CellVegetation> ReadVegetationDat(byte[] data, CellVegetationTable? vegetation = null)
    {
        var list = new List<CellVegetation>();
        foreach (var (rec, offset, shift) in ReadBuckets(data, 4, 256f, 64))
        {
            var v = ParseVegetation(rec, vegetation, shift);
            v.Source = CellPlacementSource.VegetationDat;
            v.FileOffset = offset;
            list.Add(v);
        }
        return list;
    }

    /// <summary>Reads brush.dat: u32 version (1), 256 offsets, 256 sizes, 128-byte brush records bucketed by 64 m sector
    /// (index = sx * 16 + sy, sector-local). Same brushes as object.dat; returned in cell-local coordinates.</summary>
    public static List<CellBrush> ReadBrushDat(byte[] data, IReadOnlyList<string> modelPaths, IReadOnlyList<string> materialPaths)
    {
        var list = new List<CellBrush>();
        foreach (var (rec, offset, shift) in ReadBuckets(data, 16, 64f, 128))
        {
            var b = ParseBrush(rec, modelPaths, materialPaths, shift);
            b.Source = CellPlacementSource.BrushDat;
            b.FileOffset = offset;
            list.Add(b);
        }
        return list;
    }

    /// <summary>statobjs.dat / materials.dat: u32 count + count x char[256].</summary>
    public static List<string> ReadNameTable(byte[] data)
    {
        var list = new List<string>();
        if (data.Length < 4)
            return list;
        var n = BitConverter.ToInt32(data, 0);
        for (var i = 0; i < n && 4 + (i + 1) * 256 <= data.Length; i++)
            list.Add(CellBin.CString(data, 4 + i * 256, 256));
        return list;
    }

    /// <summary>material_list.dat: u32 count + count x {u32, char[256]} (bytes after the terminator are garbage). Contains
    /// duplicates: it lists every material instance the cell loads, including the models' own materials.</summary>
    public static List<string> ReadMaterialList(byte[] data)
    {
        var list = new List<string>();
        if (data.Length < 4)
            return list;
        ReadTable(data, 0, list);
        return list;
    }

    /// <summary>Convenience: reads object.dat, vegetation.dat, big_object.dat and entities.xml of one cell.</summary>
    /// <param name="getFile">Returns the bytes of a per-cell file by name ("object.dat", ...), or null when missing.</param>
    public static CellContent ReadCell(Func<string, byte[]?> getFile, CellVegetationTable? vegetation)
    {
        var content = new CellContent();
        var obj = getFile("object.dat");
        if (obj != null)
            content.Objects = ReadObjectDat(obj, vegetation);
        var veg = getFile("vegetation.dat");
        if (veg != null)
            content.PaintedVegetation = ReadVegetationDat(veg, vegetation);
        var big = getFile("big_object.dat");
        if (big != null)
            content.NeighbourBigObjects = NotIn(ReadBigObjectDat(big, vegetation), content.Objects);
        var ent = getFile("entities.xml");
        if (ent != null)
            content.Entities = CellEntityReader.Read(ent);
        return content;
    }

    /// <summary>Records of <paramref name="big"/> that are not also in <paramref name="own"/> (same type and stored bounds),
    /// i.e. the big objects owned by a neighbour cell.</summary>
    public static List<CellRenderNode> NotIn(CellObjects big, CellObjects own)
    {
        var keys = new HashSet<(CellRecordType, Vector3, Vector3)>(own.All.Select(n => (n.Type, n.BoundsMin, n.BoundsMax)));
        return big.All.Where(n => !keys.Contains((n.Type, n.BoundsMin, n.BoundsMax))).ToList();
    }

    private static int ReadTable(byte[] b, int o, List<string> names)
    {
        var n = BitConverter.ToInt32(b, o);
        o += 4;
        for (var i = 0; i < n; i++, o += 260)
            names.Add(CellBin.CString(b, o + 4, 256));
        return o;
    }

    private static int ReadNode(byte[] b, int o, CellObjects result, CellVegetationTable? vegetation)
    {
        // Node header: i32 (always 2), Vec3 min, Vec3 max, i32 data size, u8 child mask; then data, then the children in bit order.
        var size = BitConverter.ToInt32(b, o + 28);
        var mask = b[o + 32];
        var start = o + NodeHeaderSize;
        var end = start + size;
        result.OctreeNodeCount++;
        if (size < 0 || end > b.Length)
        {
            result.Warnings.Add($"node at 0x{o:X}: bad size {size}");
            return b.Length;
        }
        ReadRecords(b, start, end, result, vegetation, CellPlacementSource.ObjectDat);
        var p = end;
        for (var i = 0; i < 8 && p < b.Length; i++)
            if ((mask & (1 << i)) != 0)
                p = ReadNode(b, p, result, vegetation);
        return p;
    }

    /// <summary>Size of the record at <paramref name="o"/>, or -1 for an unknown type.</summary>
    public static int RecordSize(byte[] b, int o)
    {
        var type = BitConverter.ToInt32(b, o);
        switch (type)
        {
            case 1: return 132;
            case 2: return 68;
            case 9: return 115;
            case 14: return 83;
            case 11: return 0x7B + 12 * (BitConverter.ToInt32(b, o + 0x6B) + BitConverter.ToInt32(b, o + 0x77));
            case 13: return 0x43 + 12 * BitConverter.ToInt32(b, o + 0x2B);
            case 6:
                var ranges = BitConverter.ToInt32(b, o + VoxelLodTableOffset);
                var p = o + VoxelLodTableOffset + 4;
                for (var i = 0; i < ranges; i++)
                    p += 4 + BitConverter.ToInt32(b, p);
                return p - o;
            default: return -1; // AAEmu lists sizes for 4, 5, 8 and 27, but no file of the client contains them.
        }
    }

    private static void ReadRecords(byte[] b, int o, int end, CellObjects result, CellVegetationTable? vegetation, CellPlacementSource source)
    {
        while (o + 4 <= end)
        {
            var type = BitConverter.ToInt32(b, o);
            var size = RecordSize(b, o);
            if (size <= 0 || o + size > end)
            {
                result.Warnings.Add($"record at 0x{o:X}: type {type}, size {size}, block ends at 0x{end:X}");
                return;
            }
            result.RecordCounts[type] = result.RecordCounts.GetValueOrDefault(type) + 1;
            var r = new ReadOnlySpan<byte>(b, o, size);
            CellRenderNode node;
            switch ((CellRecordType)type)
            {
                case CellRecordType.Brush:
                    var brush = ParseBrush(r, result.ModelPaths, result.MaterialPaths, Vector3.Zero);
                    result.Brushes.Add(brush);
                    node = brush;
                    break;
                case CellRecordType.Vegetation:
                    var veg = ParseVegetation(r, vegetation, Vector3.Zero);
                    result.Vegetation.Add(veg);
                    node = veg;
                    break;
                case CellRecordType.Decal:
                    var decal = ParseDecal(r, result.MaterialPaths);
                    result.Decals.Add(decal);
                    node = decal;
                    break;
                case CellRecordType.WaterVolume:
                    var water = ParseWater(r, result.MaterialPaths);
                    result.WaterVolumes.Add(water);
                    node = water;
                    break;
                case CellRecordType.Road:
                    var road = ParseRoad(r, result.MaterialPaths);
                    result.Roads.Add(road);
                    node = road;
                    break;
                case CellRecordType.DistanceCloud:
                    var cloud = ParseCloud(r, result.MaterialPaths);
                    result.DistanceClouds.Add(cloud);
                    node = cloud;
                    break;
                case CellRecordType.Voxel:
                    var voxel = ParseVoxel(r);
                    result.Voxels.Add(voxel);
                    node = voxel;
                    break;
                default:
                    o += size;
                    continue;
            }
            node.Type = (CellRecordType)type;
            node.Source = source;
            node.FileOffset = o;
            o += size;
        }
    }

    /// <summary>Walks a bucketed file and yields each record with its type field restored and the bucket's offset.</summary>
    private static IEnumerable<(byte[] Record, int Offset, Vector3 Shift)> ReadBuckets(byte[] data, int perSide, float bucketSize, int recordSize)
    {
        var buckets = perSide * perSide;
        if (data.Length < 4 + buckets * 8)
            yield break;
        var type = recordSize == 64 ? 2 : 1;
        for (var i = 0; i < buckets; i++)
        {
            var offset = BitConverter.ToInt32(data, 4 + i * 4);
            var size = BitConverter.ToInt32(data, 4 + buckets * 4 + i * 4);
            var shift = new Vector3(i / perSide * bucketSize, i % perSide * bucketSize, 0);
            for (var p = offset; p + recordSize <= offset + size && p + recordSize <= data.Length; p += recordSize)
            {
                var rec = new byte[recordSize + 4];
                BitConverter.TryWriteBytes(rec, type);
                Array.Copy(data, p, rec, 4, recordSize);
                yield return (rec, p, shift);
            }
        }
    }

    private static CellBrush ParseBrush(ReadOnlySpan<byte> r, IReadOnlyList<string> models, IReadOnlyList<string> materials, Vector3 shift)
    {
        var b = new CellBrush { Type = CellRecordType.Brush };
        b.ReadHeader(r);
        b.BoundsMin += shift;
        b.BoundsMax += shift;
        // 0x2B..0x46: 28 bytes that vary like stack garbage (pointer-looking values); not needed.
        var m = new float[12];
        for (var i = 0; i < 12; i++)
            m[i] = CellBin.F(r, 0x47 + i * 4);
        // CryEngine Matrix34, row-major, column vectors: translation in m[3], m[7], m[11].
        b.Transform = new Matrix4x4(
            m[0], m[4], m[8], 0,
            m[1], m[5], m[9], 0,
            m[2], m[6], m[10], 0,
            m[3] + shift.X, m[7] + shift.Y, m[11] + shift.Z, 1);
        b.MaterialIndex = CellBin.I(r, 0x77);
        b.ModelIndex = CellBin.I(r, 0x7F);
        b.ModelPath = At(models, b.ModelIndex);
        b.MaterialPath = At(materials, b.MaterialIndex);
        return b;
    }

    private static CellVegetation ParseVegetation(ReadOnlySpan<byte> r, CellVegetationTable? table, Vector3 shift)
    {
        var v = new CellVegetation { Type = CellRecordType.Vegetation };
        v.ReadHeader(r);
        v.BoundsMin += shift;
        v.BoundsMax += shift;
        v.Position = CellBin.V3(r, 0x2B) + shift;
        v.Scale = CellBin.F(r, 0x37);
        v.GroupId = BitConverter.ToUInt16(r[0x3B..]);
        v.AngleX = (sbyte)r[0x3D];
        v.AngleY = (sbyte)r[0x3E];
        v.Unknown3F = r[0x3F];
        v.AngleZ = r[0x40];
        v.Unknown41 = (uint)(r[0x41] | r[0x42] << 8 | r[0x43] << 16);
        const float byteToRad = MathF.PI * 2f / 255f;
        v.Transform = Matrix4x4.CreateScale(v.Scale)
                      * Matrix4x4.CreateRotationX(v.AngleX * byteToRad)
                      * Matrix4x4.CreateRotationY(v.AngleY * byteToRad)
                      * Matrix4x4.CreateRotationZ(v.AngleZ * byteToRad)
                      * Matrix4x4.CreateTranslation(v.Position);
        if (table != null && table.Groups.TryGetValue(v.GroupId, out var g))
        {
            v.ModelPath = g.ModelPath;
            v.MaterialPath = g.MaterialPath;
            v.Brightness = g.Brightness;
            v.UseTerrainColor = g.UseTerrainColor;
            v.AlignToTerrain = g.AlignToTerrain;
        }
        return v;
    }

    private static CellDecal ParseDecal(ReadOnlySpan<byte> r, IReadOnlyList<string> materials)
    {
        var d = new CellDecal { Type = CellRecordType.Decal };
        d.ReadHeader(r);
        d.ProjectionType = r[0x2B];
        d.Deferred = r[0x2C];
        // 0x2D..0x2E: uninitialised bytes.
        d.Position = CellBin.V3(r, 0x2F);
        d.Normal = CellBin.V3(r, 0x3B);
        var m = new float[9];
        for (var i = 0; i < 9; i++)
            m[i] = CellBin.F(r, 0x47 + i * 4);
        // Stored row-major; its columns are right, up, normal. Numerics rows = those columns.
        d.Rotation = new Matrix4x4(m[0], m[3], m[6], 0, m[1], m[4], m[7], 0, m[2], m[5], m[8], 0, 0, 0, 0, 1);
        d.Radius = CellBin.F(r, 0x6B);
        d.MaterialIndex = BitConverter.ToUInt16(r[0x6F..]);
        d.SortPriority = r[0x72];
        d.MaterialPath = At(materials, d.MaterialIndex);
        d.Transform = Matrix4x4.CreateScale(d.Radius, d.Radius, d.HalfDepth) * d.Rotation * Matrix4x4.CreateTranslation(d.Position);
        return d;
    }

    private static CellWaterVolume ParseWater(ReadOnlySpan<byte> r, IReadOnlyList<string> materials)
    {
        var w = new CellWaterVolume { Type = CellRecordType.WaterVolume };
        w.ReadHeader(r);
        w.TypeAndMiscBits = CellBin.U(r, 0x2B);
        w.VolumeType = (CellWaterVolumeType)(w.TypeAndMiscBits & 0xFF);
        w.VolumeId = BitConverter.ToUInt64(r[0x2F..]);
        w.MaterialIndex = CellBin.I(r, 0x37);
        w.MaterialPath = At(materials, w.MaterialIndex);
        w.FogDensity = CellBin.F(r, 0x3B);
        w.FogColor = CellBin.V3(r, 0x3F);
        w.FogPlane = new Plane(CellBin.V3(r, 0x4B), CellBin.F(r, 0x57));
        w.UTexCoordBegin = CellBin.F(r, 0x5B);
        w.UTexCoordEnd = CellBin.F(r, 0x5F);
        w.SurfaceUScale = CellBin.F(r, 0x63);
        w.SurfaceVScale = CellBin.F(r, 0x67);
        var n1 = CellBin.I(r, 0x6B);
        w.Depth = CellBin.F(r, 0x6F);
        w.StreamSpeed = CellBin.F(r, 0x73);
        var n2 = CellBin.I(r, 0x77);
        for (var i = 0; i < n1; i++)
            w.Vertices.Add(CellBin.V3(r, 0x7B + i * 12));
        for (var i = 0; i < n2; i++)
            w.PhysicsContour.Add(CellBin.V3(r, 0x7B + (n1 + i) * 12));
        return w;
    }

    private static CellRoad ParseRoad(ReadOnlySpan<byte> r, IReadOnlyList<string> materials)
    {
        var road = new CellRoad { Type = CellRecordType.Road };
        road.ReadHeader(r);
        var n = CellBin.I(r, 0x2B);
        road.TexCoordBegin = CellBin.F(r, 0x2F);
        road.TexCoordEnd = CellBin.F(r, 0x33);
        road.TexCoordBeginGlobal = CellBin.F(r, 0x37);
        road.TexCoordEndGlobal = CellBin.F(r, 0x3B);
        // u16 material index, u8 0, u8 sort priority (AAEmu's single-byte count at 0x2B works only below 256 vertices).
        road.MaterialIndex = BitConverter.ToUInt16(r[0x3F..]);
        road.SortPriority = r[0x42];
        road.MaterialPath = At(materials, road.MaterialIndex);
        for (var i = 0; i < n; i++)
            road.Vertices.Add(CellBin.V3(r, 0x43 + i * 12));
        return road;
    }

    private static CellDistanceCloud ParseCloud(ReadOnlySpan<byte> r, IReadOnlyList<string> materials)
    {
        var c = new CellDistanceCloud { Type = CellRecordType.DistanceCloud };
        c.ReadHeader(r);
        c.Position = CellBin.V3(r, 0x2B);
        c.SizeX = CellBin.F(r, 0x37);
        c.SizeY = CellBin.F(r, 0x3B);
        c.Rotation = new Quaternion(CellBin.F(r, 0x3F), CellBin.F(r, 0x43), CellBin.F(r, 0x47), CellBin.F(r, 0x4B));
        c.MaterialIndex = CellBin.I(r, 0x4F);
        c.MaterialPath = At(materials, c.MaterialIndex);
        return c;
    }

    private static CellVoxel ParseVoxel(ReadOnlySpan<byte> r)
    {
        var v = new CellVoxel { Type = CellRecordType.Voxel };
        v.ReadHeader(r);
        v.Header = new int[8];
        for (var i = 0; i < 8; i++)
            v.Header[i] = CellBin.I(r, 0x2B + i * 4);
        var names = 0x2B + 32 + 65536; // after the header and a 32^3 x 2 byte volume
        for (var i = 0; i < 32; i++)
            v.SurfaceNames.Add(CellBin.CString(r.Slice(names + i * 64, 64))); // positional: unused slots are ""
        while (v.SurfaceNames.Count > 0 && v.SurfaceNames[^1].Length == 0)
            v.SurfaceNames.RemoveAt(v.SurfaceNames.Count - 1);
        var mo = VoxelLodTableOffset - 48; // Matrix34 before the LOD table, after a 32^3 x 4 byte volume
        var m = new float[12];
        for (var i = 0; i < 12; i++)
            m[i] = CellBin.F(r, mo + i * 4);
        v.Transform = new Matrix4x4(m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, m[3], m[7], m[11], 1);
        var ranges = CellBin.I(r, VoxelLodTableOffset);
        var p = VoxelLodTableOffset + 4;
        for (var i = 0; i < ranges; i++)
        {
            var size = CellBin.I(r, p);
            v.LodData.Add(r.Slice(p + 4, size).ToArray());
            p += 4 + size;
        }
        return v;
    }

    private static string? At(IReadOnlyList<string> list, int index) =>
        index >= 0 && index < list.Count && list[index].Length > 0 ? list[index] : null;
}

/// <summary>World-level vegetation.xml: vegetation groups (id = index) referenced by vegetation records.</summary>
public sealed class CellVegetationTable
{
    public Dictionary<int, CellVegetationGroup> Groups { get; } = [];

    public static CellVegetationTable Read(byte[] xml)
    {
        var table = new CellVegetationTable();
        var doc = System.Xml.Linq.XDocument.Parse(CellBin.XmlText(xml));
        foreach (var e in doc.Descendants("group"))
        {
            var g = new CellVegetationGroup
            {
                Id = CellXml.Int(e, "id"),
                Name = (string?)e.Attribute("name") ?? "",
                ModelPath = CellPaths.Normalize((string?)e.Attribute("modelFileName")),
                MaterialPath = CellPaths.Normalize((string?)e.Attribute("matName")),
                Brightness = CellXml.Float(e, "fBrightness", 1),
                AlignToTerrain = CellXml.Bool(e, "bAlignToTerrain"),
                RandomRotation = CellXml.Bool(e, "bRandomRotation"),
                UseTerrainColor = CellXml.Bool(e, "bUseTerrainColor"),
                CastShadow = CellXml.Bool(e, "bCastShadow"),
                Size = CellXml.Float(e, "fSize", 1),
                SizeVar = CellXml.Float(e, "fSizeVar", 0),
                Density = CellXml.Float(e, "fDensity", 1),
                Bending = CellXml.Float(e, "fBending", 0),
                MaxViewDistRatio = CellXml.Float(e, "fMaxViewDistRatio", 1),
                SpriteDistRatio = CellXml.Float(e, "fSpriteDistRatio", 1),
                MinConfigSpec = CellXml.Int(e, "minConfigSpec"),
                ElevationMin = CellXml.Float(e, "fElevationMin", 0),
                ElevationMax = CellXml.Float(e, "fElevationMax", 4096),
                SlopeMin = CellXml.Float(e, "fSlopeMin", 0),
                SlopeMax = CellXml.Float(e, "fSlopeMax", 255),
                Category = (string?)e.Attribute("category") ?? "",
            };
            table.Groups[g.Id] = g;
        }
        return table;
    }
}

public sealed class CellVegetationGroup
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>"game/" + modelFileName, or null for the few empty groups.</summary>
    public string? ModelPath { get; init; }
    /// <summary>Material override (matName) or null.</summary>
    public string? MaterialPath { get; init; }
    public float Brightness { get; init; }
    public bool AlignToTerrain { get; init; }
    public bool RandomRotation { get; init; }
    public bool UseTerrainColor { get; init; }
    public bool CastShadow { get; init; }
    public float Size { get; init; }
    public float SizeVar { get; init; }
    /// <summary>Instances per ... (editor paint / procedural density).</summary>
    public float Density { get; init; }
    public float Bending { get; init; }
    public float MaxViewDistRatio { get; init; }
    public float SpriteDistRatio { get; init; }
    public int MinConfigSpec { get; init; }
    public float ElevationMin { get; init; }
    public float ElevationMax { get; init; }
    public float SlopeMin { get; init; }
    public float SlopeMax { get; init; }
    public string Category { get; init; } = "";
}

/// <summary>World-level surface.xml: terrain surface types (the ids used by the terrain layers) and the vegetation groups
/// the engine scatters on them at run time (procedural grass, not stored in any cell file).</summary>
public sealed class CellSurfaceTypes
{
    public Dictionary<int, CellSurfaceType> ById { get; } = [];

    public static CellSurfaceTypes Read(byte[] xml)
    {
        var t = new CellSurfaceTypes();
        var doc = System.Xml.Linq.XDocument.Parse(CellBin.XmlText(xml));
        foreach (var e in doc.Descendants("SurfaceType"))
        {
            var s = new CellSurfaceType
            {
                Id = CellXml.Int(e, "Id"),
                Name = (string?)e.Attribute("Name") ?? "",
                DetailMaterial = CellPaths.Normalize((string?)e.Attribute("DetailMaterial")),
                DetailScale = new Vector2(CellXml.Float(e, "DetailScaleX", 1), CellXml.Float(e, "DetailScaleY", 1)),
                ProjectionAxis = (string?)e.Attribute("ProjAxis") ?? "Z",
            };
            s.VegetationGroupIds.AddRange(e.Elements("VegetationGroup").Select(v => CellXml.Int(v, "Id")));
            t.ById[s.Id] = s;
        }
        return t;
    }
}

public sealed class CellSurfaceType
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? DetailMaterial { get; init; }
    public Vector2 DetailScale { get; init; }
    public string ProjectionAxis { get; init; } = "Z";
    /// <summary>vegetation.xml groups generated procedurally on this surface at run time.</summary>
    public List<int> VegetationGroupIds { get; } = [];
}

/// <summary>Path helpers: pak paths are lower-case with forward slashes and start with "game/".</summary>
public static class CellPaths
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var p = path.Trim().Replace('\\', '/').ToLowerInvariant().TrimStart('/');
        while (p.Contains("//"))
            p = p.Replace("//", "/");
        return p.StartsWith("game/", StringComparison.Ordinal) ? p : "game/" + p;
    }

    /// <summary>Material path as stored (no extension) to its .mtl pak path.</summary>
    public static string? MaterialFile(string? material) =>
        material == null ? null : material.EndsWith(".mtl", StringComparison.Ordinal) ? material : material + ".mtl";
}

internal static class CellGeometry
{
    /// <summary>Ear-clipping triangulation of a simple polygon in the XY plane; returns counter-clockwise (seen from +Z) triangles.</summary>
    public static int[] TriangulatePolygon(IReadOnlyList<Vector3> pts)
    {
        var n = pts.Count;
        if (n > 1 && Vector3.DistanceSquared(pts[0], pts[n - 1]) < 1e-8f)
            n--; // closed ring
        if (n < 3)
            return [];
        double area = 0;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        var ring = Enumerable.Range(0, n).ToList();
        if (area < 0)
            ring.Reverse();
        var result = new List<int>();
        var guard = 0;
        while (ring.Count > 3 && guard++ < n * n)
        {
            var clipped = false;
            for (var i = 0; i < ring.Count; i++)
            {
                int ia = ring[(i + ring.Count - 1) % ring.Count], ib = ring[i], ic = ring[(i + 1) % ring.Count];
                var a = pts[ia];
                var b = pts[ib];
                var c = pts[ic];
                if (Cross(a, b, c) <= 0)
                    continue;
                var inside = false;
                foreach (var k in ring)
                {
                    if (k == ia || k == ib || k == ic)
                        continue;
                    if (InTriangle(pts[k], a, b, c))
                    {
                        inside = true;
                        break;
                    }
                }
                if (inside)
                    continue;
                result.AddRange([ia, ib, ic]);
                ring.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
                break; // degenerate / self-intersecting: fan the rest
        }
        for (var i = 1; i + 1 < ring.Count; i++)
            result.AddRange([ring[0], ring[i], ring[i + 1]]);
        return result.ToArray();
    }

    /// <summary>Flips triangles whose normal points down so every triangle faces +Z.</summary>
    public static int[] OrientUp(IReadOnlyList<Vector3> p, int[] idx)
    {
        for (var i = 0; i + 2 < idx.Length; i += 3)
            if (Cross(p[idx[i]], p[idx[i + 1]], p[idx[i + 2]]) < 0)
                (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
        return idx;
    }

    private static float Cross(Vector3 a, Vector3 b, Vector3 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool InTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c) =>
        Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0;
}

internal static class CellBin
{
    public static float F(ReadOnlySpan<byte> b, int o) => BitConverter.ToSingle(b[o..]);
    public static int I(ReadOnlySpan<byte> b, int o) => BitConverter.ToInt32(b[o..]);
    public static uint U(ReadOnlySpan<byte> b, int o) => BitConverter.ToUInt32(b[o..]);
    public static Vector3 V3(ReadOnlySpan<byte> b, int o) => new(F(b, o), F(b, o + 4), F(b, o + 8));

    public static string CString(byte[] b, int o, int max) => CString(new ReadOnlySpan<byte>(b, o, Math.Min(max, b.Length - o)));

    public static string CString(ReadOnlySpan<byte> s)
    {
        var z = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(z < 0 ? s : s[..z]).Trim();
    }

    public static string XmlText(byte[] xml) => Encoding.UTF8.GetString(xml).TrimStart('﻿');
}

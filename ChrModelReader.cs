using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace AAEmu.GodotViewer;

/// <summary>
/// One bone of a compiled ArcheAge skeleton.
/// <para>
/// Conventions: CryEngine model space (metres, Z up, the character faces +Y, its left is -X). Matrices are
/// System.Numerics <see cref="Matrix4x4"/> in the row-vector convention (<c>v' = v * M</c>, translation in
/// M41..M43); the file stores the transposed CryEngine Matrix34 (column vectors, translation in the 4th column).
/// Composition order is child first: <c>world = local * parentWorld</c>.
/// </para>
/// </summary>
public sealed class ChrBone
{
    public string Name = "";

    /// <summary>CRC32 (zlib polynomial) of the exact, case-sensitive <see cref="Name"/>. .caf controllers are keyed by it.</summary>
    public uint ControllerId;

    /// <summary>Index of the parent bone in <see cref="ChrSkeleton.Bones"/>, -1 for the root. Parents always precede children.</summary>
    public int ParentIndex = -1;

    /// <summary>Bind pose: bone space to model space.</summary>
    public Matrix4x4 BindWorld;

    /// <summary>Inverse of <see cref="BindWorld"/>: model space to bone space (the "inverse bind matrix").</summary>
    public Matrix4x4 InverseBindWorld;

    /// <summary>Bind pose relative to the parent: <c>BindWorld * Inverse(parent.BindWorld)</c> (the root: BindWorld).</summary>
    public Matrix4x4 BindLocal;
}

/// <summary>A skeleton read from the ACDC0000 (compiled bones) chunk of a .chr.</summary>
public sealed class ChrSkeleton
{
    public List<ChrBone> Bones = [];

    private Dictionary<string, int> _byName;
    private Dictionary<uint, int> _byController;

    public int Count => Bones.Count;

    /// <summary>Bone index by name (case-insensitive), -1 when missing.</summary>
    public int IndexOf(string name)
    {
        if (_byName == null)
        {
            _byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Bones.Count; i++)
                _byName.TryAdd(Bones[i].Name, i);
        }
        return name != null && _byName.TryGetValue(name, out var index) ? index : -1;
    }

    /// <summary>Bone index for a .caf controller id (CRC32 of the bone name), -1 when missing.</summary>
    public int IndexOfController(uint controllerId)
    {
        if (_byController == null)
        {
            _byController = [];
            for (var i = 0; i < Bones.Count; i++)
                _byController.TryAdd(Bones[i].ControllerId, i);
        }
        return _byController.TryGetValue(controllerId, out var index) ? index : -1;
    }

    /// <summary>A copy of the bind pose in parent-relative form (input for <see cref="LocalToWorld"/>).</summary>
    public Matrix4x4[] BindLocalPose()
    {
        var a = new Matrix4x4[Bones.Count];
        for (var i = 0; i < a.Length; i++)
            a[i] = Bones[i].BindLocal;
        return a;
    }

    /// <summary>Model-space bone transforms from parent-relative ones (<c>world[i] = local[i] * world[parent]</c>).</summary>
    public Matrix4x4[] LocalToWorld(ReadOnlySpan<Matrix4x4> local)
    {
        var world = new Matrix4x4[Bones.Count];
        for (var i = 0; i < world.Length; i++)
        {
            var p = Bones[i].ParentIndex;
            world[i] = p >= 0 ? local[i] * world[p] : local[i];
        }
        return world;
    }

    /// <summary>
    /// Skinning matrices for a posed skeleton: <c>InverseBindWorld * world</c>. A bind-space vertex v is drawn at
    /// <c>sum(weight_k * (v * skin[bone_k]))</c>. For the bind pose these are identity.
    /// </summary>
    public Matrix4x4[] SkinningMatrices(ReadOnlySpan<Matrix4x4> world)
    {
        var m = new Matrix4x4[Bones.Count];
        for (var i = 0; i < m.Length; i++)
            m[i] = Bones[i].InverseBindWorld * world[i];
        return m;
    }

    /// <summary>
    /// Maps this skeleton's bones onto another one by name (case-insensitive): result[i] is the index in
    /// <paramref name="master"/> or -1. CA_SKIN attachments (body, face, hair, armour) carry their own partial copy
    /// of the character skeleton and are animated by the master skeleton's bones of the same name.
    /// </summary>
    public int[] MapTo(ChrSkeleton master)
    {
        var map = new int[Bones.Count];
        for (var i = 0; i < map.Length; i++)
            map[i] = master.IndexOf(Bones[i].Name);
        return map;
    }

    /// <summary>CRC32 as used for controller ids (zlib/IEEE polynomial, on the exact bytes of the name).</summary>
    public static uint Crc32(string name)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in Encoding.Latin1.GetBytes(name))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}

/// <summary>One subset (material slot) of a skinned mesh, with its own compact vertex arrays.</summary>
public sealed class ChrSubmesh
{
    /// <summary>Bind-pose positions in metres, CryEngine model space (Z up, facing +Y).</summary>
    public Vector3[] Positions = [];

    /// <summary>Unit normals (bind pose), or null.</summary>
    public Vector3[] Normals;

    /// <summary>Texture coordinates, D3D convention (v down, like Godot), or null.</summary>
    public Vector2[] UVs;

    /// <summary>Tangent frame as in <see cref="CgfSubmesh.Tangents"/>, or null.</summary>
    public Vector4[] Tangents;

    /// <summary>Vertex colours 0..1, or null (present in about 7% of files).</summary>
    public Vector4[] Colors;

    /// <summary>Triangle list, counter-clockwise front faces (reverse for Godot, see <see cref="ChrModel.FlipWinding"/>).</summary>
    public int[] Indices = [];

    /// <summary>Index into the .mtl SubMaterials list (positional).</summary>
    public int MaterialIndex;

    /// <summary>
    /// 4 bone indices per vertex (length 4 * vertex count) into <see cref="ChrModel.Skeleton"/> (or the master
    /// skeleton after <see cref="ChrModel.RemapBones"/>). Unused slots are 0 with weight 0. Godot: ARRAY_BONES.
    /// </summary>
    public int[] BoneIndices = [];

    /// <summary>4 weights per vertex matching <see cref="BoneIndices"/>, sorted descending, summing to 1. Godot: ARRAY_WEIGHTS.</summary>
    public float[] BoneWeights = [];

    /// <summary>For each vertex, its index in the file's mesh (the space <see cref="ChrMorphTarget.Vertices"/> uses).</summary>
    public int[] SourceVertices = [];
}

/// <summary>A blend shape of the render mesh (face customisation sliders, visemes, expressions).</summary>
public sealed class ChrMorphTarget
{
    /// <summary>Name as stored, with the leading '#' removed (e.g. "eye_Size_max", "m_a").</summary>
    public string Name = "";

    /// <summary>Mesh vertex indices (see <see cref="ChrSubmesh.SourceVertices"/>).</summary>
    public int[] Vertices = [];

    /// <summary>Position offsets in metres (model space) at weight 1.</summary>
    public Vector3[] Deltas = [];
}

public sealed class ChrModel
{
    /// <summary>
    /// The file's skeleton. Every non-LOD .chr has one; some _lod1/_lod2 files have none and use the bone list of
    /// the LOD0 file (pass it to <see cref="ChrModelReader.Read(byte[], ChrSkeleton)"/>).
    /// </summary>
    public ChrSkeleton Skeleton = new();

    public List<ChrSubmesh> Submeshes = [];

    /// <summary>Material file name from the MtlName chunk (no folder or extension); see <see cref="MtlReader.ResolveModelMaterial"/>.</summary>
    public string MaterialName = "";

    public List<CgfMaterialSlot> MaterialSlots = [];

    public List<ChrMorphTarget> MorphTargets = [];

    /// <summary>Vertex count of the file's mesh (the index space of <see cref="ChrSubmesh.SourceVertices"/>).</summary>
    public int SourceVertexCount;

    public Vector3 BoundsMin, BoundsMax;

    public uint FileVersion;

    /// <summary>"intskin" when weights came from ACDC0005/0006 (normal case), "stream" when from the bone-mapping stream.</summary>
    public string SkinSource = "";

    public List<string> Warnings = [];

    /// <summary>Chunks met, "ACDC0000/801" style, for diagnostics.</summary>
    public SortedSet<string> ChunkTypes = [];

    public void FlipWinding()
    {
        foreach (var s in Submeshes)
            for (var i = 0; i + 2 < s.Indices.Length; i += 3)
                (s.Indices[i + 1], s.Indices[i + 2]) = (s.Indices[i + 2], s.Indices[i + 1]);
    }

    /// <summary>
    /// How this model is skinned by a master skeleton (the CDF's base .chr): for each bone of <see cref="Skeleton"/>
    /// (the index space of <see cref="ChrSubmesh.BoneIndices"/>) the master bone that drives it (matched by name) and
    /// the inverse bind matrix to use; the skinning matrix is <c>InverseBind * masterWorld[MasterBone]</c>.
    /// <para>
    /// Default (CryEngine behaviour): the master's inverse bind, i.e. the part's vertices are taken to be in the
    /// master's bind space. Races that borrow another race's face mesh rely on it: dwarf, elf and hariharan males wear
    /// nu_m_face01.chr and their *_targets.xml apply a race morph with Apply="always" (dwarf_Morph moves every face
    /// vertex by about -0.61 m in Z: the nuian face centroid goes from 1.772 m to 1.165 m, 3.6 cm above the dwarf's
    /// Bip01 Head, as it is 3.9 cm above the nuian's). <paramref name="ownBindPose"/> = true uses the part's own inverse binds
    /// instead (rigidly re-targets a part authored on another skeleton; do not combine with the race morphs).
    /// For bodies, hair and armour authored on the same skeleton both agree to 5e-4 m.
    /// </para>
    /// Bones missing from the master follow their nearest mapped ancestor rigidly; MasterBone is -1 when none exists.
    /// Godot: one Skin per part with <c>AddBind(MasterBone, InverseBind)</c> per entry, bone indices unchanged.
    /// </summary>
    public (int MasterBone, Matrix4x4 InverseBind)[] SkinBinds(ChrSkeleton master, bool ownBindPose = false)
    {
        var map = ReferenceEquals(Skeleton, master) ? null : Skeleton.MapTo(master);
        var binds = new (int, Matrix4x4)[Skeleton.Count];
        for (var b = 0; b < binds.Length; b++)
        {
            if (map == null)
            {
                binds[b] = (b, Skeleton.Bones[b].InverseBindWorld);
                continue;
            }
            var a = b;
            while (a >= 0 && map[a] < 0)
                a = Skeleton.Bones[a].ParentIndex;
            if (a < 0)
                binds[b] = (-1, Matrix4x4.Identity);
            else
                binds[b] = (map[a], ownBindPose ? Skeleton.Bones[a].InverseBindWorld : master.Bones[map[a]].InverseBindWorld);
        }
        return binds;
    }

    /// <summary>Skinning matrices for this model's bone indices, given the master skeleton's model-space pose (see <see cref="SkinBinds"/>).</summary>
    public Matrix4x4[] SkinningMatrices(ChrSkeleton master, ReadOnlySpan<Matrix4x4> masterWorld, bool ownBindPose = false)
    {
        var binds = SkinBinds(master, ownBindPose);
        var m = new Matrix4x4[binds.Length];
        for (var b = 0; b < m.Length; b++)
            m[b] = binds[b].MasterBone >= 0 ? binds[b].InverseBind * masterWorld[binds[b].MasterBone] : Matrix4x4.Identity;
        return m;
    }

    /// <summary>
    /// Rewrites every submesh's bone indices through <paramref name="map"/> (from <see cref="ChrSkeleton.MapTo"/>) so
    /// they index the master skeleton directly (one shared skin; equivalent to <see cref="SkinBinds"/> with the default
    /// master bind pose, except that unmapped bones are dropped instead of following their ancestor).
    /// Influences whose bone is missing from the master (-1) are dropped and the rest renormalised.
    /// Returns the number of dropped influences.
    /// </summary>
    public int RemapBones(int[] map)
    {
        var dropped = 0;
        foreach (var s in Submeshes)
        {
            var n = s.BoneIndices.Length / 4;
            for (var v = 0; v < n; v++)
            {
                float sum = 0;
                for (var k = 0; k < 4; k++)
                {
                    var i = v * 4 + k;
                    var b = s.BoneIndices[i];
                    var mapped = b >= 0 && b < map.Length ? map[b] : -1;
                    if (mapped < 0)
                    {
                        if (s.BoneWeights[i] > 0)
                            dropped++;
                        s.BoneIndices[i] = 0;
                        s.BoneWeights[i] = 0;
                    }
                    else
                        s.BoneIndices[i] = mapped;
                    sum += s.BoneWeights[i];
                }
                if (sum > 0 && MathF.Abs(sum - 1) > 1e-6f)
                    for (var k = 0; k < 4; k++)
                        s.BoneWeights[v * 4 + k] /= sum;
            }
        }
        return dropped;
    }

    /// <summary>Adds weight * deltas of a morph target to the bind-pose positions of every submesh.</summary>
    public void ApplyMorphTarget(ChrMorphTarget target, float weight)
    {
        if (target == null || weight == 0)
            return;
        var delta = new Dictionary<int, Vector3>(target.Vertices.Length);
        for (var i = 0; i < target.Vertices.Length; i++)
            delta[target.Vertices[i]] = target.Deltas[i];
        foreach (var s in Submeshes)
            for (var v = 0; v < s.SourceVertices.Length; v++)
                if (delta.TryGetValue(s.SourceVertices[v], out var d))
                    s.Positions[v] += d * weight;
    }

    public ChrMorphTarget FindMorphTarget(string name)
    {
        foreach (var t in MorphTargets)
            if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }
}

/// <summary>
/// Reader for ArcheAge skinned meshes (.chr): CryEngine 3 chunk files (0x745 table, file type 0xFFFF0000) with one
/// render mesh plus compiled skinning chunks. No Godot dependency.
/// <para>Layout (verified on 1041 sampled .chr files, see the report):</para>
/// <list type="bullet">
/// <item>Render mesh: the same Mesh 0x800 / DataStream 0x800 / MeshSubsets 0x800 / Node 0x823 chunks as .cgf. Streams:
///   positions float3, normals float3, uvs float2, colours RGBA8 (rare), indices uint16, tangents int16x8, bone
///   mapping (type 9: 4 x uint8 subset-local bone index + 4 x uint8 weight summing to 255). The subsets chunk has
///   flag 2 and after the 36-byte subset records one 260-byte table per subset (int count + 128 x uint16 bone ids)
///   that maps the stream's local indices to skeleton bones. Exactly one mesh per file; its node transform is
///   identity and ignored.</item>
/// <item>ACDC0000 v0x801 compiled bones (differs from stock CE3 0x800's 584-byte records): 32 zero bytes, then
///   324-byte records: CryBonePhysics_Comp[2] (208 bytes), controller id (CRC32 of the name) @208, int @212 (0),
///   name[48] @216, parent offset @264 (relative, 0 = root), child count @268, first-child offset @272,
///   bone-to-model Matrix34 @276 (row-major 3x4, column vectors, translation in the 4th column, metres).</item>
/// <item>ACDC0005 v0x801 internal skin vertices: 32 zero bytes, then 40-byte records {float3 position, uint16
///   bone[4], float weight[4], uint32 colour}; bone ids index the file's skeleton, weights sum to 1.
///   ACDC0006 (uint16 per mesh vertex) maps each render vertex to its internal vertex. The internal positions are
///   the render positions turned 180 degrees about Z (983 of 1039 files; other exports differ), so positions come
///   from the render stream and only the skinning from ACDC0005.</item>
/// <item>ACDC0002 v0x801 morph targets: uint32 count, then per target {uint32 meshId, nameLength, numInt, numExt;
///   name (nameLength bytes incl. zero, unaligned); numInt + numExt records of {uint32 vertex, float3 delta}}. The
///   external records index render vertices. Chunks with count 0 sometimes carry a garbage version.</item>
/// <item>Ignored: ACDC0001 (physical bones), ACDC0003 (physics proxies), ACDC0004 (internal faces), ACDC0007/0008,
///   AAFC0004 (bone boxes), CCCC0008 (scene props), CCCC000E (timing), CCCC0013/0015.</item>
/// </list>
/// </summary>
public static class ChrModelReader
{
    public const uint ChunkCompiledBones = 0xACDC0000, ChunkCompiledMorphTargets = 0xACDC0002,
        ChunkCompiledIntSkinVertices = 0xACDC0005, ChunkCompiledExt2IntMap = 0xACDC0006;

    public const int StreamBoneMapping = 9;

    private const int BoneRecordSize = 324, IntSkinVertexSize = 40, CompiledHeaderSize = 32;

    public static ChrModel Read(byte[] file) => Read(file, null);

    /// <param name="file">The .chr bytes.</param>
    /// <param name="fallbackSkeleton">Skeleton to use when the file has no ACDC0000 chunk (LOD files): pass the LOD0 file's.</param>
    public static ChrModel Read(byte[] file, ChrSkeleton fallbackSkeleton)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < 20 || Encoding.ASCII.GetString(file, 0, 6) != "CryTek")
            throw new InvalidDataException("not a CryTek chunk file");
        var fileType = CgfModelReader.U32(file, 8);
        var version = CgfModelReader.U32(file, 12);
        if (fileType != 0xFFFF0000)
            throw new InvalidDataException($"not a geometry file (type 0x{fileType:X8})");
        var chunks = CgfModelReader.ReadChunkTable(file, version, CgfModelReader.I32(file, 16));
        var byId = new Dictionary<int, CgfModelReader.Chunk>();
        foreach (var c in chunks)
            byId.TryAdd(c.Id, c);

        var model = new ChrModel { FileVersion = version };
        var scratch = new CgfModel(); // receives MtlName warnings
        foreach (var c in chunks)
            model.ChunkTypes.Add($"{c.Type:X8}/{c.Version:X}");

        // Skeleton.
        foreach (var c in chunks)
            if (c.Type == ChunkCompiledBones)
            {
                model.Skeleton = ReadBones(file, c, model);
                break;
            }
        if (model.Skeleton.Count == 0 && fallbackSkeleton != null)
            model.Skeleton = fallbackSkeleton;

        // Material names.
        var mtlNames = new Dictionary<int, CgfModelReader.MtlNameData>();
        foreach (var c in chunks)
            if (c.Type == CgfModelReader.ChunkMtlName)
                mtlNames[c.Id] = CgfModelReader.ReadMtlName(file, c, scratch);
        model.Warnings.AddRange(scratch.Warnings);

        // The mesh node.
        CgfModelReader.Chunk meshChunk = default;
        var meshMtlId = -1;
        var found = false;
        foreach (var c in chunks)
        {
            if (c.Type != CgfModelReader.ChunkNode)
                continue;
            var p = CgfModelReader.Payload(file, c, out var size);
            if (c.Version != 0x823 || size < 204)
                continue;
            var objectId = CgfModelReader.I32(file, p + 64);
            if (!byId.TryGetValue(objectId, out var obj) || obj.Type != CgfModelReader.ChunkMesh)
                continue;
            if (found)
            {
                model.Warnings.Add($"extra mesh node '{CgfModelReader.CString(file, p, 64)}' ignored");
                continue;
            }
            meshChunk = obj;
            meshMtlId = CgfModelReader.I32(file, p + 76);
            found = true;
        }
        if (!found)
        {
            model.Warnings.Add("no mesh node");
            return model;
        }

        // Material slots.
        if (mtlNames.TryGetValue(meshMtlId, out var top))
        {
            model.MaterialName = top.Name;
            if (top.InlineSlots != null)
                model.MaterialSlots = [.. top.InlineSlots];
            else if (top.SubChunkIds.Length == 0)
                model.MaterialSlots.Add(new CgfMaterialSlot { Name = top.Name, PhysicalizeType = top.PhysicalizeType });
            else
                foreach (var sid in top.SubChunkIds)
                    model.MaterialSlots.Add(mtlNames.TryGetValue(sid, out var sub)
                        ? new CgfMaterialSlot { Name = sub.Name, PhysicalizeType = sub.PhysicalizeType }
                        : new CgfMaterialSlot { PhysicalizeType = -1 });
        }
        else
            foreach (var m in mtlNames.Values)
                if ((m.Flags & 2) == 0)
                {
                    model.MaterialName = m.Name;
                    break;
                }

        var mesh = CgfModelReader.ReadMesh(file, meshChunk);
        if (mesh == null)
        {
            model.Warnings.Add($"mesh chunk version 0x{meshChunk.Version:X} not supported");
            return model;
        }
        ReadGeometry(file, byId, chunks, mesh, model);
        return model;
    }

    /// <summary>Reads only the skeleton (cheap: skips geometry). Returns an empty skeleton when the file has none.</summary>
    public static ChrSkeleton ReadSkeleton(byte[] file)
    {
        var version = CgfModelReader.U32(file, 12);
        var chunks = CgfModelReader.ReadChunkTable(file, version, CgfModelReader.I32(file, 16));
        var model = new ChrModel();
        foreach (var c in chunks)
            if (c.Type == ChunkCompiledBones)
                return ReadBones(file, c, model);
        return new ChrSkeleton();
    }

    private static ChrSkeleton ReadBones(byte[] f, CgfModelReader.Chunk c, ChrModel model)
    {
        var skeleton = new ChrSkeleton();
        var p = CgfModelReader.Payload(f, c, out var size);
        if (c.Version != 0x801 || size < CompiledHeaderSize || (size - CompiledHeaderSize) % BoneRecordSize != 0)
        {
            model.Warnings.Add($"compiled bones chunk version 0x{c.Version:X} size {size} not understood");
            return skeleton;
        }
        var n = (size - CompiledHeaderSize) / BoneRecordSize;
        for (var i = 0; i < n; i++)
        {
            var r = p + CompiledHeaderSize + i * BoneRecordSize;
            var parentOffset = CgfModelReader.I32(f, r + 264);
            var bone = new ChrBone
            {
                ControllerId = CgfModelReader.U32(f, r + 208),
                Name = CgfModelReader.CString(f, r + 216, 48),
                ParentIndex = parentOffset == 0 ? -1 : i + parentOffset,
            };
            // Matrix34, row-major, column vectors: rows (m00 m01 m02 m03) (m10 ...) (m20 ...).
            var m = new float[12];
            for (var k = 0; k < 12; k++)
                m[k] = CgfModelReader.F32(f, r + 276 + k * 4);
            bone.BindWorld = new Matrix4x4(
                m[0], m[4], m[8], 0,
                m[1], m[5], m[9], 0,
                m[2], m[6], m[10], 0,
                m[3], m[7], m[11], 1);
            if (bone.ParentIndex < -1 || bone.ParentIndex >= i)
            {
                model.Warnings.Add($"bone {i} '{bone.Name}' has parent {bone.ParentIndex} out of order; treated as root");
                bone.ParentIndex = -1;
            }
            skeleton.Bones.Add(bone);
        }
        foreach (var bone in skeleton.Bones)
        {
            // The stored rotations are orthonormal to ~1e-6; invert the rigid transform exactly.
            bone.InverseBindWorld = InvertRigid(bone.BindWorld);
        }
        foreach (var bone in skeleton.Bones)
            bone.BindLocal = bone.ParentIndex >= 0
                ? bone.BindWorld * skeleton.Bones[bone.ParentIndex].InverseBindWorld
                : bone.BindWorld;
        return skeleton;
    }

    /// <summary>Inverse of a rotation + translation matrix (row-vector convention).</summary>
    public static Matrix4x4 InvertRigid(Matrix4x4 m)
    {
        if (Matrix4x4.Invert(m, out var inv))
            return inv;
        var r = Matrix4x4.Transpose(new Matrix4x4(m.M11, m.M12, m.M13, 0, m.M21, m.M22, m.M23, 0, m.M31, m.M32, m.M33, 0, 0, 0, 0, 1));
        var t = -Vector3.TransformNormal(new Vector3(m.M41, m.M42, m.M43), r);
        r.M41 = t.X;
        r.M42 = t.Y;
        r.M43 = t.Z;
        return r;
    }

    private static void ReadGeometry(byte[] f, Dictionary<int, CgfModelReader.Chunk> byId, List<CgfModelReader.Chunk> chunks,
        CgfModelReader.MeshData mesh, ChrModel model)
    {
        Vector3[] pos = null, nrm = null;
        Vector2[] uv = null;
        Vector4[] tan = null, col = null;
        int[] idx = null;
        byte[] boneMap = null;

        for (var st = 0; st < 16; st++)
        {
            var sid = mesh.StreamChunkIds[st];
            if (sid <= 0 || !byId.TryGetValue(sid, out var sc) || sc.Type != CgfModelReader.ChunkDataStream)
                continue;
            var p = CgfModelReader.Payload(f, sc, out var size);
            var type = CgfModelReader.I32(f, p + 4);
            var count = CgfModelReader.I32(f, p + 8);
            var es = CgfModelReader.I32(f, p + 12);
            var d = p + 24;
            if (count < 0 || es <= 0 || (long)count * es > size - 24)
                continue;
            switch (type)
            {
                case CgfModelReader.StreamPositions when es == 12:
                    pos = CgfModelReader.ReadVec3(f, d, count, 12);
                    break;
                case CgfModelReader.StreamPositions when es == 8:
                    pos = CgfModelReader.ReadHalf3(f, d, count, 8);
                    break;
                case CgfModelReader.StreamNormals when es == 12:
                    nrm = CgfModelReader.ReadVec3(f, d, count, 12);
                    break;
                case CgfModelReader.StreamTexCoords when es == 8:
                    uv = CgfModelReader.ReadVec2(f, d, count);
                    break;
                case CgfModelReader.StreamTexCoords when es == 4:
                    uv = CgfModelReader.ReadHalf2(f, d, count, 4, 0);
                    break;
                case CgfModelReader.StreamColors when es == 4:
                    col = CgfModelReader.ReadColors(f, d, count, 4, 0);
                    break;
                case CgfModelReader.StreamIndices when es == 2:
                    idx = CgfModelReader.ReadU16(f, d, count);
                    break;
                case CgfModelReader.StreamIndices when es == 4:
                    idx = CgfModelReader.ReadI32Array(f, d, count);
                    break;
                case CgfModelReader.StreamTangents when es == 16:
                    tan = CgfModelReader.ReadTangents(f, d, count);
                    break;
                case StreamBoneMapping when es == 8:
                    boneMap = new byte[count * 8];
                    Buffer.BlockCopy(f, d, boneMap, 0, count * 8);
                    break;
                default:
                    if (type != 8) // type 8 (element size 0) appears in a few files and holds nothing
                        model.Warnings.Add($"stream type {type} element size {es} ignored");
                    break;
            }
        }
        if (pos == null || idx == null)
        {
            model.Warnings.Add("missing position or index stream");
            return;
        }
        var nv = pos.Length;
        model.SourceVertexCount = nv;
        if (nrm != null && nrm.Length != nv) nrm = null;
        if (uv != null && uv.Length != nv) uv = null;
        if (tan != null && tan.Length != nv) tan = null;
        if (col != null && col.Length != nv) col = null;

        var subsets = CgfModelReader.ReadSubsets(f, byId, mesh.SubsetsChunkId);
        if (subsets.Count == 0)
            subsets.Add(new CgfModelReader.Subset { FirstIndex = 0, NumIndices = idx.Length, FirstVert = 0, NumVerts = nv, MatId = 0 });

        // Skinning per render vertex: 4 bones (file skeleton indices) + weights.
        var skinBones = new int[nv * 4];
        var skinWeights = new float[nv * 4];
        if (ReadIntSkin(f, chunks, nv, skinBones, skinWeights, model))
            model.SkinSource = "intskin";
        else if (boneMap != null && boneMap.Length == nv * 8 && ReadStreamSkin(f, byId, mesh.SubsetsChunkId, subsets, idx, boneMap, skinBones, skinWeights))
            model.SkinSource = "stream";
        else
        {
            model.SkinSource = "none";
            model.Warnings.Add("no skinning data; every vertex bound to bone 0");
            for (var v = 0; v < nv; v++)
                skinWeights[v * 4] = 1;
        }
        SortInfluences(skinBones, skinWeights);

        foreach (var c in chunks)
            if (c.Type == ChunkCompiledMorphTargets)
                ReadMorphTargets(f, c, nv, model);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var remap = new int[nv];
        foreach (var s in subsets)
        {
            if (s.NumIndices <= 0)
                continue;
            if (s.MatId >= 0 && s.MatId < model.MaterialSlots.Count && model.MaterialSlots[s.MatId].IsPhysicsProxy)
                continue;
            var start = Math.Max(0, s.FirstIndex);
            var end = Math.Min(idx.Length, (long)s.FirstIndex + s.NumIndices);
            var triCount = (int)Math.Max(0, (end - start) / 3);
            Array.Fill(remap, -1);
            var verts = new List<int>();
            var outIdx = new List<int>(triCount * 3);
            var bad = 0;
            for (var t = 0; t < triCount; t++)
            {
                int a = idx[start + t * 3], b = idx[start + t * 3 + 1], c = idx[start + t * 3 + 2];
                if ((uint)a >= (uint)nv || (uint)b >= (uint)nv || (uint)c >= (uint)nv)
                {
                    bad++;
                    continue;
                }
                outIdx.Add(Remap(a, remap, verts));
                outIdx.Add(Remap(b, remap, verts));
                outIdx.Add(Remap(c, remap, verts));
            }
            if (bad > 0)
                model.Warnings.Add($"{bad} triangles with out-of-range indices dropped");
            if (outIdx.Count == 0)
                continue;
            var n = verts.Count;
            var sm = new ChrSubmesh
            {
                MaterialIndex = s.MatId,
                Indices = outIdx.ToArray(),
                Positions = new Vector3[n],
                Normals = nrm != null ? new Vector3[n] : null,
                UVs = uv != null ? new Vector2[n] : null,
                Tangents = tan != null ? new Vector4[n] : null,
                Colors = col != null ? new Vector4[n] : null,
                BoneIndices = new int[n * 4],
                BoneWeights = new float[n * 4],
                SourceVertices = verts.ToArray(),
            };
            for (var i = 0; i < n; i++)
            {
                var v = verts[i];
                sm.Positions[i] = pos[v];
                min = Vector3.Min(min, pos[v]);
                max = Vector3.Max(max, pos[v]);
                if (sm.Normals != null) sm.Normals[i] = CgfModelReader.SafeNormalize(nrm[v]);
                if (sm.UVs != null) sm.UVs[i] = uv[v];
                if (sm.Tangents != null) sm.Tangents[i] = tan[v];
                if (sm.Colors != null) sm.Colors[i] = col[v];
                Array.Copy(skinBones, v * 4, sm.BoneIndices, i * 4, 4);
                Array.Copy(skinWeights, v * 4, sm.BoneWeights, i * 4, 4);
            }
            model.Submeshes.Add(sm);
        }
        if (model.Submeshes.Count > 0)
        {
            model.BoundsMin = min;
            model.BoundsMax = max;
        }
    }

    /// <summary>ACDC0005 (internal skin vertices) through ACDC0006 (render vertex -> internal vertex).</summary>
    private static bool ReadIntSkin(byte[] f, List<CgfModelReader.Chunk> chunks, int nv, int[] bones, float[] weights, ChrModel model)
    {
        CgfModelReader.Chunk? intChunk = null, mapChunk = null;
        foreach (var c in chunks)
        {
            if (c.Type == ChunkCompiledIntSkinVertices) intChunk = c;
            else if (c.Type == ChunkCompiledExt2IntMap) mapChunk = c;
        }
        if (intChunk == null || mapChunk == null)
            return false;
        var ip = CgfModelReader.Payload(f, intChunk.Value, out var isize);
        var mp = CgfModelReader.Payload(f, mapChunk.Value, out var msize);
        if (intChunk.Value.Version != 0x801 || (isize - CompiledHeaderSize) % IntSkinVertexSize != 0)
        {
            model.Warnings.Add($"int skin chunk version 0x{intChunk.Value.Version:X} size {isize} not understood");
            return false;
        }
        var nInt = (isize - CompiledHeaderSize) / IntSkinVertexSize;
        if (msize < nv * 2)
        {
            model.Warnings.Add($"ext2int map has {msize / 2} entries for {nv} vertices");
            return false;
        }
        var boneCount = model.Skeleton.Count;
        var badBone = 0;
        for (var v = 0; v < nv; v++)
        {
            var iv = f[mp + v * 2] | (f[mp + v * 2 + 1] << 8);
            if (iv >= nInt)
            {
                model.Warnings.Add($"ext2int entry {v} = {iv} beyond {nInt} internal vertices");
                return false;
            }
            var r = ip + CompiledHeaderSize + iv * IntSkinVertexSize;
            for (var k = 0; k < 4; k++)
            {
                var b = f[r + 12 + k * 2] | (f[r + 13 + k * 2] << 8);
                var w = CgfModelReader.F32(f, r + 20 + k * 4);
                if (w <= 0)
                {
                    b = 0;
                    w = 0;
                }
                else if (boneCount > 0 && b >= boneCount)
                {
                    badBone++;
                    b = 0;
                    w = 0;
                }
                bones[v * 4 + k] = b;
                weights[v * 4 + k] = w;
            }
            Normalize4(weights, v * 4);
        }
        if (badBone > 0)
            model.Warnings.Add($"{badBone} skin influences reference bones beyond the skeleton");
        return true;
    }

    /// <summary>
    /// Fallback: bone-mapping stream (subset-local uint8 indices) + the per-subset bone tables of the subsets chunk.
    /// A vertex's subset is the first subset whose triangles use it (subset vertex ranges overlap in some files).
    /// </summary>
    private static bool ReadStreamSkin(byte[] f, Dictionary<int, CgfModelReader.Chunk> byId, int subsetsChunkId,
        List<CgfModelReader.Subset> subsets, int[] idx, byte[] boneMap, int[] bones, float[] weights)
    {
        if (!byId.TryGetValue(subsetsChunkId, out var c))
            return false;
        var p = CgfModelReader.Payload(f, c, out var size);
        var flags = CgfModelReader.I32(f, p);
        var n = CgfModelReader.I32(f, p + 4);
        if ((flags & 2) == 0 || 16 + (long)n * (36 + 260) > size)
            return false;
        var tables = new int[n][];
        for (var s = 0; s < n; s++)
        {
            var r = p + 16 + n * 36 + s * 260;
            var count = Math.Clamp(CgfModelReader.I32(f, r), 0, 128);
            tables[s] = new int[count];
            for (var k = 0; k < count; k++)
                tables[s][k] = f[r + 4 + k * 2] | (f[r + 5 + k * 2] << 8);
        }
        var nv = boneMap.Length / 8;
        var owner = new int[nv];
        Array.Fill(owner, -1);
        for (var s = 0; s < subsets.Count && s < n; s++)
        {
            var start = Math.Max(0, subsets[s].FirstIndex);
            var end = Math.Min(idx.Length, subsets[s].FirstIndex + subsets[s].NumIndices);
            for (var i = start; i < end; i++)
                if ((uint)idx[i] < (uint)nv && owner[idx[i]] < 0)
                    owner[idx[i]] = s;
        }
        for (var v = 0; v < nv; v++)
        {
            var table = owner[v] >= 0 ? tables[owner[v]] : [];
            for (var k = 0; k < 4; k++)
            {
                int local = boneMap[v * 8 + k], w = boneMap[v * 8 + 4 + k];
                var ok = w > 0 && local < table.Length;
                bones[v * 4 + k] = ok ? table[local] : 0;
                weights[v * 4 + k] = ok ? w / 255f : 0;
            }
            Normalize4(weights, v * 4);
        }
        return true;
    }

    private static void ReadMorphTargets(byte[] f, CgfModelReader.Chunk c, int nv, ChrModel model)
    {
        var p = CgfModelReader.Payload(f, c, out var size);
        if (size < 4)
            return;
        var end = p + size;
        var count = CgfModelReader.I32(f, p);
        if (count <= 0)
            return; // empty chunks carry arbitrary version numbers
        if (c.Version != 0x801)
        {
            model.Warnings.Add($"morph target chunk version 0x{c.Version:X} with {count} targets not supported");
            return;
        }
        var q = p + 4;
        for (var t = 0; t < count; t++)
        {
            if (q + 16 > end)
            {
                model.Warnings.Add("morph target chunk truncated");
                return;
            }
            var nameLen = CgfModelReader.I32(f, q + 4);
            var nInt = CgfModelReader.I32(f, q + 8);
            var nExt = CgfModelReader.I32(f, q + 12);
            q += 16;
            if (nameLen < 0 || nInt < 0 || nExt < 0 || q + nameLen + 16L * (nInt + nExt) > end)
            {
                model.Warnings.Add("morph target record out of range");
                return;
            }
            var name = CgfModelReader.CString(f, q, nameLen).TrimStart('#');
            q += nameLen + 16 * nInt; // internal-vertex records are not needed for drawing
            var target = new ChrMorphTarget { Name = name, Vertices = new int[nExt], Deltas = new Vector3[nExt] };
            var kept = 0;
            for (var i = 0; i < nExt; i++, q += 16)
            {
                var v = CgfModelReader.I32(f, q);
                if ((uint)v >= (uint)nv)
                    continue;
                target.Vertices[kept] = v;
                target.Deltas[kept] = new Vector3(CgfModelReader.F32(f, q + 4), CgfModelReader.F32(f, q + 8), CgfModelReader.F32(f, q + 12));
                kept++;
            }
            if (kept != nExt)
            {
                model.Warnings.Add($"morph '{name}': {nExt - kept} vertices out of range");
                Array.Resize(ref target.Vertices, kept);
                Array.Resize(ref target.Deltas, kept);
            }
            model.MorphTargets.Add(target);
        }
    }

    private static void Normalize4(float[] w, int o)
    {
        var sum = w[o] + w[o + 1] + w[o + 2] + w[o + 3];
        if (sum <= 0)
        {
            w[o] = 1;
            return;
        }
        for (var k = 0; k < 4; k++)
            w[o + k] /= sum;
    }

    /// <summary>Orders each vertex's influences by descending weight (unused slots last, bone 0 weight 0).</summary>
    private static void SortInfluences(int[] bones, float[] weights)
    {
        Span<int> b = stackalloc int[4];
        Span<float> w = stackalloc float[4];
        for (var o = 0; o < bones.Length; o += 4)
        {
            for (var k = 0; k < 4; k++)
            {
                b[k] = bones[o + k];
                w[k] = weights[o + k];
            }
            for (var i = 1; i < 4; i++)
                for (var j = i; j > 0 && w[j] > w[j - 1]; j--)
                {
                    (w[j], w[j - 1]) = (w[j - 1], w[j]);
                    (b[j], b[j - 1]) = (b[j - 1], b[j]);
                }
            for (var k = 0; k < 4; k++)
            {
                bones[o + k] = w[k] > 0 ? b[k] : 0;
                weights[o + k] = w[k];
            }
        }
    }

    private static int Remap(int v, int[] remap, List<int> verts)
    {
        if (remap[v] < 0)
        {
            remap[v] = verts.Count;
            verts.Add(v);
        }
        return remap[v];
    }
}

/// <summary>
/// Armour "cut" volumes. Armour and costume .mtl files carry a sub-material with Shader="Cut" whose geometry is a set
/// of closed boxes (8 vertices / 12 triangles each) around the part of the body the piece covers (verified on the
/// sampled armour; e.g. the Lucius costume has one box around the head). They are not drawn; ArcheAge uses them to
/// hide the body underneath. This helper approximates that on the CPU in bind pose: body/hair triangles whose three
/// vertices are inside a volume are removed. Which parts the engine cuts (body and hair; not the face?) is inferred.
/// </summary>
public static class ChrCutVolumes
{
    public static bool IsCutMaterial(MtlMaterial m) => m != null && m.Shader.Equals("Cut", StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes triangles of <paramref name="target"/> lying entirely inside any closed <paramref name="volumes"/> mesh. Returns the count removed.</summary>
    public static int RemoveInside(ChrModel target, IReadOnlyList<ChrSubmesh> volumes)
    {
        if (volumes == null || volumes.Count == 0)
            return 0;
        var removed = 0;
        foreach (var s in target.Submeshes)
        {
            var inside = new bool[s.Positions.Length];
            for (var v = 0; v < inside.Length; v++)
                foreach (var vol in volumes)
                    if (Contains(vol, s.Positions[v]))
                    {
                        inside[v] = true;
                        break;
                    }
            var kept = new List<int>(s.Indices.Length);
            for (var t = 0; t + 2 < s.Indices.Length; t += 3)
            {
                if (inside[s.Indices[t]] && inside[s.Indices[t + 1]] && inside[s.Indices[t + 2]])
                {
                    removed++;
                    continue;
                }
                kept.Add(s.Indices[t]);
                kept.Add(s.Indices[t + 1]);
                kept.Add(s.Indices[t + 2]);
            }
            s.Indices = kept.ToArray();
        }
        return removed;
    }

    /// <summary>Point-in-closed-mesh test by ray parity (a slightly skewed +X ray avoids edge hits).</summary>
    public static bool Contains(ChrSubmesh volume, Vector3 p)
    {
        var dir = Vector3.Normalize(new Vector3(1, 0.00131f, 0.00077f));
        var crossings = 0;
        var pos = volume.Positions;
        var idx = volume.Indices;
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            Vector3 a = pos[idx[t]], b = pos[idx[t + 1]], c = pos[idx[t + 2]];
            Vector3 e1 = b - a, e2 = c - a;
            var h = Vector3.Cross(dir, e2);
            var det = Vector3.Dot(e1, h);
            if (MathF.Abs(det) < 1e-12f)
                continue;
            var inv = 1 / det;
            var sv = p - a;
            var u = inv * Vector3.Dot(sv, h);
            if (u < 0 || u > 1)
                continue;
            var q = Vector3.Cross(sv, e1);
            var w = inv * Vector3.Dot(dir, q);
            if (w < 0 || u + w > 1)
                continue;
            if (inv * Vector3.Dot(e2, q) > 0)
                crossings++;
        }
        return (crossings & 1) == 1;
    }
}

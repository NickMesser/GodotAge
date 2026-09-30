using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace AAEmu.GodotViewer;

/// <summary>One drawable piece of a model: the triangles of one mesh subset (one material slot).</summary>
public sealed class CgfSubmesh
{
    /// <summary>Name of the node the geometry came from.</summary>
    public string NodeName = "";

    /// <summary>Model-space positions in metres, CryEngine axes (Z up).</summary>
    public Vector3[] Positions = [];

    /// <summary>Unit normals in model space, or null when the mesh has no normal stream.</summary>
    public Vector3[] Normals;

    /// <summary>Texture coordinates, D3D convention (v grows down the image, same as Godot). Null when absent.</summary>
    public Vector2[] UVs;

    /// <summary>
    /// Tangent frame, or null. XYZ = unit tangent along +dP/du. W = +1 or -1 so that binormal = cross(normal, tangent) * W
    /// points along +dP/dv (down the image), the frame CryEngine's shaders use. Godot builds its binormal the same way
    /// (cross(N, T) * w; its own generate_tangents picks w so the binormal points up the image, for OpenGL-style maps).
    /// ArcheAge normal maps are DirectX style (green = down the image), so hand these tangents to ARRAY_TANGENT unchanged
    /// and do not flip the green channel. If Godot generates tangents instead, invert green.
    /// </summary>
    public Vector4[] Tangents;

    /// <summary>Vertex colours RGBA in 0..1, or null. Often unused by the material; don't multiply blindly.</summary>
    public Vector4[] Colors;

    /// <summary>
    /// Triangle list into the arrays above. Front faces are counter-clockwise around the normal (right-handed).
    /// Godot treats clockwise as front, so reverse each triangle (or call <see cref="CgfModel.FlipWinding"/>)
    /// when building an ArrayMesh.
    /// </summary>
    public int[] Indices = [];

    /// <summary>Slot in the model's material: index into the .mtl SubMaterials list (positional, names don't matter).</summary>
    public int MaterialIndex;
}

/// <summary>Scene-graph node as stored in the file, kept for diagnostics.</summary>
public sealed class CgfNode
{
    public int ChunkId;
    public string Name = "";
    public int ParentId = -1;

    /// <summary>"mesh", "helper", "none" (object chunk missing) or the object's chunk type in hex.</summary>
    public string Kind = "";

    /// <summary>Local transform (row-vector convention, translation in metres).</summary>
    public Matrix4x4 Local;

    /// <summary>World transform relative to the file's root (row-vector convention).</summary>
    public Matrix4x4 World;

    public string Properties = "";
    public int VertexCount;
    public int IndexCount;

    /// <summary>True when the node's triangles are part of <see cref="CgfModel.Submeshes"/>.</summary>
    public bool Rendered;

    /// <summary>Why the node was not rendered (null when rendered).</summary>
    public string SkipReason;
}

/// <summary>Sub-material slot as named inside the .cgf (the .mtl file is authoritative for everything else).</summary>
public sealed class CgfMaterialSlot
{
    public string Name = "";

    /// <summary>-1 none, 0 default, 1 no-collide, 2 obstruct, 0x100 physics proxy (0x1000 is added in newer exports).</summary>
    public int PhysicalizeType;

    public bool IsPhysicsProxy => PhysicalizeType != -1 && (PhysicalizeType & 0xFFF) == 0x100;
}

public sealed class CgfModel
{
    public List<CgfSubmesh> Submeshes = [];

    /// <summary>
    /// Material file name from the MtlName chunk, without extension or folder (e.g. "cliff_rock_b").
    /// Look it up next to the .cgf; see <see cref="MtlReader.ModelMaterialPath"/>.
    /// </summary>
    public string MaterialName = "";

    /// <summary>Sub-material slots named in the .cgf (diagnostics; use the .mtl for rendering).</summary>
    public List<CgfMaterialSlot> MaterialSlots = [];

    /// <summary>Bounds of the rendered triangles in model space (metres, Z up). Zero when nothing renders.</summary>
    public Vector3 BoundsMin, BoundsMax;

    /// <summary>
    /// Bounds the engine uses: the mesh-chunk boxes of every mesh node except "$" helpers, physics proxies included.
    /// Transforming this box by a brush matrix reproduces the object.dat record AABB. It can be larger than
    /// <see cref="BoundsMin"/>/<see cref="BoundsMax"/> (proxies, stripped faces).
    /// </summary>
    public Vector3 EngineBoundsMin, EngineBoundsMax;

    public List<CgfNode> Nodes = [];

    /// <summary>True when node transforms were baked (more than one mesh node besides $lod nodes).</summary>
    public bool NodeTransformsApplied;

    /// <summary>File format version: 0x744 or 0x745.</summary>
    public uint FileVersion;

    /// <summary>Non-fatal oddities met while reading (unknown stream encodings, bad indices, ...).</summary>
    public List<string> Warnings = [];

    /// <summary>Stream encodings met in rendered meshes, e.g. "positions/12", "indices/2" (type name / element bytes).</summary>
    public SortedSet<string> Encodings = [];

    /// <summary>Swaps the 2nd and 3rd index of every triangle in place (CryEngine CCW front faces to Godot CW).</summary>
    public void FlipWinding()
    {
        foreach (var s in Submeshes)
            for (var i = 0; i + 2 < s.Indices.Length; i += 3)
                (s.Indices[i + 1], s.Indices[i + 2]) = (s.Indices[i + 2], s.Indices[i + 1]);
    }

    /// <summary>Returns the submeshes merged per material slot (one draw surface per material).</summary>
    public List<CgfSubmesh> MergedByMaterial()
    {
        var result = new List<CgfSubmesh>();
        var bySlot = new SortedDictionary<int, List<CgfSubmesh>>();
        foreach (var s in Submeshes)
        {
            if (!bySlot.TryGetValue(s.MaterialIndex, out var list))
                bySlot[s.MaterialIndex] = list = [];
            list.Add(s);
        }
        foreach (var (slot, parts) in bySlot)
        {
            if (parts.Count == 1)
            {
                result.Add(parts[0]);
                continue;
            }
            int nv = 0, ni = 0;
            foreach (var p in parts)
            {
                nv += p.Positions.Length;
                ni += p.Indices.Length;
            }
            bool hasN = parts.TrueForAll(p => p.Normals != null), hasUv = parts.TrueForAll(p => p.UVs != null);
            bool hasT = parts.TrueForAll(p => p.Tangents != null), hasC = parts.TrueForAll(p => p.Colors != null);
            var m = new CgfSubmesh
            {
                NodeName = parts[0].NodeName,
                MaterialIndex = slot,
                Positions = new Vector3[nv],
                Normals = hasN ? new Vector3[nv] : null,
                UVs = hasUv ? new Vector2[nv] : null,
                Tangents = hasT ? new Vector4[nv] : null,
                Colors = hasC ? new Vector4[nv] : null,
                Indices = new int[ni],
            };
            int vo = 0, io = 0;
            foreach (var p in parts)
            {
                p.Positions.CopyTo(m.Positions, vo);
                if (hasN) p.Normals.CopyTo(m.Normals, vo);
                if (hasUv) p.UVs.CopyTo(m.UVs, vo);
                if (hasT) p.Tangents.CopyTo(m.Tangents, vo);
                if (hasC) p.Colors.CopyTo(m.Colors, vo);
                for (var i = 0; i < p.Indices.Length; i++)
                    m.Indices[io + i] = p.Indices[i] + vo;
                vo += p.Positions.Length;
                io += p.Indices.Length;
            }
            result.Add(m);
        }
        return result;
    }
}

/// <summary>
/// Reader for ArcheAge static geometry (.cgf, CryEngine 3 chunk files, versions 0x744 and 0x745).
/// No Godot dependency: output is System.Numerics in CryEngine model space (metres, Z up).
/// <para>
/// Rendering rules (verified against 4366 brushes of three cells, see the report): a node is drawn when its object
/// is a mesh chunk with vertices and indices, its name doesn't start with '$' ($lod1.., $occlusion, $picking) and
/// doesn't contain "proxy". Subsets with no indices or with a physics-proxy material are dropped. Node transforms
/// are baked only when the file has more than one mesh node besides $lod nodes; a lone mesh is used as stored and
/// its node transform (sometimes a scale or rotation left over from 3ds Max) is ignored, exactly like the engine.
/// </para>
/// </summary>
public static class CgfModelReader
{
    public const uint ChunkMesh = 0xCCCC0000, ChunkHelper = 0xCCCC0001, ChunkNode = 0xCCCC000B;
    public const uint ChunkMtlName = 0xCCCC0014, ChunkDataStream = 0xCCCC0016, ChunkMeshSubsets = 0xCCCC0017;

    public const int StreamPositions = 0, StreamNormals = 1, StreamTexCoords = 2, StreamColors = 3, StreamIndices = 5,
        StreamTangents = 6, StreamQTangents = 12, StreamP3S_C4B_T2S = 15;

    // The chunk-level types and helpers below are internal so ChrModelReader and CafReader can share them.
    internal readonly struct Chunk(uint type, uint version, int offset, int id, int size)
    {
        public readonly uint Type = type, Version = version;
        public readonly int Offset = offset, Id = id, Size = size;
    }

    internal sealed class MeshData
    {
        public int NumVerts, NumIndices, SubsetsChunkId;
        public readonly int[] StreamChunkIds = new int[16];
        public Vector3 BoxMin, BoxMax;
    }

    internal sealed class Subset
    {
        public int FirstIndex, NumIndices, FirstVert, NumVerts, MatId;
    }

    internal sealed class MtlNameData
    {
        public int Flags, PhysicalizeType;
        public string Name = "";
        public int[] SubChunkIds = [];

        /// <summary>Version 0x802 keeps the slots inline instead of in separate MtlName chunks.</summary>
        public List<CgfMaterialSlot> InlineSlots;
    }

    public static CgfModel Read(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < 20 || Encoding.ASCII.GetString(file, 0, 6) != "CryTek")
            throw new InvalidDataException("not a CryTek chunk file");
        var fileType = U32(file, 8);
        var version = U32(file, 12);
        var tableOffset = I32(file, 16);
        if (fileType != 0xFFFF0000)
            throw new InvalidDataException($"not a geometry file (type 0x{fileType:X8})");

        var chunks = ReadChunkTable(file, version, tableOffset);
        var byId = new Dictionary<int, Chunk>();
        foreach (var c in chunks)
            byId.TryAdd(c.Id, c);

        var model = new CgfModel { FileVersion = version };

        // Materials named inside the file.
        var mtlNames = new Dictionary<int, MtlNameData>();
        foreach (var c in chunks)
            if (c.Type == ChunkMtlName)
                mtlNames[c.Id] = ReadMtlName(file, c, model);

        // Nodes.
        var nodeChunks = new List<(CgfNode node, int objectId, int mtlId)>();
        foreach (var c in chunks)
        {
            if (c.Type != ChunkNode)
                continue;
            var p = Payload(file, c, out var size);
            if (c.Version != 0x823 || size < 204)
            {
                model.Warnings.Add($"node chunk {c.Id} version 0x{c.Version:X} size {size} not understood");
                continue;
            }
            var node = new CgfNode
            {
                ChunkId = c.Id,
                Name = CString(file, p, 64),
                ParentId = I32(file, p + 68),
                Local = ReadNodeMatrix(file, p + 84),
            };
            var objectId = I32(file, p + 64);
            var mtlId = I32(file, p + 76);
            var propLen = I32(file, p + 200);
            if (propLen > 0 && propLen <= size - 204)
                node.Properties = CString(file, p + 204, propLen).Trim();
            node.Kind = byId.TryGetValue(objectId, out var obj)
                ? obj.Type == ChunkMesh ? "mesh" : obj.Type == ChunkHelper ? "helper" : $"0x{obj.Type:X8}"
                : "none";
            nodeChunks.Add((node, objectId, mtlId));
            model.Nodes.Add(node);
        }

        // World transforms (parents may come after children in the file).
        var nodeById = new Dictionary<int, CgfNode>();
        foreach (var n in model.Nodes)
            nodeById.TryAdd(n.ChunkId, n);
        foreach (var n in model.Nodes)
        {
            var m = n.Local;
            var q = n;
            for (var guard = 0; guard < 64 && nodeById.TryGetValue(q.ParentId, out var parent) && parent != n; guard++)
            {
                m *= parent.Local; // row vectors: child first, then parent
                q = parent;
            }
            n.World = m;
        }

        var meshNodeCount = 0;
        foreach (var (node, _, _) in nodeChunks)
            if (node.Kind == "mesh" && !node.Name.StartsWith("$lod", StringComparison.OrdinalIgnoreCase))
                meshNodeCount++;
        model.NodeTransformsApplied = meshNodeCount > 1;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var emin = new Vector3(float.MaxValue);
        var emax = new Vector3(float.MinValue);

        foreach (var (node, objectId, mtlId) in nodeChunks)
        {
            if (node.Kind != "mesh")
            {
                node.SkipReason = node.Kind == "helper" ? "helper/dummy" : "no mesh";
                continue;
            }
            var mesh = ReadMesh(file, byId[objectId]);
            if (mesh == null)
            {
                node.SkipReason = "unsupported mesh chunk version";
                model.Warnings.Add($"{node.Name}: mesh chunk {objectId} version 0x{byId[objectId].Version:X} not supported");
                continue;
            }
            node.VertexCount = mesh.NumVerts;
            node.IndexCount = mesh.NumIndices;
            var world = model.NodeTransformsApplied ? node.World : Matrix4x4.Identity;
            var isDollar = node.Name.StartsWith('$');

            if (!isDollar)
                GrowBox(ref emin, ref emax, mesh.BoxMin, mesh.BoxMax, world);

            if (isDollar)
            {
                node.SkipReason = node.Name.StartsWith("$lod", StringComparison.OrdinalIgnoreCase) ? "lod" : "$ helper mesh";
                continue;
            }
            if (node.Name.Contains("proxy", StringComparison.OrdinalIgnoreCase))
            {
                node.SkipReason = "physics proxy (name)";
                continue;
            }
            if (mesh.NumVerts <= 0 || mesh.NumIndices <= 0)
            {
                node.SkipReason = mesh.NumVerts <= 0 ? "no render geometry (physics only)" : "no indices (source copy of a Merged node)";
                continue;
            }

            // Material slots for proxy detection.
            List<CgfMaterialSlot> slots = null;
            if (mtlNames.TryGetValue(mtlId, out var top))
            {
                slots = top.InlineSlots != null ? [.. top.InlineSlots] : [];
                if (top.InlineSlots == null && top.SubChunkIds.Length == 0)
                    slots.Add(new CgfMaterialSlot { Name = top.Name, PhysicalizeType = top.PhysicalizeType });
                foreach (var sid in top.SubChunkIds)
                    slots.Add(mtlNames.TryGetValue(sid, out var sub)
                        ? new CgfMaterialSlot { Name = sub.Name, PhysicalizeType = sub.PhysicalizeType }
                        : new CgfMaterialSlot { Name = "", PhysicalizeType = -1 });
                if (model.MaterialName.Length == 0)
                {
                    model.MaterialName = top.Name;
                    model.MaterialSlots = slots;
                }
            }

            var added = AddMeshSubmeshes(file, byId, mesh, node, world, slots, model, ref min, ref max);
            node.Rendered = added > 0;
            if (!node.Rendered)
                node.SkipReason = "no drawable subsets";
        }

        // A file whose render nodes carry no material still has a top-level MtlName; use it.
        if (model.MaterialName.Length == 0)
            foreach (var m in mtlNames.Values)
                if ((m.Flags & 2) == 0)
                {
                    model.MaterialName = m.Name;
                    break;
                }

        if (model.Submeshes.Count > 0)
        {
            model.BoundsMin = min;
            model.BoundsMax = max;
        }
        if (emin.X <= emax.X)
        {
            model.EngineBoundsMin = emin;
            model.EngineBoundsMax = emax;
        }
        return model;
    }

    private static int AddMeshSubmeshes(byte[] f, Dictionary<int, Chunk> byId, MeshData mesh, CgfNode node, Matrix4x4 world,
        List<CgfMaterialSlot> slots, CgfModel model, ref Vector3 min, ref Vector3 max)
    {
        Vector3[] pos = null, nrm = null;
        Vector2[] uv = null;
        Vector4[] tan = null, col = null;
        int[] idx = null;

        for (var st = 0; st < 16; st++)
        {
            var sid = mesh.StreamChunkIds[st];
            if (sid <= 0 || !byId.TryGetValue(sid, out var sc) || sc.Type != ChunkDataStream)
                continue;
            var p = Payload(f, sc, out var size);
            var type = I32(f, p + 4);
            var count = I32(f, p + 8);
            var es = I32(f, p + 12);
            var d = p + 24;
            model.Encodings.Add($"{StreamName(type)}/{es}");
            if (count < 0 || es <= 0 || (long)count * es > size - 24)
            {
                model.Warnings.Add($"{node.Name}: stream {type} count {count} x {es} exceeds chunk");
                continue;
            }
            switch (type)
            {
                case StreamPositions:
                    pos = es switch
                    {
                        12 => ReadVec3(f, d, count, 12),
                        16 => ReadVec3(f, d, count, 16),
                        8 => ReadHalf3(f, d, count, 8),
                        _ => Unsupported<Vector3>(model, node, type, es),
                    };
                    break;
                case StreamNormals:
                    nrm = es switch
                    {
                        12 => ReadVec3(f, d, count, 12),
                        8 => ReadHalf3(f, d, count, 8),
                        _ => Unsupported<Vector3>(model, node, type, es),
                    };
                    break;
                case StreamTexCoords:
                    uv = es switch
                    {
                        8 => ReadVec2(f, d, count),
                        4 => ReadHalf2(f, d, count, 4, 0),
                        _ => Unsupported<Vector2>(model, node, type, es),
                    };
                    break;
                case StreamColors:
                    col = es == 4 ? ReadColors(f, d, count, 4, 0) : Unsupported<Vector4>(model, node, type, es);
                    break;
                case StreamIndices:
                    idx = es switch
                    {
                        2 => ReadU16(f, d, count),
                        4 => ReadI32Array(f, d, count),
                        _ => Unsupported<int>(model, node, type, es),
                    };
                    break;
                case StreamTangents:
                    tan = es == 16 ? ReadTangents(f, d, count) : Unsupported<Vector4>(model, node, type, es);
                    break;
                case StreamQTangents:
                    if (es == 8)
                        ReadQTangents(f, d, count, out tan, out nrm);
                    else
                        Unsupported<Vector4>(model, node, type, es);
                    break;
                case StreamP3S_C4B_T2S:
                    if (es == 16)
                    {
                        pos = ReadHalf3(f, d, count, 16);
                        col = ReadColors(f, d, count, 16, 8);
                        uv = ReadHalf2(f, d, count, 16, 12);
                    }
                    else
                        Unsupported<Vector3>(model, node, type, es);
                    break;
                default:
                    break; // colors2, sh coeffs, bone mapping, face map, vertex mats: not needed for static drawing
            }
        }

        if (pos == null || idx == null)
        {
            model.Warnings.Add($"{node.Name}: missing position or index stream");
            return 0;
        }
        if (nrm == null && tan != null)
            model.Warnings.Add($"{node.Name}: no normal stream");
        nrm = MatchLength(nrm, pos.Length, "normals", node, model);
        uv = MatchLength(uv, pos.Length, "texcoords", node, model);
        tan = MatchLength(tan, pos.Length, "tangents", node, model);
        col = MatchLength(col, pos.Length, "colors", node, model);

        var subsets = ReadSubsets(f, byId, mesh.SubsetsChunkId);
        if (subsets.Count == 0)
            subsets.Add(new Subset { FirstIndex = 0, NumIndices = idx.Length, FirstVert = 0, NumVerts = pos.Length, MatId = 0 });

        Matrix4x4.Invert(world, out var inv);
        var normalM = Matrix4x4.Transpose(inv);
        var mirrored = world.GetDeterminant() < 0;
        var identity = world.IsIdentity;

        var remap = new int[pos.Length];
        var added = 0;
        foreach (var s in subsets)
        {
            if (s.NumIndices <= 0)
                continue;
            if (slots != null && s.MatId >= 0 && s.MatId < slots.Count && slots[s.MatId].IsPhysicsProxy)
                continue;
            var start = Math.Max(0, s.FirstIndex);
            var end = Math.Min(idx.Length, (long)s.FirstIndex + s.NumIndices);
            if (end - start < 3)
                continue;
            var triCount = (int)((end - start) / 3);
            Array.Fill(remap, -1);
            var verts = new List<int>();
            var outIdx = new List<int>(triCount * 3);
            var bad = 0;
            for (var t = 0; t < triCount; t++)
            {
                int a = idx[start + t * 3], b = idx[start + t * 3 + 1], c = idx[start + t * 3 + 2];
                if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length)
                {
                    bad++;
                    continue;
                }
                if (mirrored)
                    (b, c) = (c, b);
                outIdx.Add(Remap(a, remap, verts));
                outIdx.Add(Remap(b, remap, verts));
                outIdx.Add(Remap(c, remap, verts));
            }
            if (bad > 0)
                model.Warnings.Add($"{node.Name}: {bad} triangles with out-of-range indices dropped");
            if (outIdx.Count == 0)
                continue;

            var sm = new CgfSubmesh
            {
                NodeName = node.Name,
                MaterialIndex = s.MatId,
                Indices = outIdx.ToArray(),
                Positions = new Vector3[verts.Count],
                Normals = nrm != null ? new Vector3[verts.Count] : null,
                UVs = uv != null ? new Vector2[verts.Count] : null,
                Tangents = tan != null ? new Vector4[verts.Count] : null,
                Colors = col != null ? new Vector4[verts.Count] : null,
            };
            for (var i = 0; i < verts.Count; i++)
            {
                var v = verts[i];
                var pp = identity ? pos[v] : Vector3.Transform(pos[v], world);
                sm.Positions[i] = pp;
                min = Vector3.Min(min, pp);
                max = Vector3.Max(max, pp);
                if (sm.Normals != null)
                    sm.Normals[i] = identity ? SafeNormalize(nrm[v]) : SafeNormalize(Vector3.TransformNormal(nrm[v], normalM));
                if (sm.UVs != null)
                    sm.UVs[i] = uv[v];
                if (sm.Colors != null)
                    sm.Colors[i] = col[v];
                if (sm.Tangents != null)
                {
                    var tv = tan[v];
                    var t3 = identity ? new Vector3(tv.X, tv.Y, tv.Z) : Vector3.TransformNormal(new Vector3(tv.X, tv.Y, tv.Z), world);
                    sm.Tangents[i] = new Vector4(SafeNormalize(t3), mirrored ? -tv.W : tv.W);
                }
            }
            model.Submeshes.Add(sm);
            added++;
        }
        return added;
    }

    private static string StreamName(int type) => type switch
    {
        StreamPositions => "positions",
        StreamNormals => "normals",
        StreamTexCoords => "texcoords",
        StreamColors => "colors",
        4 => "colors2",
        StreamIndices => "indices",
        StreamTangents => "tangents",
        StreamQTangents => "qtangents",
        StreamP3S_C4B_T2S => "p3s_c4b_t2s",
        _ => "type" + type,
    };

    private static T[] MatchLength<T>(T[] a, int n, string what, CgfNode node, CgfModel model)
    {
        if (a == null || a.Length == n)
            return a;
        model.Warnings.Add($"{node.Name}: {what} stream has {a.Length} entries for {n} vertices, ignored");
        return null;
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

    // ---------------------------------------------------------------- chunk level

    internal static List<Chunk> ReadChunkTable(byte[] f, uint version, int tableOffset)
    {
        if (tableOffset < 0 || tableOffset + 4 > f.Length)
            throw new InvalidDataException("chunk table offset out of range");
        var count = I32(f, tableOffset);
        var list = new List<Chunk>(Math.Max(0, count));
        if (version == 0x745)
        {
            if (count < 0 || tableOffset + 4 + (long)count * 20 > f.Length)
                throw new InvalidDataException("chunk table truncated");
            for (var i = 0; i < count; i++)
            {
                var e = tableOffset + 4 + i * 20;
                list.Add(new Chunk(U32(f, e), U32(f, e + 4) & 0x7FFFFFFF, I32(f, e + 8), I32(f, e + 12), I32(f, e + 16)));
            }
        }
        else if (version == 0x744)
        {
            // No size column: a chunk runs to the next chunk's offset (or the table / end of file).
            if (count < 0 || tableOffset + 4 + (long)count * 16 > f.Length)
                throw new InvalidDataException("chunk table truncated");
            var raw = new List<(uint t, uint v, int o, int id)>();
            for (var i = 0; i < count; i++)
            {
                var e = tableOffset + 4 + i * 16;
                raw.Add((U32(f, e), U32(f, e + 4) & 0x7FFFFFFF, I32(f, e + 8), I32(f, e + 12)));
            }
            var offsets = new List<int>();
            foreach (var r in raw)
                offsets.Add(r.o);
            offsets.Add(tableOffset);
            offsets.Add(f.Length);
            offsets.Sort();
            foreach (var r in raw)
            {
                var end = f.Length;
                foreach (var o in offsets)
                    if (o > r.o)
                    {
                        end = o;
                        break;
                    }
                list.Add(new Chunk(r.t, r.v, r.o, r.id, end - r.o));
            }
        }
        else
            throw new InvalidDataException($"unsupported chunk file version 0x{version:X}");

        foreach (var c in list)
            if (c.Offset < 0 || c.Size < 0 || (long)c.Offset + c.Size > f.Length)
                throw new InvalidDataException($"chunk {c.Id} out of file bounds");
        return list;
    }

    /// <summary>Most chunks repeat their 16-byte header at the start of the data; skip it when present.</summary>
    internal static int Payload(byte[] f, Chunk c, out int size)
    {
        if (c.Size >= 16 && U32(f, c.Offset) == c.Type && I32(f, c.Offset + 12) == c.Id)
        {
            size = c.Size - 16;
            return c.Offset + 16;
        }
        size = c.Size;
        return c.Offset;
    }

    internal static MtlNameData ReadMtlName(byte[] f, Chunk c, CgfModel model)
    {
        var p = Payload(f, c, out var size);
        var m = new MtlNameData();
        if (c.Version == 0x802 && size >= 132)
        {
            // name[128], int count, then physicalize types (one when count is 0), then optional zero-terminated names.
            m.Name = CString(f, p, 128);
            var count = Math.Clamp(I32(f, p + 128), 0, 256);
            var q = p + 132;
            var end = p + size;
            m.InlineSlots = [];
            if (count == 0)
            {
                m.PhysicalizeType = q + 4 <= end ? I32(f, q) : -1;
                m.InlineSlots = null;
                return m;
            }
            m.Flags = 1;
            for (var i = 0; i < count; i++, q += 4)
                m.InlineSlots.Add(new CgfMaterialSlot { PhysicalizeType = q + 4 <= end ? I32(f, q) : -1 });
            foreach (var slot in m.InlineSlots)
            {
                if (q >= end)
                    break;
                slot.Name = CString(f, q, end - q);
                q += slot.Name.Length + 1;
            }
            return m;
        }
        if (c.Version != 0x800 || size < 272)
        {
            model.Warnings.Add($"MtlName chunk {c.Id} version 0x{c.Version:X} not understood");
            return m;
        }
        m.Flags = I32(f, p);
        m.Name = CString(f, p + 8, 128);
        m.PhysicalizeType = I32(f, p + 136);
        var n = Math.Clamp(I32(f, p + 140), 0, 32);
        m.SubChunkIds = new int[n];
        for (var i = 0; i < n; i++)
            m.SubChunkIds[i] = I32(f, p + 144 + i * 4);
        return m;
    }

    internal static MeshData ReadMesh(byte[] f, Chunk c)
    {
        var p = Payload(f, c, out var size);
        if (c.Version != 0x800 || size < 132)
            return null;
        var m = new MeshData
        {
            NumVerts = I32(f, p + 8),
            NumIndices = I32(f, p + 12),
            SubsetsChunkId = I32(f, p + 20),
        };
        for (var i = 0; i < 16; i++)
            m.StreamChunkIds[i] = I32(f, p + 28 + i * 4);
        // +92: physics data chunk ids[4]
        m.BoxMin = new Vector3(F32(f, p + 108), F32(f, p + 112), F32(f, p + 116));
        m.BoxMax = new Vector3(F32(f, p + 120), F32(f, p + 124), F32(f, p + 128));
        return m;
    }

    internal static List<Subset> ReadSubsets(byte[] f, Dictionary<int, Chunk> byId, int chunkId)
    {
        var list = new List<Subset>();
        if (chunkId <= 0 || !byId.TryGetValue(chunkId, out var c) || c.Type != ChunkMeshSubsets)
            return list;
        var p = Payload(f, c, out var size);
        var n = I32(f, p + 4);
        if (n < 0 || 16 + (long)n * 36 > size)
            return list;
        for (var i = 0; i < n; i++)
        {
            var e = p + 16 + i * 36;
            list.Add(new Subset
            {
                FirstIndex = I32(f, e),
                NumIndices = I32(f, e + 4),
                FirstVert = I32(f, e + 8),
                NumVerts = I32(f, e + 12),
                MatId = I32(f, e + 16),
                // +20 radius, +24 center (unused)
            });
        }
        return list;
    }

    /// <summary>
    /// Node tm: 16 floats, row-major, row-vector convention (the same layout as System.Numerics.Matrix4x4),
    /// translation in the 4th row in centimetres. The 16th float is stored as 0, so the last column is rebuilt.
    /// </summary>
    private static Matrix4x4 ReadNodeMatrix(byte[] f, int p)
    {
        var m = new Matrix4x4(
            F32(f, p), F32(f, p + 4), F32(f, p + 8), 0,
            F32(f, p + 16), F32(f, p + 20), F32(f, p + 24), 0,
            F32(f, p + 32), F32(f, p + 36), F32(f, p + 40), 0,
            F32(f, p + 48) * 0.01f, F32(f, p + 52) * 0.01f, F32(f, p + 56) * 0.01f, 1);
        return m;
    }

    // ---------------------------------------------------------------- stream decoders

    private static T[] Unsupported<T>(CgfModel model, CgfNode node, int type, int es)
    {
        model.Warnings.Add($"{node.Name}: stream type {type} with element size {es} not supported");
        return null;
    }

    internal static Vector3[] ReadVec3(byte[] f, int p, int n, int stride)
    {
        var a = new Vector3[n];
        for (var i = 0; i < n; i++, p += stride)
            a[i] = new Vector3(F32(f, p), F32(f, p + 4), F32(f, p + 8));
        return a;
    }

    internal static Vector2[] ReadVec2(byte[] f, int p, int n)
    {
        var a = new Vector2[n];
        for (var i = 0; i < n; i++, p += 8)
            a[i] = new Vector2(F32(f, p), F32(f, p + 4));
        return a;
    }

    internal static Vector3[] ReadHalf3(byte[] f, int p, int n, int stride)
    {
        var a = new Vector3[n];
        for (var i = 0; i < n; i++, p += stride)
            a[i] = new Vector3(H16(f, p), H16(f, p + 2), H16(f, p + 4));
        return a;
    }

    internal static Vector2[] ReadHalf2(byte[] f, int p, int n, int stride, int offset)
    {
        var a = new Vector2[n];
        p += offset;
        for (var i = 0; i < n; i++, p += stride)
            a[i] = new Vector2(H16(f, p), H16(f, p + 2));
        return a;
    }

    internal static Vector4[] ReadColors(byte[] f, int p, int n, int stride, int offset)
    {
        var a = new Vector4[n];
        p += offset;
        for (var i = 0; i < n; i++, p += stride)
            a[i] = new Vector4(f[p] / 255f, f[p + 1] / 255f, f[p + 2] / 255f, f[p + 3] / 255f);
        return a;
    }

    internal static int[] ReadU16(byte[] f, int p, int n)
    {
        var a = new int[n];
        for (var i = 0; i < n; i++, p += 2)
            a[i] = f[p] | (f[p + 1] << 8);
        return a;
    }

    internal static int[] ReadI32Array(byte[] f, int p, int n)
    {
        var a = new int[n];
        for (var i = 0; i < n; i++, p += 4)
            a[i] = I32(f, p);
        return a;
    }

    /// <summary>
    /// SMeshTangents: tangent int16[4] then binormal int16[4], normalised by 32767. W of both is the handedness:
    /// normal = cross(tangent, binormal) * W, tangent ~ +dP/du, binormal ~ +dP/dv.
    /// </summary>
    internal static Vector4[] ReadTangents(byte[] f, int p, int n)
    {
        var a = new Vector4[n];
        for (var i = 0; i < n; i++, p += 16)
        {
            var t = new Vector3(S16(f, p), S16(f, p + 2), S16(f, p + 4)) / 32767f;
            var w = S16(f, p + 6) < 0 ? -1f : 1f;
            a[i] = new Vector4(SafeNormalize(t), w);
        }
        return a;
    }

    /// <summary>CGF_STREAM_QTANGENTS (int16 quaternion per vertex). Not present in the sampled data; decoded per CE3.</summary>
    private static void ReadQTangents(byte[] f, int p, int n, out Vector4[] tangents, out Vector3[] normals)
    {
        tangents = new Vector4[n];
        normals = new Vector3[n];
        for (var i = 0; i < n; i++, p += 8)
        {
            var q = Quaternion.Normalize(new Quaternion(S16(f, p) / 32767f, S16(f, p + 2) / 32767f, S16(f, p + 4) / 32767f, S16(f, p + 6) / 32767f));
            var sign = S16(f, p + 6) < 0 ? -1f : 1f;
            var t = Vector3.Transform(Vector3.UnitX, q);
            var b = Vector3.Transform(Vector3.UnitY, q) * sign;
            var nn = Vector3.Cross(t, b) * sign;
            tangents[i] = new Vector4(t, sign);
            normals[i] = SafeNormalize(nn);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void GrowBox(ref Vector3 min, ref Vector3 max, Vector3 bmin, Vector3 bmax, Matrix4x4 m)
    {
        if (bmin.X > bmax.X)
            return;
        for (var i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) != 0 ? bmax.X : bmin.X, (i & 2) != 0 ? bmax.Y : bmin.Y, (i & 4) != 0 ? bmax.Z : bmin.Z);
            var w = Vector3.Transform(c, m);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
    }

    internal static Vector3 SafeNormalize(Vector3 v)
    {
        var l = v.Length();
        return l > 1e-12f ? v / l : Vector3.UnitZ;
    }

    internal static string CString(byte[] f, int p, int max)
    {
        var end = p;
        var limit = Math.Min(f.Length, p + max);
        while (end < limit && f[end] != 0)
            end++;
        return Encoding.Latin1.GetString(f, p, end - p);
    }

    internal static uint U32(byte[] f, int p) => BitConverter.ToUInt32(f, p);
    internal static int I32(byte[] f, int p) => BitConverter.ToInt32(f, p);
    internal static float F32(byte[] f, int p) => BitConverter.ToSingle(f, p);
    internal static short S16(byte[] f, int p) => BitConverter.ToInt16(f, p);
    internal static float H16(byte[] f, int p) => (float)BitConverter.ToHalf(f, p);
}

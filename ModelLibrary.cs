using System.Collections.Concurrent;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>A drawable model + material combination, as handed out by <see cref="ModelLibrary.Request"/>.</summary>
/// <param name="CollisionKey">Key of the model's collision triangles (see <see cref="ModelLibrary.CollisionFaces"/>).</param>
internal sealed record MeshRef(string Key, float Radius, string CollisionKey, int TriangleCount);

/// <summary>
/// Turns .cgf models, .mtl materials and .dds textures from the pak into Godot meshes and materials.
/// Everything happens on worker threads, several at once: reading, parsing, decoding, LOD generation and creating the
/// Godot resources themselves (Godot allows creating resources off the main thread; only the scene tree is main-thread
/// only). Every cache entry is a <see cref="Lazy{T}"/>, so each file is processed exactly once and other threads wait
/// for it; a mesh's factory asks for its materials (and they for their textures) first, so those always exist by then.
/// </summary>
internal sealed partial class ModelLibrary(bool useNormalMaps, bool renderParity = false)
{
    private const string DefaultMaterialKey = "<default>";
    /// <summary>Meshes with more triangles than this get automatic LODs (Godot picks one by screen size).</summary>
    private const int LodTriangleThreshold = 300;

    /// <summary>
    /// A model surface in Godot axes and winding, shared by every material variant of the model. FrameNormals are the
    /// tangent frames' own normals (what CryEngine's Vegetation shader lights with) and VertexAlpha the vertex colour
    /// alpha (its baked ambient occlusion); either may be null.
    /// </summary>
    private sealed record Surface(int MaterialIndex, Vector3[] Positions, Vector3[] Normals, float[] Tangents, Vector2[] UVs,
        int[] Indices, int[] MirroredIndices, Vector3[] FrameNormals, float[] VertexAlpha);

    private sealed record Model(string Path, string MaterialName, Surface[] Surfaces, float Radius);

    // Worker threads.
    private readonly ConcurrentDictionary<string, Lazy<Model>> _models = new();
    private readonly ConcurrentDictionary<string, Lazy<MtlFile>> _mtlFiles = new();
    private readonly ConcurrentDictionary<string, Lazy<MeshRef>> _meshRefs = new();
    private readonly ConcurrentDictionary<string, Lazy<string>> _textureKeys = new(); // request key -> texture key, null when unusable
    private readonly ConcurrentDictionary<string, Lazy<string>> _materialKeys = new();
    private readonly ConcurrentDictionary<string, Vector3[]> _collisionFaces = new();
    private readonly ConcurrentDictionary<string, bool> _textureSrgb = new();

    // The Godot resources, created by the factories above.
    private readonly ConcurrentDictionary<string, Texture2D> _textures = new();
    private readonly ConcurrentDictionary<string, Material> _materials = new();
    private readonly ConcurrentDictionary<(string MaterialKey, int Priority), Material> _roadMaterials = new();
    private readonly ConcurrentDictionary<string, Lazy<BaseMaterial3D>> _voxelMaterials = new();
    private readonly ConcurrentDictionary<string, Lazy<BaseMaterial3D>> _cloudMaterials = new();
    private readonly ConcurrentDictionary<string, Mesh> _meshes = new();

    public int ModelCount => _models.Values.Count(m => m.IsValueCreated && m.Value != null);
    public int ModelFailures => _models.Values.Count(m => m.IsValueCreated && m.Value == null);
    public int TextureCount => _textureKeys.Values.Count(t => t.IsValueCreated && t.Value != null);
    public int TextureFailures => _textureKeys.Values.Count(t => t.IsValueCreated && t.Value == null);
    public bool RenderParity => renderParity;

    /// <summary>Render layer of placed objects (the terrain is on layer 1), so terrain-only decals can skip them.</summary>
    public const uint ObjectLayer = 2;

    /// <summary>
    /// Any worker thread. Gets the mesh for a model drawn with a material file (null or "" = the model's own), or null
    /// when the model can't be read or has nothing to draw. <paramref name="mirrored"/> selects a variant with reversed
    /// winding for instances whose transform has a negative determinant. <paramref name="receiveShadows"/> false gives
    /// Vegetation-shader surfaces a variant that ignores shadow maps (vegetation groups with bRecvShadow = 0).
    /// </summary>
    public MeshRef Request(string cgfPath, string materialOverride, bool mirrored, bool receiveShadows = true)
    {
        cgfPath = PakFiles.Normalize(cgfPath);
        var model = _models.GetOrAdd(cgfPath, p => new Lazy<Model>(() => LoadModel(p))).Value;
        if (model == null)
            return null;
        var mtlPath = !string.IsNullOrEmpty(materialOverride) && PakFiles.Exists(materialOverride)
            ? PakFiles.Normalize(materialOverride)
            : MtlReader.ResolveModelMaterial(cgfPath, model.MaterialName, PakFiles.Exists);
        var key = $"{cgfPath}|{mtlPath}|{(mirrored ? "m" : "")}{(receiveShadows ? "" : "|nr")}";
        return _meshRefs.GetOrAdd(key, k => new Lazy<MeshRef>(() => CreateMesh(k, model, mtlPath, mirrored, receiveShadows))).Value;
    }

    private MeshRef CreateMesh(string key, Model model, string mtlPath, bool mirrored, bool receiveShadows)
    {
        var mtl = LoadMtlCached(mtlPath);
        var surfaces = new List<(Surface Surface, string MaterialKey)>();
        foreach (var surface in model.Surfaces)
        {
            var sub = mtl?.ForSubset(surface.MaterialIndex);
            if (sub != null && sub.IsNoDraw)
                continue;
            var vegetationSurface = renderParity && sub?.Shader.Equals("Vegetation", StringComparison.OrdinalIgnoreCase) == true;
            surfaces.Add((surface, RequestMaterial(mtlPath, surface.MaterialIndex, sub, vegetationSurface: vegetationSurface,
                receiveShadows: receiveShadows || !vegetationSurface)));
        }
        if (surfaces.Count == 0)
            return null;

        var minY = surfaces.SelectMany(s => s.Surface.Positions).Min(p => p.Y);
        var maxY = surfaces.SelectMany(s => s.Surface.Positions).Max(p => p.Y);
        var arrays = surfaces.Select(s => BuildArrays(s.Surface, mirrored,
            s.MaterialKey.EndsWith("|vegetation", StringComparison.Ordinal), minY, maxY)).ToList();
        var triangles = surfaces.Sum(s => s.Surface.Indices.Length / 3);
        Material MaterialOf(int i) => _materials.GetValueOrDefault(surfaces[i].MaterialKey) ?? _materials[DefaultMaterialKey];
        RenderBudget.Acquire(surfaces.Count);
        if (triangles > LodTriangleThreshold && surfaces.All(s => s.Surface.Normals != null))
        {
            // Simplified LODs; Godot switches between them by screen size.
            var importer = new ImporterMesh();
            foreach (var a in arrays)
                importer.AddSurface(Mesh.PrimitiveType.Triangles, a);
            importer.GenerateLods(25f, 60f, new Godot.Collections.Array());
            for (var i = 0; i < surfaces.Count; i++)
                importer.SetSurfaceMaterial(i, MaterialOf(i));
            _meshes[key] = importer.GetMesh();
        }
        else
        {
            var mesh = new ArrayMesh();
            for (var i = 0; i < arrays.Count; i++)
            {
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays[i]);
                mesh.SurfaceSetMaterial(i, MaterialOf(i));
            }
            _meshes[key] = mesh;
        }
        _collisionFaces.TryAdd(model.Path, CollisionTriangles(model));
        return new MeshRef(key, model.Radius, model.Path, triangles);
    }

    /// <summary>
    /// Any thread. One MultiMesh node holding every instance of a mesh, not yet in the tree (add it on the main thread),
    /// or null when the mesh doesn't exist.
    /// </summary>
    public MultiMeshInstance3D CreateInstances(MeshRef mesh, IReadOnlyList<Transform3D> transforms, float visibilityEnd, string name,
        bool castShadow = true, IReadOnlyList<Color> colors = null, IReadOnlyList<Color> customData = null,
        bool preciseAabb = true, float lodBias = 1f, bool visibilityFade = false, float visibilityEndMargin = 80f)
    {
        if (transforms.Count == 0 || !_meshes.TryGetValue(mesh.Key, out var godotMesh))
            return null;
        var nodeOrigin = InstanceOriginCenter(transforms);
        var localBounds = preciseAabb ? InstanceBounds(godotMesh.GetAabb(), transforms, nodeOrigin) : default;
        // The buffer holds each transform as three rows of the 3x4 matrix [basis | origin].
        RenderBudget.Acquire();
        var buffer = new float[transforms.Count * 12];
        for (var i = 0; i < transforms.Count; i++)
        {
            var t = transforms[i];
            t.Origin -= nodeOrigin;
            var o = i * 12;
            buffer[o] = t.Basis.X.X; buffer[o + 1] = t.Basis.Y.X; buffer[o + 2] = t.Basis.Z.X; buffer[o + 3] = t.Origin.X;
            buffer[o + 4] = t.Basis.X.Y; buffer[o + 5] = t.Basis.Y.Y; buffer[o + 6] = t.Basis.Z.Y; buffer[o + 7] = t.Origin.Y;
            buffer[o + 8] = t.Basis.X.Z; buffer[o + 9] = t.Basis.Y.Z; buffer[o + 10] = t.Basis.Z.Z; buffer[o + 11] = t.Origin.Z;
        }
        var useColors = colors != null && colors.Count == transforms.Count;
        var useCustomData = customData != null && customData.Count == transforms.Count;
        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = useColors,
            UseCustomData = useCustomData,
            Mesh = godotMesh,
            InstanceCount = transforms.Count,
        };
        if (useColors || useCustomData)
        {
            // The typed API keeps this independent of RenderingServer's packed-buffer layout.
            for (var i = 0; i < transforms.Count; i++)
            {
                var transform = transforms[i];
                transform.Origin -= nodeOrigin;
                multiMesh.SetInstanceTransform(i, transform);
                if (useColors)
                    multiMesh.SetInstanceColor(i, colors[i]);
                if (useCustomData)
                    multiMesh.SetInstanceCustomData(i, customData[i]);
            }
        }
        else
            multiMesh.Buffer = buffer;
        return new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = multiMesh,
            Position = nodeOrigin,
            CustomAabb = localBounds,
            LodBias = lodBias,
            VisibilityRangeEnd = visibilityEnd,
            VisibilityRangeEndMargin = visibilityFade && visibilityEnd > 0f ? Math.Min(visibilityEndMargin, visibilityEnd * 0.2f) : 0f,
            VisibilityRangeFadeMode = visibilityFade && visibilityEnd > 0f
                ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self
                : GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
            Layers = ObjectLayer,
            CastShadow = castShadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>Conservative tight bounds for a MultiMesh, including every transformed instance and the mesh's origin offset.</summary>
    private static Vector3 InstanceOriginCenter(IReadOnlyList<Transform3D> transforms)
    {
        var minimum = transforms[0].Origin;
        var maximum = minimum;
        for (var i = 1; i < transforms.Count; i++)
        {
            minimum = minimum.Min(transforms[i].Origin);
            maximum = maximum.Max(transforms[i].Origin);
        }
        return (minimum + maximum) * 0.5f;
    }

    private static Aabb InstanceBounds(Aabb meshBounds, IReadOnlyList<Transform3D> transforms, Vector3 nodeOrigin)
    {
        var end = meshBounds.Position + meshBounds.Size;
        var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (var transform in transforms)
        {
            for (var mask = 0; mask < 8; mask++)
            {
                var corner = new Vector3(
                    (mask & 1) == 0 ? meshBounds.Position.X : end.X,
                    (mask & 2) == 0 ? meshBounds.Position.Y : end.Y,
                    (mask & 4) == 0 ? meshBounds.Position.Z : end.Z);
                var point = transform * corner - nodeOrigin;
                minimum = minimum.Min(point);
                maximum = maximum.Max(point);
            }
        }

        return new Aabb(minimum, maximum - minimum);
    }

    /// <summary>
    /// Any worker thread. Material of a stand-alone surface (road, water, cloud, voxel), by .mtl path and sub-material.
    /// <paramref name="forceAlphaBlend"/> keeps the texture's alpha and blends it (roads, clouds).
    /// </summary>
    public string RequestMaterial(string mtlPath, int subIndex = 0, bool forceAlphaBlend = false)
    {
        mtlPath = CellPaths.MaterialFile(CellPaths.Normalize(mtlPath));
        if (mtlPath == null || !PakFiles.Exists(mtlPath))
            return DefaultMaterialKey;
        var sub = LoadMtlCached(mtlPath)?.ForSubset(subIndex);
        return RequestMaterial(mtlPath, subIndex, sub, forceAlphaBlend);
    }

    /// <summary>Any worker thread. A parsed .mtl file (cached), or null when missing or unreadable.</summary>
    public MtlFile Mtl(string mtlPath)
    {
        mtlPath = CellPaths.MaterialFile(CellPaths.Normalize(mtlPath));
        return mtlPath == null ? null : LoadMtlCached(mtlPath);
    }

    /// <summary>Any worker thread. Albedo (with alpha) and normal textures of a decal material; either key may be null.</summary>
    public (string Albedo, string Normal) RequestDecalTextures(string mtlPath)
    {
        mtlPath = CellPaths.MaterialFile(CellPaths.Normalize(mtlPath));
        var sub = mtlPath == null ? null : LoadMtlCached(mtlPath)?.ForSubset(0);
        if (sub == null)
            return (null, null);
        var albedo = sub.DiffuseMap ?? sub.DecalMap;
        return (albedo == null ? null : RequestTexture(albedo.PakPath, true),
            useNormalMaps && sub.NormalMap != null ? RequestTexture(sub.NormalMap.PakPath, false) : null);
    }

    /// <summary>Any thread. The material for a key from <see cref="RequestMaterial(string, int, bool)"/>.</summary>
    public Material GetMaterial(string key) =>
        key != null && _materials.TryGetValue(key, out var m) ? m : _materials[DefaultMaterialKey];

    /// <summary>Shared road material variant; render priority is part of the cache key.</summary>
    public Material GetRoadMaterial(string key, int priority) => _roadMaterials.GetOrAdd((key, priority), k =>
    {
        var material = (BaseMaterial3D)GetMaterial(k.MaterialKey).Duplicate();
        material.RenderPriority = k.Priority;
        return material;
    });

    /// <summary>Shared material variant for triplanar voxel geometry.</summary>
    public BaseMaterial3D GetVoxelMaterial(string key) => _voxelMaterials.GetOrAdd(key ?? string.Empty, k => new Lazy<BaseMaterial3D>(() =>
    {
        var material = k.Length > 0
            ? (BaseMaterial3D)GetMaterial(k).Duplicate()
            : new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.41f, 0.36f) };
        material.Uv1Triplanar = true;
        material.Uv1WorldTriplanar = true;
        material.Uv1Scale = new Vector3(0.25f, 0.25f, 0.25f);
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        return material;
    }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Shared unshaded billboard variant for distance clouds.</summary>
    public BaseMaterial3D GetCloudMaterial(string key) => _cloudMaterials.GetOrAdd(key, k => new Lazy<BaseMaterial3D>(() =>
    {
        var material = (BaseMaterial3D)GetMaterial(k).Duplicate();
        material.BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled;
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        return material;
    }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Any thread. The Godot mesh of a <see cref="MeshRef"/>, or null.</summary>
    public Mesh GetMesh(MeshRef mesh) => mesh != null && _meshes.TryGetValue(mesh.Key, out var m) ? m : null;

    /// <summary>Any thread. A texture by key, or null.</summary>
    public Texture2D GetTexture(string key) => key != null && _textures.TryGetValue(key, out var t) ? t : null;

    /// <summary>Main thread, once before anything else runs: the grey material the engine shows for missing ones.</summary>
    public void CreateDefaults() =>
        _materials[DefaultMaterialKey] = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.55f, 0.55f), Roughness = 0.9f,
            VertexColorUseAsAlbedo = renderParity,
        };

    /// <summary>Any thread. The model's triangles in model space (Godot axes), 3 vertices per triangle, or null.</summary>
    public Vector3[] CollisionFaces(string collisionKey) =>
        collisionKey != null && _collisionFaces.TryGetValue(collisionKey, out var faces) ? faces : null;

    private static Model LoadModel(string path)
    {
        try
        {
            var bytes = PakFiles.Read(path);
            if (bytes == null)
                return null;
            var cgf = CgfModelReader.Read(bytes);
            cgf.FlipWinding(); // CryEngine front faces are counter-clockwise, Godot's clockwise.
            var surfaces = cgf.MergedByMaterial().Where(s => s.Indices.Length > 0).Select(ToSurface).ToArray();
            if (surfaces.Length == 0)
                return null;
            var radius = (cgf.BoundsMax - cgf.BoundsMin).Length() / 2f;
            return new Model(path, cgf.MaterialName, surfaces, radius);
        }
        catch (Exception e)
        {
            GD.PrintErr($"{path}: {e.Message}");
            return null;
        }
    }

    private static Vector3[] CollisionTriangles(Model model)
    {
        var faces = new List<Vector3>();
        foreach (var s in model.Surfaces)
            foreach (var i in s.Indices)
                faces.Add(s.Positions[i]);
        return faces.ToArray();
    }

    private static Surface ToSurface(CgfSubmesh s)
    {
        var positions = s.Positions.Select(CryAxes.Point).ToArray();
        var normals = s.Normals?.Select(CryAxes.Point).ToArray();
        float[] tangents = null;
        if (s.Tangents != null)
        {
            tangents = new float[s.Tangents.Length * 4];
            for (var i = 0; i < s.Tangents.Length; i++)
            {
                var t = CryAxes.Point(s.Tangents[i].X, s.Tangents[i].Y, s.Tangents[i].Z);
                tangents[i * 4] = t.X;
                tangents[i * 4 + 1] = t.Y;
                tangents[i * 4 + 2] = t.Z;
                tangents[i * 4 + 3] = s.Tangents[i].W;
            }
        }
        var uvs = s.UVs?.Select(uv => new Vector2(uv.X, uv.Y)).ToArray();
        var mirrored = (int[])s.Indices.Clone();
        for (var i = 0; i + 2 < mirrored.Length; i += 3)
            (mirrored[i + 1], mirrored[i + 2]) = (mirrored[i + 2], mirrored[i + 1]);
        var frameNormals = s.FrameNormals?.Select(CryAxes.Point).ToArray();
        var vertexAlpha = s.Colors?.Select(c => c.W).ToArray();
        return new Surface(s.MaterialIndex, positions, normals, tangents, uvs, s.Indices, mirrored, frameNormals, vertexAlpha);
    }

    private Godot.Collections.Array BuildArrays(Surface s, bool mirrored, bool vegetationSurface,
        float modelMinY, float modelMaxY)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = s.Positions;
        // A mirrored instance transform mirrors the normals itself; only the winding needs reversing.
        // vegetation.cfx lights with the tangent frame's normal (worldTangentN), not the normal stream.
        var normals = vegetationSurface && s.FrameNormals != null && s.Tangents != null ? s.FrameNormals : s.Normals;
        if (normals != null)
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        if (s.Tangents != null && normals != null && useNormalMaps)
            arrays[(int)Mesh.ArrayType.Tangent] = s.Tangents;
        if (s.UVs != null)
            arrays[(int)Mesh.ArrayType.TexUV] = s.UVs;
        if (vegetationSurface)
        {
            // UV2: x = height within the model, y = the vertex alpha the Vegetation shader multiplies the result by.
            var modelHeight = Math.Max(modelMaxY - modelMinY, 0.001f);
            var uv2 = new Vector2[s.Positions.Length];
            for (var i = 0; i < uv2.Length; i++)
                uv2[i] = new Vector2((s.Positions[i].Y - modelMinY) / modelHeight, s.VertexAlpha?[i] ?? 1f);
            arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        }
        arrays[(int)Mesh.ArrayType.Index] = mirrored ? s.MirroredIndices : s.Indices;
        return arrays;
    }

    private MtlFile LoadMtlCached(string path) =>
        string.IsNullOrEmpty(path) ? null : _mtlFiles.GetOrAdd(path, p => new Lazy<MtlFile>(() => LoadMtl(p))).Value;

    private static MtlFile LoadMtl(string path)
    {
        try
        {
            var bytes = PakFiles.Read(path);
            return bytes == null ? null : MtlReader.Parse(bytes);
        }
        catch (Exception e)
        {
            GD.PrintErr($"{path}: {e.Message}");
            return null;
        }
    }

    private string RequestMaterial(string mtlPath, int index, MtlMaterial sub, bool forceAlphaBlend = false,
        bool vegetationSurface = false, bool receiveShadows = true)
    {
        if (sub == null)
            return DefaultMaterialKey;
        var key = $"{mtlPath}#{index}{(forceAlphaBlend ? "|blend" : "")}{(receiveShadows ? "" : "|nr")}{(vegetationSurface ? "|vegetation" : "")}";
        return _materialKeys.GetOrAdd(key, k => new Lazy<string>(() => CreateMaterial(k, sub, forceAlphaBlend, vegetationSurface, receiveShadows))).Value;
    }

    private string CreateMaterial(string key, MtlMaterial sub, bool forceAlphaBlend, bool vegetationSurface,
        bool receiveShadows = true)
    {
        var diffuse = sub.DiffuseMap;
        var isHair = sub.Shader.Equals("Hair", StringComparison.OrdinalIgnoreCase);
        var usesAlpha = sub.IsAlphaTested || sub.IsAlphaBlended || forceAlphaBlend || isHair;
        var diffuseKey = diffuse == null ? null : RequestTexture(diffuse.PakPath, usesAlpha);
        var normalKey = useNormalMaps && sub.NormalMap != null ? RequestTexture(sub.NormalMap.PakPath, false) : null;
        var special = sub.IsSpecialShader; // glass, water surfaces and the like
        var twoSided = sub.IsTwoSided || isHair || sub.Shader.Equals("Vegetation", StringComparison.OrdinalIgnoreCase);
        // The Hair shader multiplies its grey diffuse texture by the HairColor public parameter.
        var tint = isHair && sub.PublicParams.TryGetValue("HairColor", out var hairColor) ? ParseColor(hairColor) : Colors.White;
        // The Vegetation shader applies the authored Diffuse colour itself (vegetation.cfx), at its stored value.
        if (renderParity && !vegetationSurface)
            tint *= NormalizedDiffuseTint(sub.DiffuseColor);
        var tile = diffuse == null ? Vector2.One : new Vector2(diffuse.TileU, diffuse.TileV);
        var offset = diffuse == null ? Vector2.Zero : new Vector2(diffuse.OffsetU, diffuse.OffsetV);
        var fallback = FallbackColor(sub);
        RenderBudget.Acquire();

        {
            var m = new StandardMaterial3D
            {
                Roughness = 0.85f,
                Uv1Scale = new Vector3(tile.X, tile.Y, 1f),
                Uv1Offset = new Vector3(offset.X, offset.Y, 0f),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            };
            if (diffuseKey != null && _textures.TryGetValue(diffuseKey, out var albedo))
            {
                m.AlbedoTexture = albedo;
                m.AlbedoColor = tint;
                m.AlbedoTextureForceSrgb = renderParity && _textureSrgb.TryGetValue(diffuseKey, out var srgb) && srgb;
            }
            else
                m.AlbedoColor = fallback;

            if (special)
            {
                m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                m.AlbedoColor = new Color(0.35f, 0.5f, 0.6f, 0.35f);
                m.AlbedoTexture = null;
                m.Roughness = 0.05f;
            }
            else if (forceAlphaBlend)
            {
                m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                m.AlbedoColor = m.AlbedoColor with { A = sub.Opacity };
            }
            else if (sub.IsAlphaTested || isHair)
            {
                m.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                m.AlphaScissorThreshold = Mathf.Clamp(sub.AlphaTest > 0 ? sub.AlphaTest : 0.3f, 0.05f, 0.95f);
            }
            else if (sub.IsAlphaBlended)
            {
                m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                m.AlbedoColor = m.AlbedoColor with { A = sub.Opacity };
            }

            if (twoSided)
                m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            if (normalKey != null && _textures.TryGetValue(normalKey, out var normal))
            {
                m.NormalEnabled = true;
                m.NormalTexture = normal;
            }
            if (renderParity && !special)
            {
                var hasGlowParameter = sub.EmittanceMap != null || sub.GlowAmount > 0.001f ||
                                       sub.PublicParams.ContainsKey("Emittance") || sub.PublicParams.ContainsKey("Glow");
                Texture2D glowTexture = null;
                if (hasGlowParameter)
                {
                    var glowMap = sub.EmittanceMap ??
                        (sub.Shader.Equals("Illum", StringComparison.OrdinalIgnoreCase) && sub.DecalMap != null
                            ? sub.DecalMap : sub.DiffuseMap);
                    if (glowMap != null)
                    {
                        if (glowMap == diffuse && diffuseKey != null)
                            _textures.TryGetValue(diffuseKey, out glowTexture);
                        else
                        {
                            var glowKey = RequestTexture(glowMap.PakPath, false);
                            if (glowKey != null)
                                _textures.TryGetValue(glowKey, out glowTexture);
                        }
                    }
                }
                RenderParityProfile.ApplyMaterial(m, sub, glowTexture);
            }
            _materials[key] = vegetationSurface
                ? CreateVegetationMaterial(m, sub, diffuseKey != null ? m.AlbedoTexture : null,
                    normalKey != null ? m.NormalTexture : null, receiveShadows)
                : m;
        }
        return key;
    }

    /// <summary>
    /// CryEngine's Vegetation shader (vegetation.cfx with shadelib.cfi LeafShadingFront/LeafShadingBack), per light:
    /// <list type="bullet">
    /// <item>The normal is the tangent frame's (worldTangentN), scaled for grass and leaves by min(N.E + 0.1, 1) so it
    /// turns toward the viewer and shortens at grazing angles.</item>
    /// <item>Front light: grass max(N.L, 0.5) (never darker than half the light), leaves and trunks saturate(N.L)
    /// weighted by N.E * 0.5 + 0.5, all times the light's shadow.</item>
    /// <item>Back light (grass, leaves): saturate((E.-L)^4 * BackViewDep + (1 - BackViewDep) * saturate(-N.L * 0.6 + 0.4))
    /// x BackDiffuse x BackDiffuseMultiplier, shadowed no further than BackShadowBias.</item>
    /// <item>Composition: ((ambient + front) * MatDif + back) * texture * (grass ? MatDif : 1), times the group
    /// brightness and the vertex alpha. Ambient is the environment's, for trunks times the hemisphere term
    /// N.y * 0.25 + 0.75.</item>
    /// <item>Groups with bUseTerrainColor blend grass with the lit terrain colour under it: lerp(terrain, grass,
    /// 1 - blendWithTerrainAmount) (GetTerrainColor).</item>
    /// <item>Leaves and trunks add a Phong highlight (MatSpecColor, Shininess); grass has none.</item>
    /// <item>Groups with bRecvShadow = 0 are not darkened by the sun's shadow map (they still cast shadows).</item>
    /// </list>
    /// Instances fade out at their own view distance (INSTANCE_CUSTOM.a, whole metres) with a dithered dissolve, measured
    /// from the main camera so shadows fade with them; past it the vertices collapse.
    /// </summary>
    private const string VegetationShaderSource = """
            shader_type spatial;
            render_mode cull_disabled, world_vertex_coords/*RENDER_MODE*/;

            uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform bool has_albedo_texture = false;
            uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform bool has_normal_texture = false;
            uniform vec2 uv_scale = vec2(1.0);
            uniform vec2 uv_offset = vec2(0.0);
            uniform float alpha_scissor = 0.3;
            uniform float opacity = 1.0;
            // vegetation.cfx inputs as stored in the .mtl (linear): MatDifColor, MatSpecColor and its exponent.
            uniform vec3 mat_diffuse = vec3(1.0);
            uniform vec3 mat_specular = vec3(0.0);
            uniform float mat_shininess = 10.0;
            // 0 = plain vegetation (trunks), 1 = %LEAVES, 2 = %GRASS (the material's GenMask).
            uniform int veg_mode = 0;
            uniform vec3 back_diffuse = vec3(0.57, 0.76, 0.37);
            uniform float back_multiplier = 0.85;
            uniform float back_view_dep = 0.5;
            uniform float back_shadow_bias = 0.9;
            uniform float blend_with_terrain = 0.5;
            uniform float vegetation_gain = 1.0;
            uniform float shadow_reach = 36.75;
            uniform vec3 emission_color : source_color = vec3(0.0);
            uniform float emission_energy = 0.0;
            uniform sampler2D emission_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform bool has_emission_texture = false;
            uniform bool emission_multiply = false;

            varying vec3 terrain_rgb;
            varying float terrain_share;
            varying float instance_fade;
            varying vec3 ce_normal;
            varying vec3 frag_vegetation;
            varying vec3 frag_terrain;
            varying vec3 frag_up;

            void vertex() {
                // INSTANCE_CUSTOM: rgb = terrain colour under the instance, a = view distance in whole metres (0 = none)
                // plus 0.25 when the group blends with the terrain colour.
                vec4 custom = INSTANCE_CUSTOM;
                float view_distance = floor(custom.a);
                bool use_terrain = custom.a - view_distance > 0.125;
                terrain_rgb = custom.rgb;
                terrain_share = use_terrain && veg_mode == 2 ? blend_with_terrain : 0.0;
                vec3 eye = MAIN_CAM_INV_VIEW_MATRIX[3].xyz;
                instance_fade = 1.0;
                if (view_distance > 0.0) {
                    vec3 origin = MODEL_MATRIX[3].xyz;
                    float margin = max(view_distance * 0.1, 2.0);
                    float d = distance(origin, eye);
                    instance_fade = clamp((view_distance - d) / margin, 0.0, 1.0);
                    // Shadow passes (drawn from another camera) keep only casters within e_shadows_cast_view_dist_ratio
                    // (0.8) of the view distance and inside the client's third shadow cascade.
                    bool shadow_pass = distance(INV_VIEW_MATRIX[3].xyz, eye) > 0.5;
                    if (instance_fade <= 0.0 || (shadow_pass && d > min(view_distance * 0.8, shadow_reach))) {
                        VERTEX = origin;
                    }
                }
                vec3 n = normalize(NORMAL);
                if (veg_mode > 0) {
                    n *= min(dot(normalize(eye - VERTEX), n) + 0.1, 1.0);
                }
                ce_normal = mat3(VIEW_MATRIX) * n;
            }

            void fragment() {
                if (instance_fade < 1.0) {
                    float noise = fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))));
                    if (noise >= instance_fade) {
                        discard;
                    }
                }
                vec2 tiled_uv = UV * uv_scale + uv_offset;
                vec4 texel = has_albedo_texture ? texture(albedo_texture, tiled_uv) : vec4(1.0);
                // Grass multiplies the texture by MatDif twice, leaves and trunks once. COLOR = group brightness,
                // UV2.y = vertex alpha: both scale the final colour.
                vec3 vegetation = texel.rgb * mat_diffuse * (veg_mode == 2 ? mat_diffuse : vec3(1.0)) * vegetation_gain;
                float scale = COLOR.r * UV2.y;
                frag_vegetation = vegetation * scale;
                frag_terrain = terrain_rgb * scale;
                frag_up = mat3(VIEW_MATRIX) * vec3(0.0, 1.0, 0.0);
                ALBEDO = mix(frag_vegetation, frag_terrain, terrain_share);
                // Trunks and other plain vegetation use hemisphere ambient (N.z * 0.25 + 0.75); grass and leaves take the
                // sky ambient as it is (the |view.z| weighting is the shader's QUALITY_HIGH-only path, not max quality).
                if (veg_mode == 0) {
                    AO = normalize(mat3(INV_VIEW_MATRIX) * NORMAL).y * 0.25 + 0.75;
                    AO_LIGHT_AFFECT = 0.0;
                }
                ROUGHNESS = 1.0;
                SPECULAR = 0.0;
                // ALPHA_MODE
                if (has_normal_texture) {
                    NORMAL_MAP = texture(normal_texture, tiled_uv).rgb;
                }
                vec3 emitted = emission_color;
                if (has_emission_texture) {
                    vec3 sampled_emission = texture(emission_texture, tiled_uv).rgb;
                    emitted = emission_multiply ? emitted * sampled_emission : emitted + sampled_emission;
                }
                EMISSION = emitted * emission_energy;
            }

            void light() {
                float shadow = ATTENUATION;
                vec3 light_color = LIGHT_COLOR / PI;
                vec3 front;
                if (veg_mode == 2) {
                    front = max(dot(ce_normal, LIGHT), 0.5) * light_color * shadow;
                } else {
                    front = clamp(dot(NORMAL, LIGHT), 0.0, 1.0) * light_color * shadow * (dot(NORMAL, VIEW) * 0.5 + 0.5);
                }
                vec3 back = vec3(0.0);
                if (veg_mode > 0) {
                    float e = clamp(dot(VIEW, -LIGHT), 0.0, 1.0);
                    e *= e;
                    e *= e;
                    float wrap = clamp(dot(-ce_normal, LIGHT) * 0.6 + 0.4, 0.0, 1.0);
                    float amount = clamp(e * back_view_dep + (1.0 - back_view_dep) * wrap, 0.0, 1.0);
                    float back_shadow = LIGHT_IS_DIRECTIONAL ? max(shadow, back_shadow_bias) : shadow;
                    back = amount * light_color * back_diffuse * back_multiplier * back_shadow;
                }
                // ALBEDO holds texture x MatDif (x MatDif for grass): the back light carries one MatDif less.
                vec3 vegetation = front * frag_vegetation + back * frag_vegetation / max(mat_diffuse, vec3(0.001));
                vec3 terrain = frag_terrain * clamp(dot(frag_up, LIGHT), 0.0, 1.0) * light_color * shadow;
                DIFFUSE_LIGHT += mix(vegetation, terrain, terrain_share) / max(ALBEDO, vec3(0.0001));
                if (veg_mode != 2) {
                    float highlight = pow(clamp(dot(reflect(-VIEW, NORMAL), LIGHT), 0.0, 1.0), max(mat_shininess, 1.0));
                    SPECULAR_LIGHT += highlight * light_color * mat_specular * (shadow * 0.8 + 0.2) *
                        (dot(NORMAL, VIEW) * 0.5 + 0.5);
                }
            }
            """;

    // Each variant also exists without shadow receiving (render_mode shadows_disabled) for bRecvShadow = 0 groups.
    private static readonly Shader VegetationOpaqueShader = CreateVegetationShader("", "");
    private static readonly Shader VegetationOpaqueNoShadowShader = CreateVegetationShader("", ", shadows_disabled");
    // Cut-outs stay a plain alpha test. Godot 4.7 puts alpha-to-coverage materials (ALPHA_ANTIALIASING_EDGE) in the
    // transparent pass: out of the depth prepass and the depth texture, so the ocean drew seabed plants on top of it.
    private const string VegetationScissorAlpha = "ALPHA = texel.a;\n                ALPHA_SCISSOR_THRESHOLD = alpha_scissor;";
    private static readonly Shader VegetationScissorShader = CreateVegetationShader(VegetationScissorAlpha, "");
    private static readonly Shader VegetationScissorNoShadowShader = CreateVegetationShader(VegetationScissorAlpha, ", shadows_disabled");
    private static readonly Shader VegetationBlendShader = CreateVegetationShader("ALPHA = texel.a * opacity;", "");
    private static readonly Shader VegetationBlendNoShadowShader = CreateVegetationShader("ALPHA = texel.a * opacity;", ", shadows_disabled");

    private static Shader CreateVegetationShader(string alphaMode, string renderMode) => new()
    {
        Code = VegetationShaderSource.Replace("// ALPHA_MODE", alphaMode).Replace("/*RENDER_MODE*/", renderMode),
    };

    /// <summary>
    /// Scale between the client's material diffuse values and this renderer's light units. The sun's energy maps the
    /// client's light units with 0.16 (<see cref="RenderParityProfile.LightScale"/>), a mapping tuned with materials
    /// normalised to their brightest channel; the Vegetation shader keeps the stored diffuse (median 0.31 per channel,
    /// see <see cref="MtlMaterial.DiffuseColor"/>) and takes the median's inverse instead, one factor for every
    /// vegetation material. Checked against the original: waterweed_b in sun needs 3.3x the terrain's scale in both
    /// the hill-forest and village references.
    /// </summary>
    public static float VegetationGain { get; set; } = 1f / 0.31f;

    /// <summary>GenMask bits of vegetation.ext: %LEAVES and %GRASS.</summary>
    private const ulong GenMaskLeaves = 0x100, GenMaskGrass = 0x2000;

    private static ShaderMaterial CreateVegetationMaterial(StandardMaterial3D source, MtlMaterial material,
        Texture2D albedo, Texture2D normal, bool receiveShadows)
    {
        var genMask = ulong.TryParse(material.GenMask, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var mask) ? mask : 0UL;
        var mode = (genMask & GenMaskGrass) != 0 ? 2 : (genMask & GenMaskLeaves) != 0 ? 1 : 0;
        // vegetation.cfx defaults for materials that leave these public parameters out.
        var backDiffuse = material.PublicParams.TryGetValue("BackDiffuse", out var back)
            ? ParseColor(back)
            : new Color(0.57f, 0.76f, 0.37f);
        var vegetationShader = source.Transparency switch
        {
            BaseMaterial3D.TransparencyEnum.AlphaScissor => receiveShadows ? VegetationScissorShader : VegetationScissorNoShadowShader,
            BaseMaterial3D.TransparencyEnum.Alpha => receiveShadows ? VegetationBlendShader : VegetationBlendNoShadowShader,
            _ => receiveShadows ? VegetationOpaqueShader : VegetationOpaqueNoShadowShader,
        };
        var shader = new ShaderMaterial { Shader = vegetationShader, RenderPriority = source.RenderPriority };
        shader.SetShaderParameter("has_albedo_texture", albedo != null);
        if (albedo != null)
            shader.SetShaderParameter("albedo_texture", albedo);
        shader.SetShaderParameter("has_normal_texture", normal != null);
        if (normal != null)
            shader.SetShaderParameter("normal_texture", normal);
        shader.SetShaderParameter("uv_scale", new Vector2(source.Uv1Scale.X, source.Uv1Scale.Y));
        shader.SetShaderParameter("uv_offset", new Vector2(source.Uv1Offset.X, source.Uv1Offset.Y));
        shader.SetShaderParameter("alpha_scissor", source.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor
            ? source.AlphaScissorThreshold : 0f);
        shader.SetShaderParameter("opacity", material.Opacity);
        shader.SetShaderParameter("mat_diffuse", new Vector3(material.DiffuseColor.X, material.DiffuseColor.Y, material.DiffuseColor.Z));
        shader.SetShaderParameter("mat_specular", new Vector3(material.SpecularColor.X, material.SpecularColor.Y, material.SpecularColor.Z));
        shader.SetShaderParameter("mat_shininess", material.Shininess);
        shader.SetShaderParameter("veg_mode", mode);
        shader.SetShaderParameter("back_diffuse", new Vector3(backDiffuse.R, backDiffuse.G, backDiffuse.B));
        shader.SetShaderParameter("back_multiplier", PublicScalar(material, "BackDiffuseMultiplier", 0.85f));
        shader.SetShaderParameter("back_view_dep", PublicScalar(material, "BackViewDep", 0.5f));
        shader.SetShaderParameter("back_shadow_bias", PublicScalar(material, "BackShadowBias", 0.9f));
        shader.SetShaderParameter("blend_with_terrain", PublicScalar(material, "blendWithTerrainAmount", 0.5f));
        shader.SetShaderParameter("vegetation_gain", VegetationGain);
        shader.SetShaderParameter("shadow_reach", CellSceneBuilder.PaintedShadowReach);
        shader.SetShaderParameter("emission_color", source.EmissionEnabled ? source.Emission : Colors.Black);
        shader.SetShaderParameter("emission_energy", source.EmissionEnabled ? source.EmissionEnergyMultiplier : 0f);
        shader.SetShaderParameter("has_emission_texture", source.EmissionTexture != null);
        if (source.EmissionTexture != null)
            shader.SetShaderParameter("emission_texture", source.EmissionTexture);
        shader.SetShaderParameter("emission_multiply",
            source.EmissionOperator == BaseMaterial3D.EmissionOperatorEnum.Multiply);
        return shader;
    }

    private static float PublicScalar(MtlMaterial source, string name, float fallback) =>
        source.PublicParams.TryGetValue(name, out var text) &&
        float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0f, value)
            : fallback;

    /// <summary>Any worker thread. Decodes a DDS and uploads it; returns the texture key, or null when unusable.</summary>
    private string RequestTexture(string pakPath, bool keepAlpha)
    {
        if (string.IsNullOrEmpty(pakPath))
            return null;
        var key = keepAlpha ? pakPath + "|alpha" : pakPath;
        return _textureKeys.GetOrAdd(key, k => new Lazy<string>(() => LoadTexture(k, pakPath, keepAlpha))).Value;
    }

    private string LoadTexture(string key, string pakPath, bool keepAlpha)
    {
        try
        {
            var bytes = PakFiles.Read(pakPath);
            if (bytes == null)
                return null;
            _textureSrgb[key] = CryDds.ReadInfo(bytes).IsSrgb;
            var image = new Image();
            if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) != Error.Ok || image.IsEmpty())
                return null;
            // Godot uploads DXT1 as RGB and drops its 1-bit alpha; cut-out materials need it as DXT5.
            if (keepAlpha && image.GetFormat() == Image.Format.Dxt1)
                image = Dxt1ToDxt5(image);
            RenderBudget.Acquire();
            _textures[key] = ImageTexture.CreateFromImage(image);
            return key;
        }
        catch (Exception e)
        {
            GD.PrintErr($"{pakPath}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Rewrites DXT1 blocks as DXT5 so their 1-bit alpha survives: twice the size, where decoding to RGBA would be eight
    /// times. A DXT5 colour block always has four colours, so in DXT1's three-colour blocks the midpoint becomes the
    /// 2:1 mix and the transparent index becomes the 1:2 mix, made invisible by the alpha block.
    /// </summary>
    private static Image Dxt1ToDxt5(Image image)
    {
        var src = image.GetData();
        var dst = new byte[src.Length * 2];
        for (int b = 0, o = 0; b + 8 <= src.Length; b += 8, o += 16)
        {
            var c0 = src[b] | (src[b + 1] << 8);
            var c1 = src[b + 2] | (src[b + 3] << 8);
            var indices = BitConverter.ToUInt32(src, b + 4);
            ulong alphaIndices = 0; // 3 bits per texel: 0 = alpha0 (opaque), 1 = alpha1 (transparent)
            if (c0 <= c1)
                for (var i = 0; i < 16; i++)
                    if (((indices >> (2 * i)) & 3) == 3)
                        alphaIndices |= 1UL << (3 * i);
            dst[o] = 255;
            dst[o + 1] = 0;
            for (var k = 0; k < 6; k++)
                dst[o + 2 + k] = (byte)(alphaIndices >> (8 * k));
            Buffer.BlockCopy(src, b, dst, o + 8, 8);
        }
        return Image.CreateFromData(image.GetWidth(), image.GetHeight(), image.HasMipmaps(), Image.Format.Dxt5, dst);
    }

    private static Color ParseColor(string rgb)
    {
        var c = rgb.Split(',').Select(x => float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1f).ToArray();
        return c.Length >= 3 ? new Color(c[0], c[1], c[2]) : Colors.White;
    }

    /// <summary>
    /// Retains the source material hue without importing CryEngine's low absolute diffuse values, whose brightness
    /// depends on its HDR lighting pipeline. The brightest channel remains one, so the texture keeps its exposure.
    /// </summary>
    private static Color NormalizedDiffuseTint(System.Numerics.Vector3 c)
    {
        var max = Math.Max(c.X, Math.Max(c.Y, c.Z));
        return max > 0.001f ? new Color(c.X / max, c.Y / max, c.Z / max) : Colors.White;
    }

    /// <summary>Colour for a material without a usable texture. CryEngine diffuse colours are dark, so lift them.</summary>
    private static Color FallbackColor(MtlMaterial sub)
    {
        var c = sub.DiffuseColor;
        var max = Math.Max(c.X, Math.Max(c.Y, c.Z));
        var scale = max > 0.001f ? 0.7f / max : 0f;
        return max > 0.001f ? new Color(c.X * scale, c.Y * scale, c.Z * scale) : new Color(0.55f, 0.55f, 0.55f);
    }
}

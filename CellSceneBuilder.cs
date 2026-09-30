using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer;

/// <summary>
/// Turns one cell's placements (object.dat, vegetation.dat, big_object.dat, entities.xml) into Godot nodes.
/// Runs on a loader thread, which also creates the meshes, materials and nodes; only adding them to the scene goes
/// through <c>post</c> (main thread), one node per action so the main thread's per-frame budget can spread them out.
/// Models of the same kind are drawn as MultiMeshes grouped per block of the cell, so Godot can cull and range-limit them.
/// </summary>
internal sealed class CellSceneBuilder(ModelLibrary models, Action<Action> post, CellSurfaceTypes surfaces,
    HashSet<(ulong, int, int)> drawnWaterVolumes, float vegetationDistanceScale = 1f, bool vegetationShadows = true,
    bool showWorldLights = true, bool mergeRoads = true, bool mergeBrushes = true, bool largerFarBlocks = true,
    bool preciseMultiMeshBounds = true, bool cheapFarLod = true, bool visibilityFades = true,
    bool limitVegetationShadows = true, WaterRenderSettings waterSettings = default)
{
    // Set per Build call; each loader thread has its own builder and each queued action captures its own copy.
    private Node3D _parent;
    private Stats _stats;
    private bool _far;
    /// <summary>The cell's water surfaces, merged per colour (rivers come in dozens of segments).</summary>
    private readonly Dictionary<(Color Tint, WaterSurfaceProfile Profile), (List<Vector3> Vertices, List<Vector2> UVs, List<int> Indices)> _water = [];
    private readonly Dictionary<(string MaterialKey, int Priority), (List<Vector3> Vertices, List<Vector2> UVs, List<int> Indices)> _roads = [];
    private readonly Dictionary<string, (List<Vector3> Vertices, List<Vector3> Normals, List<int> Indices, Material Material)> _voxels = [];
    private readonly Dictionary<(ulong Material, int X, int Z, bool CastShadow, int ViewDistance), BrushBatch> _brushBatches = [];

    /// <summary>Far cells keep brushes at least this long corner to corner and trees at least this tall.</summary>
    private const float FarMinBrushDiagonal = 20f;
    private const float FarMinTreeHeight = 8f;

    private const float BlockSize = 256f;
    private const float GrassBlockSize = 128f;
    /// <summary>Current far grouping size, retained for the performance A/B switch.</summary>
    private const float FarBlockSize = 512f;
    /// <summary>Far geometry can use a whole 1 km cell as one culling block.</summary>
    private const float LargerFarBlockSize = 1024f;

    private sealed class Group
    {
        public MeshRef Mesh;
        public bool CastShadow;
        public bool IsVegetation;
        public float MaxViewDistance;
        public readonly List<Transform3D> Transforms = [];
        public readonly List<Color> Colors = [];
        public readonly List<Color> CustomData = [];
    }

    private sealed class BrushBatch
    {
        public required SurfaceTool Tool;
        public required Vector3 Origin;
        public required bool CastShadow;
        public required float VisibilityEnd;
    }

    /// <summary>What was drawn, for the log line.</summary>
    public sealed class Stats
    {
        public int Brushes, Trees, Grass, Decals, Roads, Water, Voxels, Clouds, EntityModels, Lights, Skipped, CharacterEntities;
        /// <summary>Water volumes this cell drew; release them from the shared set when the cell is unloaded.</summary>
        public readonly List<(ulong, int, int)> WaterKeys = [];
        /// <summary>Solid placements the player can stand on (brushes, entity models, voxels; not vegetation).</summary>
        public readonly List<Collidable> Collidables = [];
        /// <summary>Near vegetation groups whose shadows are reevaluated as the focus moves.</summary>
        public readonly List<(MultiMeshInstance3D Node, Aabb Bounds)> VegetationShadowNodes = [];
        public override string ToString() =>
            $"{Brushes} brushes, {Trees} trees, {Grass} grass, {Decals} decals, {Roads} roads, {Water} water, {Voxels} voxels, " +
            $"{Clouds} clouds, {EntityModels} entity models, {Lights} lights ({CharacterEntities} character entities not drawn yet, {Skipped} skipped)";
    }

    /// <param name="cellOffset">Cry metres from the viewer origin to this cell's south-west corner.</param>
    /// <param name="heights">The cell's heightmap (for draping roads).</param>
    /// <param name="parent">Node the cell's content is added to.</param>
    /// <param name="far">
    /// The cheap version for distant cells: only big brushes and tall trees (no view-distance limit, no shadows), water,
    /// voxels and clouds; no grass, decals, roads, entities or lights, and no collision.
    /// </param>
    public Stats Build(CellContent content, NVector3 cellOffset, float[,] heights, string cellName, Node3D parent,
        bool far = false, byte[] terrainColors = null, int terrainColorSize = 0)
    {
        var stats = _stats = new Stats();
        _parent = parent;
        _far = far;
        _water.Clear();
        _roads.Clear();
        _voxels.Clear();
        _brushBatches.Clear();
        var groups = new Dictionary<(string, int, int, bool, bool), Group>();
        var objects = content.Objects;
        var farBlockSize = largerFarBlocks ? LargerFarBlockSize : FarBlockSize;

        foreach (var brush in objects.Brushes)
        {
            if (!brush.IsVisible || brush.ModelPath == null)
            {
                stats.Skipped++;
                continue;
            }
            if (far && (brush.BoundsMax - brush.BoundsMin).Length() < FarMinBrushDiagonal)
                continue;
            if (mergeBrushes && AddBrushBatch(brush, cellOffset, far ? farBlockSize : BlockSize))
                stats.Brushes++;
            else if (AddModel(groups, brush.ModelPath, CellPaths.MaterialFile(brush.MaterialPath), brush.Transform, cellOffset,
                    brush.Flags.HasFlag(CellRenderFlags.CastShadowMaps), brush.MaxViewDistance, far ? farBlockSize : BlockSize, solid: true))
                stats.Brushes++;
            else
                stats.Skipped++;
        }

        foreach (var (list, block, isGrass) in new[] { (objects.Vegetation, far ? farBlockSize : BlockSize, false), (content.PaintedVegetation, GrassBlockSize, true) })
            foreach (var veg in list)
            {
                if (!veg.IsVisible || veg.ModelPath == null)
                {
                    stats.Skipped++;
                    continue;
                }
                if (far && (isGrass || veg.BoundsMax.Z - veg.BoundsMin.Z < FarMinTreeHeight))
                    continue;
                var brightness = Mathf.Clamp(veg.Brightness, 0f, 4f);
                // Brightness remains an independent instance scalar. Terrain RGB and its enable bit travel in custom
                // data so the Vegetation shader can restrict that tint to the model's base vertices.
                var instanceColor = new Color(brightness, brightness, brightness);
                // Alpha packs two shader flags in [0, 1]: terrain colour = 1/3, grass normal bending = 2/3.
                var terrainData = new Color(1f, 1f, 1f, isGrass ? 2f / 3f : 0f);
                if (models.RenderParity && veg.UseTerrainColor && terrainColors != null && terrainColorSize > 0)
                    terrainData = SampleTerrainColor(veg.Position, terrainColors, terrainColorSize) with
                    {
                        A = terrainData.A + 1f / 3f,
                    };
                var castShadow = vegetationShadows && !isGrass && veg.Flags.HasFlag(CellRenderFlags.CastShadowMaps);
                if (AddModel(groups, veg.ModelPath, CellPaths.MaterialFile(veg.MaterialPath), veg.Transform, cellOffset,
                        castShadow, veg.MaxViewDistance, block,
                        instanceColor: instanceColor, instanceCustomData: terrainData,
                        maxViewDistanceScale: vegetationDistanceScale, vegetation: !isGrass))
                {
                    if (isGrass) stats.Grass++;
                    else stats.Trees++;
                }
                else
                    stats.Skipped++;
            }

        if (content.Entities != null && !far)
            foreach (var entity in content.Entities.Entities)
            {
                if (entity.Light != null)
                {
                    if (showWorldLights && AddLight(entity, cellOffset))
                        stats.Lights++;
                    continue;
                }
                if (!entity.HasGeometry || entity.HiddenInGame)
                    continue;
                if (entity.ModelKind is CellModelKind.Character or CellModelKind.CharacterDefinition)
                {
                    stats.CharacterEntities++;
                    continue;
                }
                if (AddModel(groups, entity.ModelPath, CellPaths.MaterialFile(entity.MaterialPath), entity.WorldTransform, cellOffset,
                        entity.CastShadow, 0f, BlockSize, solid: true))
                    stats.EntityModels++;
                else
                    stats.Skipped++;
            }

        if (!far)
        {
            foreach (var decal in objects.Decals)
                if (decal.IsVisible && AddDecal(decal, cellOffset))
                    stats.Decals++;

            foreach (var road in objects.Roads)
                if (road.IsVisible && AddRoad(road, cellOffset, heights))
                    stats.Roads++;

            foreach (var (key, (vertices, uvs, indices)) in _roads)
            {
                var material = models.GetRoadMaterial(key.MaterialKey, key.Priority);
                var node = CenteredMeshNode(vertices.ToArray(), uvs.ToArray(), indices.ToArray(), material, "Roads", false,
                    visibilityFades ? 5000f : 0f, visibilityFades);
                post(() => parent.AddChild(node));
            }
        }

        foreach (var batch in _brushBatches.Values)
        {
            var node = new MeshInstance3D
            {
                Name = "BrushBatch",
                Mesh = batch.Tool.Commit(),
                Position = batch.Origin,
                CastShadow = batch.CastShadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = batch.VisibilityEnd,
                VisibilityRangeEndMargin = visibilityFades && batch.VisibilityEnd > 0f ? Math.Min(80f, batch.VisibilityEnd * 0.2f) : 0f,
                VisibilityRangeFadeMode = visibilityFades && batch.VisibilityEnd > 0f
                    ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self
                    : GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
                Layers = ModelLibrary.ObjectLayer,
            };
            post(() => parent.AddChild(node));
        }

        foreach (var water in objects.WaterVolumes.Concat(content.NeighbourBigObjects.OfType<CellWaterVolume>()))
            if (AddWater(water, cellOffset))
                stats.Water++;
        foreach (var ((tint, profile), (vertices, uvs, indices)) in _water)
        {
            var material = WaterMaterialFactory.Get(tint, waterSettings, profile);
            var node = CenteredMeshNode(vertices.ToArray(), uvs.ToArray(), indices.ToArray(), material, "Water", false,
                visibilityFades ? 5000f : 0f, visibilityFades);
            post(() => parent.AddChild(node));
        }

        foreach (var voxel in objects.Voxels)
            if (AddVoxel(voxel, cellOffset))
                stats.Voxels++;

        foreach (var (_, (vertices, normals, indices, material)) in _voxels)
        {
            var node = CenteredMeshNode(vertices.ToArray(), null, indices.ToArray(), material, "Voxels", !_far,
                _far && visibilityFades ? 6000f : 0f, _far && visibilityFades, normals.ToArray());
            node.Layers = ModelLibrary.ObjectLayer;
            post(() => parent.AddChild(node));
        }

        foreach (var cloud in objects.DistanceClouds)
            if (AddCloud(cloud, cellOffset))
                stats.Clouds++;

        foreach (var ((_, bx, bz, _, _), g) in groups)
        {
            var trackVegetationShadows = limitVegetationShadows && !far && g.IsVegetation && g.CastShadow;
            if (models.CreateInstances(g.Mesh, g.Transforms, far ? (visibilityFades ? 6000f : 0f) : VisibilityEnd(g), $"{cellName}_{bx}_{bz}", g.CastShadow && !far,
                    models.RenderParity ? g.Colors : null, models.RenderParity ? g.CustomData : null,
                    preciseMultiMeshBounds || trackVegetationShadows, far && cheapFarLod ? 0.45f : 1f, visibilityFades) is { } node)
            {
                if (trackVegetationShadows)
                    stats.VegetationShadowNodes.Add((node, node.CustomAabb));
                post(() => parent.AddChild(node));
            }
        }
        return stats;
    }

    /// <summary>Merges small opaque brushes of different models by shared material and spatial block.</summary>
    private bool AddBrushBatch(CellBrush brush, NVector3 cellOffset, float blockSize)
    {
        var transform = CryAxes.FromRowVector(brush.Transform, cellOffset);
        var meshRef = models.Request(brush.ModelPath, CellPaths.MaterialFile(brush.MaterialPath), transform.Basis.Determinant() < 0);
        var mesh = models.GetMesh(meshRef);
        if (meshRef == null || mesh == null || meshRef.TriangleCount > 300 || mesh.GetSurfaceCount() == 0)
            return false;

        var materials = new Material[mesh.GetSurfaceCount()];
        for (var surface = 0; surface < materials.Length; surface++)
        {
            materials[surface] = mesh.SurfaceGetMaterial(surface);
            if (materials[surface] is not BaseMaterial3D baseMaterial ||
                baseMaterial.Transparency != BaseMaterial3D.TransparencyEnum.Disabled)
                return false;
        }

        var castShadow = !_far && brush.Flags.HasFlag(CellRenderFlags.CastShadowMaps);
        if (!CanBatchTransform(transform.Basis))
            return false;
        var bx = Mathf.FloorToInt(transform.Origin.X / blockSize);
        var bz = Mathf.FloorToInt(transform.Origin.Z / blockSize);
        var blockOrigin = new Vector3((bx + 0.5f) * blockSize, 0f, (bz + 0.5f) * blockSize);
        var viewDistance = _far ? 0f : Math.Max(brush.MaxViewDistance > 0f ? brush.MaxViewDistance : ModelViewDistance(meshRef.Radius), 80f);
        var visibilityEnd = viewDistance >= 4000f ? 0f : viewDistance;
        if (!_far && meshRef.Radius > 0.3f)
        {
            var scale = transform.Basis.Scale.Abs();
            _stats.Collidables.Add(new Collidable(meshRef.CollisionKey, null, transform,
                meshRef.Radius * Math.Max(scale.X, Math.Max(scale.Y, scale.Z))));
        }

        transform.Origin -= blockOrigin;
        for (var surface = 0; surface < materials.Length; surface++)
        {
            var material = materials[surface];
            var key = (material.GetInstanceId(), bx, bz, castShadow, Mathf.RoundToInt(visibilityEnd));
            if (!_brushBatches.TryGetValue(key, out var batch))
            {
                var tool = new SurfaceTool();
                tool.Begin(Mesh.PrimitiveType.Triangles);
                tool.SetMaterial(material);
                _brushBatches[key] = batch = new BrushBatch
                {
                    Tool = tool,
                    Origin = blockOrigin,
                    CastShadow = castShadow,
                    VisibilityEnd = visibilityEnd,
                };
            }
            batch.Tool.AppendFrom(mesh, surface, transform);
        }
        return true;
    }

    private bool AddModel(Dictionary<(string, int, int, bool, bool), Group> groups, string modelPath, string materialPath,
        System.Numerics.Matrix4x4 transform, NVector3 cellOffset, bool castShadow, float maxViewDistance, float blockSize,
        bool solid = false, Color? instanceColor = null, Color? instanceCustomData = null, float maxViewDistanceScale = 1f,
        bool vegetation = false)
    {
        var t = CryAxes.FromRowVector(transform, cellOffset);
        var mesh = models.Request(modelPath, materialPath, t.Basis.Determinant() < 0);
        if (mesh == null)
            return false;
        if (solid && !_far && mesh.Radius > 0.3f)
        {
            var scale = t.Basis.Scale.Abs();
            _stats.Collidables.Add(new Collidable(mesh.CollisionKey, null, t, mesh.Radius * Math.Max(scale.X, Math.Max(scale.Y, scale.Z))));
        }
        var key = (mesh.Key, Mathf.FloorToInt(t.Origin.X / blockSize), Mathf.FloorToInt(t.Origin.Z / blockSize), castShadow, vegetation);
        if (!groups.TryGetValue(key, out var group))
            groups[key] = group = new Group { Mesh = mesh, CastShadow = castShadow, IsVegetation = vegetation };
        group.Transforms.Add(t);
        group.Colors.Add(instanceColor ?? Colors.White);
        group.CustomData.Add(instanceCustomData ?? new Color(1f, 1f, 1f, 0f));
        var recordViewDistance = maxViewDistance > 0 ? maxViewDistance : ModelViewDistance(mesh.Radius);
        group.MaxViewDistance = Math.Max(group.MaxViewDistance, recordViewDistance * maxViewDistanceScale);
        return true;
    }

    /// <summary>SurfaceTool transforms normals correctly for rotation, reflection, and uniform scale.</summary>
    private static bool CanBatchTransform(Basis basis)
    {
        var x = basis.X;
        var y = basis.Y;
        var z = basis.Z;
        var sx = x.Length();
        var sy = y.Length();
        var sz = z.Length();
        var smallest = Math.Min(sx, Math.Min(sy, sz));
        var largest = Math.Max(sx, Math.Max(sy, sz));
        if (smallest < 1e-5f || largest - smallest > largest * 0.001f)
            return false;
        return Math.Abs(x.Dot(y) / (sx * sy)) < 0.001f &&
               Math.Abs(x.Dot(z) / (sx * sz)) < 0.001f &&
               Math.Abs(y.Dot(z) / (sy * sz)) < 0.001f;
    }

    /// <summary>The engine's own per-record view distance where there is one, with a floor so small props don't pop too close.</summary>
    private static float VisibilityEnd(Group g) => g.MaxViewDistance >= 4000f ? 0f : Math.Max(g.MaxViewDistance, 80f);

    private static float ModelViewDistance(float radius) => radius switch
    {
        < 1.5f => 250f,
        < 4f => 500f,
        < 10f => 1000f,
        < 30f => 2200f,
        _ => 0f,
    };

    private bool AddDecal(CellDecal decal, NVector3 cellOffset)
    {
        var parent = _parent;
        if (decal.MaterialPath == null || decal.Radius <= 0)
            return false;
        var (albedo, normal) = models.RequestDecalTextures(decal.MaterialPath);
        if (albedo == null)
            return false;
        // The decal transform maps [-1,1]^3 to the volume with Z along the surface normal. In Godot axes that Z becomes
        // the node's local Y, the axis a Godot Decal projects along (towards -Y).
        var t = CryAxes.FromRowVector(decal.Transform, cellOffset);
        // 2 = terrain only, 1 = objects only, 0 / 3 = both.
        var cullMask = decal.ProjectionType switch { 2 => 1u, 1 => ModelLibrary.ObjectLayer, _ => 1u | ModelLibrary.ObjectLayer };
        var priority = decal.SortPriority;
        post(() => parent.AddChild(new Decal
        {
            Transform = t,
            Size = new Vector3(2f, 2f, 2f),
            TextureAlbedo = models.GetTexture(albedo),
            TextureNormal = models.GetTexture(normal),
            CullMask = cullMask,
            SortingOffset = priority,
            DistanceFadeEnabled = true,
            DistanceFadeBegin = 180f,
            DistanceFadeLength = 40f,
        }));
        return true;
    }

    private bool AddRoad(CellRoad road, NVector3 cellOffset, float[,] heights)
    {
        var (positions, uvs, indices) = road.BuildMesh();
        if (indices.Length == 0)
            return false;
        // The stored heights sit on the spline, 1-4 m off the ground: drape each vertex on the terrain instead.
        var vertices = positions.Select(p => CryAxes.Point(p.X + cellOffset.X, p.Y + cellOffset.Y, HeightAt(heights, p.X, p.Y) + 0.12f)).ToArray();
        var material = models.RequestMaterial(road.MaterialPath, 0, forceAlphaBlend: true);
        var priority = road.SortPriority;
        var roadUvs = uvs.Select(uv => new Vector2(uv.X, uv.Y)).ToArray();
        var roadIndices = FlipWinding(indices);
        if (!mergeRoads)
        {
            AddSurface(vertices, roadUvs, roadIndices, material, "Road", priority);
            return true;
        }

        var key = (material, priority);
        if (!_roads.TryGetValue(key, out var merged))
            _roads[key] = merged = ([], [], []);
        var first = merged.Vertices.Count;
        merged.Vertices.AddRange(vertices);
        merged.UVs.AddRange(roadUvs);
        merged.Indices.AddRange(roadIndices.Select(i => i + first));
        return true;
    }

    private bool AddWater(CellWaterVolume water, NVector3 cellOffset)
    {
        if (!water.IsVisible || water.VolumeType == CellWaterVolumeType.Ocean)
            return false;
        // big_object.dat repeats border-crossing lakes in every cell they touch: draw each once.
        var key = (water.VolumeId, (int)MathF.Round((water.BoundsMin.X + cellOffset.X) * 10), (int)MathF.Round((water.BoundsMin.Y + cellOffset.Y) * 10));
        lock (drawnWaterVolumes)
            if (!drawnWaterVolumes.Add(key))
                return false;
        _stats.WaterKeys.Add(key);
        var (positions, uvs, indices) = water.BuildSurfaceMesh();
        if (indices.Length == 0)
            return false;
        var fog = water.FogColor;
        var tint = new Color(0.10f + fog.X * 0.4f, 0.28f + fog.Y * 0.4f, 0.36f + fog.Z * 0.4f, 0.72f);
        var materialProfile = WaterMaterialFactory.ProfileFor(water.MaterialPath);
        if (!_water.TryGetValue((tint, materialProfile), out var merged))
            _water[(tint, materialProfile)] = merged = ([], [], []);
        var first = merged.Vertices.Count;
        merged.Vertices.AddRange(positions.Select(p => CryAxes.Point(p.X + cellOffset.X, p.Y + cellOffset.Y, p.Z)));
        merged.UVs.AddRange(uvs.Length == positions.Length ? uvs.Select(uv => new Vector2(uv.X, uv.Y)) : Enumerable.Repeat(Vector2.Zero, positions.Length));
        merged.Indices.AddRange(FlipWinding(indices).Select(i => i + first));
        return true;
    }

    private bool AddVoxel(CellVoxel voxel, NVector3 cellOffset)
    {
        var parent = _parent;
        var mesh = voxel.DecodeMesh();
        if (mesh == null || mesh.Indices.Length == 0)
            return false;
        var t = CryAxes.FromRowVector(voxel.Transform, cellOffset);
        var vertices = mesh.Positions.Select(p => t * CryAxes.Point(p)).ToArray();
        var normals = mesh.Normals.Length == mesh.Positions.Length
            ? mesh.Normals.Select(n => (t.Basis * CryAxes.Point(n)).Normalized()).ToArray()
            : null;

        // Paint the whole mesh with the detail material of its most used surface type, projected triplanar.
        var surfaceIndex = mesh.SurfaceIndices.Length > 0 ? mesh.SurfaceIndices.GroupBy(i => i).MaxBy(g => g.Count())!.Key : 0;
        var surfaceName = surfaceIndex >= 0 && surfaceIndex < voxel.SurfaceNames.Count ? voxel.SurfaceNames[surfaceIndex] : null;
        var surface = surfaces?.ById.Values.FirstOrDefault(s => s.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase));
        var materialKey = surface?.DetailMaterial != null ? models.RequestMaterial(surface.DetailMaterial) : null;
        var indices = FlipWinding(mesh.Indices);
        var faces = indices.Select(i => vertices[i]).ToArray();
        var centre = vertices.Aggregate(Vector3.Zero, (a, v) => a + v) / vertices.Length;
        if (!_far)
            _stats.Collidables.Add(new Collidable(null, faces, new Transform3D(Basis.Identity, Vector3.Zero), vertices.Max(v => v.DistanceTo(centre)), centre));
        var voxelMaterial = models.GetVoxelMaterial(materialKey);
        if (voxelMaterial.Transparency != BaseMaterial3D.TransparencyEnum.Disabled)
        {
            var node = MeshNode(vertices, null, indices, voxelMaterial, "Voxel", !_far, normals);
            node.Layers = ModelLibrary.ObjectLayer;
            post(() => parent.AddChild(node));
            return true;
        }

        var voxelKey = materialKey ?? string.Empty;
        if (!_voxels.TryGetValue(voxelKey, out var merged))
            _voxels[voxelKey] = merged = ([], [], [], voxelMaterial);
        var first = merged.Vertices.Count;
        merged.Vertices.AddRange(vertices);
        merged.Normals.AddRange(normals ?? Enumerable.Repeat(Vector3.Up, vertices.Length));
        merged.Indices.AddRange(indices.Select(index => index + first));
        return true;
    }

    private bool AddCloud(CellDistanceCloud cloud, NVector3 cellOffset)
    {
        var parent = _parent;
        if (cloud.MaterialPath == null)
            return false;
        var material = models.RequestMaterial(cloud.MaterialPath, 0, forceAlphaBlend: true);
        var position = CryAxes.Point(cloud.Position + cellOffset);
        var size = new Vector2(Math.Max(cloud.SizeX, 1f) * 2f, Math.Max(cloud.SizeY, 1f) * 2f);
        var m = models.GetCloudMaterial(material);
        var node = new MeshInstance3D
        {
            Name = "Cloud",
            Mesh = new QuadMesh { Size = size, Material = m },
            Position = position,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityRangeEnd = visibilityFades ? 6000f : 0f,
            VisibilityRangeEndMargin = visibilityFades ? 400f : 0f,
            VisibilityRangeFadeMode = visibilityFades
                ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self
                : GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
        };
        post(() => parent.AddChild(node));
        return true;
    }

    private bool AddLight(CellEntity entity, NVector3 cellOffset)
    {
        var parent = _parent;
        var light = entity.Light!;
        if (!light.Active || light.AmbientOnly || light.Negative || light.Fake || light.Radius <= 0)
            return false;
        var t = CryAxes.FromRowVector(entity.WorldTransform, cellOffset);
        var color = new Color(light.Color.X, light.Color.Y, light.Color.Z);
        var energy = Math.Clamp(light.DiffuseMultiplier, 0.1f, 4f);
        var radius = light.Radius;
        post(() => parent.AddChild(new OmniLight3D
        {
            Position = t.Origin,
            OmniRange = radius,
            LightColor = color,
            LightEnergy = energy,
            DistanceFadeEnabled = true,
            DistanceFadeBegin = 250f,
            DistanceFadeLength = 50f,
        }));
        return true;
    }

    private static Color SampleTerrainColor(NVector3 position, byte[] rgba, int size)
    {
        var x = Math.Clamp(position.X / WorldStreamer.CellSize, 0f, 0.99999f);
        var y = Math.Clamp(position.Y / WorldStreamer.CellSize, 0f, 0.99999f);
        var column = Math.Clamp((int)(x * size), 0, size - 1);
        var row = Math.Clamp((int)((1f - y) * size), 0, size - 1);
        var i = (row * size + column) * 4;
        return new Color(rgba[i] / 255f, rgba[i + 1] / 255f, rgba[i + 2] / 255f).SrgbToLinear();
    }

    /// <summary>Builds a single-surface mesh with a shared road material and queues adding it.</summary>
    private void AddSurface(Vector3[] vertices, Vector2[] uvs, int[] indices, string materialKey, string name, int priority)
    {
        var parent = _parent;
        var material = models.GetRoadMaterial(materialKey, priority);
        var node = CenteredMeshNode(vertices, uvs, indices, material, name, false, visibilityFades ? 5000f : 0f, visibilityFades);
        post(() => parent.AddChild(node));
    }

    private static MeshInstance3D CenteredMeshNode(Vector3[] vertices, Vector2[] uvs, int[] indices, Material material,
        string name, bool castShadow, float visibilityEnd, bool fade, Vector3[] normals = null)
    {
        if (vertices.Length == 0)
            return MeshNode(vertices, uvs, indices, material, name, castShadow);
        var minimum = vertices[0];
        var maximum = vertices[0];
        foreach (var vertex in vertices.AsSpan(1))
        {
            minimum = minimum.Min(vertex);
            maximum = maximum.Max(vertex);
        }
        var center = (minimum + maximum) * 0.5f;
        var localVertices = vertices.Select(vertex => vertex - center).ToArray();
        var node = MeshNode(localVertices, uvs, indices, material, name, castShadow, normals);
        node.Position = center;
        node.VisibilityRangeEnd = visibilityEnd;
        node.VisibilityRangeEndMargin = fade && visibilityEnd > 0f ? Math.Min(300f, visibilityEnd * 0.2f) : 0f;
        node.VisibilityRangeFadeMode = fade && visibilityEnd > 0f
            ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self
            : GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
        return node;
    }

    private static MeshInstance3D MeshNode(Vector3[] vertices, Vector2[] uvs, int[] indices, Material material, string name, bool castShadow, Vector3[] normals = null)
    {
        RenderBudget.Acquire();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals ?? Enumerable.Repeat(Vector3.Up, vertices.Length).ToArray();
        if (uvs != null)
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            CastShadow = castShadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>The cell readers emit counter-clockwise triangles (seen from above / outside); Godot's front faces are clockwise.</summary>
    private static int[] FlipWinding(int[] indices)
    {
        var flipped = (int[])indices.Clone();
        for (var i = 0; i + 2 < flipped.Length; i += 3)
            (flipped[i + 1], flipped[i + 2]) = (flipped[i + 2], flipped[i + 1]);
        return flipped;
    }

    private static float HeightAt(float[,] heights, float x, float y)
    {
        var ux = Math.Clamp((int)MathF.Round(x / TerrainCellReader.UnitMeters), 0, TerrainCellReader.Samples - 1);
        var uy = Math.Clamp((int)MathF.Round(y / TerrainCellReader.UnitMeters), 0, TerrainCellReader.Samples - 1);
        return heights[ux, uy];
    }
}

/// <summary>
/// A solid placement: either a library model (<see cref="CollisionKey"/>) placed by <see cref="Transform"/>, or
/// ready-made triangles in viewer space (<see cref="Faces"/>). <see cref="Radius"/> bounds it around <see cref="Centre"/>.
/// </summary>
internal sealed record Collidable(string CollisionKey, Vector3[] Faces, Transform3D Transform, float Radius, Vector3? CentreOverride = null)
{
    public Vector3 Centre => CentreOverride ?? Transform.Origin;
}

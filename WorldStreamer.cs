using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer;

/// <summary>
/// Keeps the world cells around a focus point loaded. Cells within <see cref="Radius"/> load in full detail; a far ring
/// out to <see cref="FarRadius"/> loads a cheap version (coarse terrain and colour map, only large objects, no grass,
/// decals or lights) so the view reaches kilometres out. Several loader threads work at once, nearest cell first; when a
/// cell moves between rings its other version loads hidden and replaces the old one once everything for it is queued.
/// Every scene change goes through <c>post</c> (main thread, in order), so a container is only freed after everything
/// queued for it has run.
///
/// Positions: Godot space is Cry world space shifted so the origin cell's south-west corner is at 0, with Cry (x, y, z)
/// shown at (x - originX, z, -(y - originY)).
/// </summary>
internal sealed class WorldStreamer
{
    public const int CellSize = 1024;
    private const int LoaderThreads = 3;
    private const int FarTerrainStep = 8;
    private const int FullTerrainLodStep = 4;
    private const int FarTextureLevel = 2;
    private const float FullTerrainLodDistance = 600f;
    private const float FullTerrainLodFadeDistance = 100f;

    private enum Detail { Full, Far }

    private sealed class CellEntry
    {
        public Detail? Loaded;    // version on screen (or about to be)
        public Detail? Loading;   // version a loader is building
        public Node3D Container;  // main thread only
        public List<(ulong, int, int)> WaterKeys = [];
        public List<(MultiMeshInstance3D Node, Aabb Bounds)> VegetationShadowNodes = [];
    }

    private sealed record TerrainArrays(Vector3[] Vertices, Vector3[] Normals, Color[] Colors, Vector2[] UVs, int[] Indices);
    private sealed record WaterTriangle(NVector3 A, NVector3 B, NVector3 C, float Depth, bool Ocean);

    private readonly Node3D _root;
    private readonly Action<Action> _post;
    private readonly ModelLibrary _models;
    private readonly DetailLayerBank _bank;
    private readonly Lock _cellsGate = new();
    private readonly Dictionary<(int X, int Y), CellEntry> _cells = [];
    private readonly ConcurrentDictionary<(int X, int Y), float[,]> _heights = new();
    private readonly ConcurrentDictionary<(int X, int Y), int[,]> _surfaceIds = new();
    private readonly ConcurrentDictionary<(int X, int Y), WaterTriangle[]> _waterSurfaces = new();
    private readonly ConcurrentDictionary<(int X, int Y), OceanMaskCell> _oceanMasks = new();
    private readonly HashSet<(ulong, int, int)> _drawnWater = [];
    private readonly ConcurrentDictionary<(int X, int Y), Collidable[]> _collidables = new();
    private volatile Dictionary<(int, int), int> _zones = []; // (world sector x, world sector y) -> zone key
    private readonly StandardMaterial3D _fallbackTerrainMaterial = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.95f };
    private volatile int _focusX, _focusY;
    private volatile float _focusWorldX, _focusWorldY;
    private volatile bool _stopped;
    private volatile bool _focusCleanupPending;
    private long _lastVegetationShadowUpdate;
    private WaterSurfaceProfile _oceanWaterProfile = WaterMaterialFactory.DefaultOceanProfile;
    private TerrainDetail _detail;
    private CellVegetationTable _vegetation;
    private CellSurfaceTypes _surfaces;
    private string _worldRoot;

    public string World { get; }
    public int OriginCellX { get; }
    public int OriginCellY { get; }
    /// <summary>Cells within this many cells of the focus load in full detail.</summary>
    public int Radius { get; set; }
    /// <summary>Cells out to this many cells of the focus load in the cheap far version.</summary>
    public int FarRadius { get; set; }
    public int TerrainStep { get; init; } = 2;
    public int TerrainTextureLevel { get; init; } = 4;
    /// <summary>When enabled, full cells fade from their step-2 terrain mesh to a step-4 mesh past 600 m.</summary>
    public bool TerrainLodEnabled { get; init; }
    public bool ShowObjects { get; init; } = true;
    public float VegetationDistanceScale { get; init; } = 1f;
    public bool VegetationShadows { get; init; } = true;
    public bool ShowWorldLights { get; init; } = true;
    public bool MergeBrushes { get; init; } = true;
    public bool LargerFarBlocks { get; init; } = true;
    public bool MergeRoads { get; init; } = true;
    public bool PreciseMultiMeshBounds { get; init; } = true;
    public bool CheapFarLod { get; init; } = true;
    public bool VisibilityFades { get; init; } = true;
    public bool LimitVegetationShadows { get; init; } = true;
    /// <summary>Use the shared sampled-normal/PBR water material instead of the legacy transparent colour.</summary>
    public bool EnhancedWater { get; init; } = true;
    /// <summary>Animate the authored water normal map. This does not move geometry or alter water collision heights.</summary>
    public bool WaterWaves { get; init; } = true;
    /// <summary>Use authored Fresnel/specular reflection parameters against the active sky environment.</summary>
    public bool WaterReflections { get; init; } = true;
    /// <summary>Sample the opaque depth buffer for shallow-water colour and shoreline foam.</summary>
    public bool WaterDepthEffects { get; init; } = true;
    /// <summary>Displace the tessellated ocean plane from authored HeightScale and OceanAnimation values.</summary>
    public bool OceanVertexWaves { get; init; }
    public float VegetationShadowDistance { get; init; } = 350f;
    public bool SkipFocusScanWithinCell { get; init; } = true;
    public float OceanLevel { get; private set; } = 100f;
    public volatile string Status = "";
    public int LoadedCells
    {
        get
        {
            lock (_cellsGate)
                return _cells.Values.Count(c => c.Loaded != null);
        }
    }
    /// <summary>Set once every cell around the first focus is loaded at the detail it should have.</summary>
    public volatile bool InitialLoadDone;

    public WorldStreamer(Node3D root, Action<Action> post, ModelLibrary models, DetailLayerBank bank, string world,
        int originCellX, int originCellY, int radius, int farRadius)
    {
        _root = root;
        _post = post;
        _models = models;
        _bank = bank;
        World = world;
        OriginCellX = originCellX;
        OriginCellY = originCellY;
        Radius = radius;
        FarRadius = Math.Max(radius, farRadius);
        _focusX = originCellX;
        _focusY = originCellY;
        _focusWorldX = (originCellX + 0.5f) * CellSize;
        _focusWorldY = (originCellY + 0.5f) * CellSize;
    }

    public Vector3 ToGodot(float cryX, float cryY, float cryZ) => new(cryX - OriginCellX * CellSize, cryZ, -(cryY - OriginCellY * CellSize));

    public NVector3 ToCry(Vector3 godot) => new(godot.X + OriginCellX * CellSize, -godot.Z + OriginCellY * CellSize, godot.Y);

    /// <summary>Main thread. Moves the focus; cells are loaded around it and dropped once they are two cells past the far ring.</summary>
    public void SetFocus(float cryX, float cryY)
    {
        var cx = (int)MathF.Floor(cryX / CellSize);
        var cy = (int)MathF.Floor(cryY / CellSize);
        _focusWorldX = cryX;
        _focusWorldY = cryY;
        if (SkipFocusScanWithinCell && cx == _focusX && cy == _focusY && !_focusCleanupPending)
            return;
        _focusX = cx;
        _focusY = cy;
        List<((int X, int Y) Cell, CellEntry Entry)> dropped = null;
        lock (_cellsGate)
        {
            var cleanupPending = false;
            foreach (var (cell, entry) in _cells)
                if (Math.Max(Math.Abs(cell.X - cx), Math.Abs(cell.Y - cy)) > FarRadius + 1)
                {
                    if (entry.Loading == null)
                        (dropped ??= []).Add((cell, entry));
                    else
                        cleanupPending = true;
                }
            if (dropped != null)
                foreach (var (cell, _) in dropped)
                    _cells.Remove(cell);
            _focusCleanupPending = cleanupPending;
        }
        if (dropped != null)
        {
            foreach (var (cell, entry) in dropped)
            {
                _heights.TryRemove(cell, out _);
                _surfaceIds.TryRemove(cell, out _);
                _waterSurfaces.TryRemove(cell, out _);
                _oceanMasks.TryRemove(cell, out _);
                _collidables.TryRemove(cell, out _);
                ReleaseWater(entry.WaterKeys);
                _post(() =>
                {
                    WorldSoundService.Shared?.RemoveCell(cell);
                    entry.Container?.QueueFree();
                });
            }
        }
    }

    /// <summary>Main thread. Rechecks vegetation caster groups periodically as the player or camera moves.</summary>
    public void UpdateVegetationShadows(Vector3 focus)
    {
        if (!LimitVegetationShadows)
            return;
        var now = Stopwatch.GetTimestamp();
        if (_lastVegetationShadowUpdate != 0 && Stopwatch.GetElapsedTime(_lastVegetationShadowUpdate, now).TotalSeconds < 0.25)
            return;
        _lastVegetationShadowUpdate = now;
        var maxDistanceSquared = VegetationShadowDistance * VegetationShadowDistance;
        lock (_cellsGate)
            foreach (var entry in _cells.Values)
            foreach (var (node, bounds) in entry.VegetationShadowNodes)
            {
                if (!GodotObject.IsInstanceValid(node))
                    continue;
                var minX = node.Position.X + bounds.Position.X;
                var maxX = minX + bounds.Size.X;
                var minZ = node.Position.Z + bounds.Position.Z;
                var maxZ = minZ + bounds.Size.Z;
                var farthestX = Math.Max(Math.Abs(minX - focus.X), Math.Abs(maxX - focus.X));
                var farthestZ = Math.Max(Math.Abs(minZ - focus.Z), Math.Abs(maxZ - focus.Z));
                var cast = farthestX * farthestX + farthestZ * farthestZ <= maxDistanceSquared;
                var setting = cast ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
                if (node.CastShadow != setting)
                    node.CastShadow = setting;
            }
    }

    /// <summary>True once the heightmap of the cell containing a Cry world position is loaded.</summary>
    public bool HasHeightsAt(float cryX, float cryY) =>
        _heights.ContainsKey(((int)MathF.Floor(cryX / CellSize), (int)MathF.Floor(cryY / CellSize)));

    /// <summary>True when the loaded detail ring around a position has reached its requested cell detail.</summary>
    public bool AreNearbyCellsReadyAt(float cryX, float cryY, int cellRadius = 1)
    {
        var cx = (int)MathF.Floor(cryX / CellSize);
        var cy = (int)MathF.Floor(cryY / CellSize);
        cellRadius = Math.Clamp(cellRadius, 0, 2);
        lock (_cellsGate)
        {
            for (var dy = -cellRadius; dy <= cellRadius; dy++)
            for (var dx = -cellRadius; dx <= cellRadius; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                if (x < 0 || y < 0)
                    continue;
                var detail = Math.Max(Math.Abs(dx), Math.Abs(dy)) <= Radius ? Detail.Full : Detail.Far;
                if (!_cells.TryGetValue((x, y), out var entry) || entry.Loading != null || entry.Loaded != detail)
                    return false;
            }
        }
        return true;
    }

    /// <summary>Terrain height at a Cry world position, or the ocean level where no cell is loaded.</summary>
    public float TerrainHeightAt(float cryX, float cryY)
    {
        var cell = ((int)MathF.Floor(cryX / CellSize), (int)MathF.Floor(cryY / CellSize));
        if (!_heights.TryGetValue(cell, out var heights))
            return OceanLevel;
        // Bilinear between the 2 m samples.
        var fx = Math.Clamp((cryX - cell.Item1 * CellSize) / TerrainCellReader.UnitMeters, 0f, TerrainCellReader.Samples - 1.001f);
        var fy = Math.Clamp((cryY - cell.Item2 * CellSize) / TerrainCellReader.UnitMeters, 0f, TerrainCellReader.Samples - 1.001f);
        int x0 = (int)fx, y0 = (int)fy;
        float tx = fx - x0, ty = fy - y0;
        var h0 = heights[x0, y0] + (heights[x0 + 1, y0] - heights[x0, y0]) * tx;
        var h1 = heights[x0, y0 + 1] + (heights[x0 + 1, y0 + 1] - heights[x0, y0 + 1]) * tx;
        return h0 + (h1 - h0) * ty;
    }

    /// <summary>Water surface at a loaded Cry position. Terrain and water remain separate physical surfaces.</summary>
    public bool TryWaterSurfaceAt(float cryX, float cryY, out float height, float? cryZ = null)
    {
        height = 0;
        if (!HasHeightsAt(cryX, cryY))
            return false;
        var terrain = TerrainHeightAt(cryX, cryY);
        var found = false;
        var cell = ((int)MathF.Floor(cryX / CellSize), (int)MathF.Floor(cryY / CellSize));
        var localX = cryX - cell.Item1 * CellSize;
        var localY = cryY - cell.Item2 * CellSize;
        if (terrain < OceanLevel - 0.05f && _oceanMasks.TryGetValue(cell, out var oceanMask) &&
            oceanMask.Contains(localX, localY))
        {
            height = OceanLevel;
            found = true;
        }
        if (!_waterSurfaces.TryGetValue(cell, out var triangles))
            return found; // below-sea terrain is wet only when the authored-data mask marks it
        foreach (var triangle in triangles)
        {
            if (!TryTriangleHeight(cryX, cryY, triangle, out var candidate))
                continue;
            if (triangle.Ocean)
            {
                if (terrain >= OceanLevel - 0.05f)
                    continue;
                candidate = OceanLevel;
            }
            else if (cryZ.HasValue && cryZ.Value < candidate - triangle.Depth)
                continue;
            if (!found || candidate > height)
            {
                height = candidate;
                found = true;
            }
        }
        return found;
    }

    /// <summary>Ocean-only surface query used to keep the rendered underside gate in sync with the swim mask.</summary>
    private bool TryOceanSurfaceAt(float cryX, float cryY, out float height)
    {
        height = 0f;
        if (!HasHeightsAt(cryX, cryY))
            return false;
        var cell = ((int)MathF.Floor(cryX / CellSize), (int)MathF.Floor(cryY / CellSize));
        if (TerrainHeightAt(cryX, cryY) < OceanLevel - 0.05f &&
            _oceanMasks.TryGetValue(cell, out var mask) &&
            mask.Contains(cryX - cell.Item1 * CellSize, cryY - cell.Item2 * CellSize))
        {
            height = OceanLevel;
            return true;
        }
        if (!_waterSurfaces.TryGetValue(cell, out var triangles))
            return false;
        foreach (var triangle in triangles)
            if (triangle.Ocean && TryTriangleHeight(cryX, cryY, triangle, out _) &&
                TerrainHeightAt(cryX, cryY) < OceanLevel - 0.05f)
            {
                height = OceanLevel;
                return true;
            }
        return false;
    }

    public void UpdateOceanCamera(float cryX, float cryY, float cryZ) =>
        WaterMaterialFactory.SetCameraInOcean(TryOceanSurfaceAt(cryX, cryY, out var height) && cryZ < height - 0.1f);

    /// <summary>Surface type name under a Cry world position, from the heightmap's global surface ID grid.</summary>
    public string? SurfaceNameAt(float cryX, float cryY)
    {
        var cell = ((int)MathF.Floor(cryX / CellSize), (int)MathF.Floor(cryY / CellSize));
        if (!_surfaceIds.TryGetValue(cell, out var ids)) return null;
        var localX = Math.Clamp((int)MathF.Round((cryX - cell.Item1 * CellSize) / (CellSize / (float)(TerrainSurfaceReader.Samples - 1))), 0, TerrainSurfaceReader.Samples - 1);
        var localY = Math.Clamp((int)MathF.Round((cryY - cell.Item2 * CellSize) / (CellSize / (float)(TerrainSurfaceReader.Samples - 1))), 0, TerrainSurfaceReader.Samples - 1);
        var id = ids[localX, localY];
        return _surfaces?.ById.TryGetValue(id, out var surface) == true ? surface.Name : null;
    }

    private static bool TryTriangleHeight(float x, float y, WaterTriangle t, out float z)
    {
        var denominator = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
        if (Math.Abs(denominator) < 1e-6f)
        {
            z = 0;
            return false;
        }
        var a = ((t.B.Y - t.C.Y) * (x - t.C.X) + (t.C.X - t.B.X) * (y - t.C.Y)) / denominator;
        var b = ((t.C.Y - t.A.Y) * (x - t.C.X) + (t.A.X - t.C.X) * (y - t.C.Y)) / denominator;
        var c = 1f - a - b;
        z = a * t.A.Z + b * t.B.Z + c * t.C.Z;
        return a >= -0.0001f && b >= -0.0001f && c >= -0.0001f;
    }

    /// <summary>Zone key (zones.zone_key, the world.xml Zone id) at a Cry world position, or -1.</summary>
    public int ZoneAt(float cryX, float cryY) =>
        _zones.TryGetValue(((int)MathF.Floor(cryX / 64f), (int)MathF.Floor(cryY / 64f)), out var zone) ? zone : -1;

    /// <summary>world.xml lists, per zone, the cells and 64 m sectors it covers.</summary>
    private static Dictionary<(int, int), int> ParseZones(System.Xml.Linq.XElement world)
    {
        var zones = new Dictionary<(int, int), int>();
        if (world == null)
            return zones;
        foreach (var zone in world.Descendants("Zone"))
        {
            var id = (int?)zone.Attribute("id") ?? -1;
            foreach (var cell in zone.Descendants("cell"))
            {
                var cx = (int)cell.Attribute("x");
                var cy = (int)cell.Attribute("y");
                foreach (var sector in cell.Descendants("sector"))
                    zones[(cx * 16 + (int)sector.Attribute("x"), cy * 16 + (int)sector.Attribute("y"))] = id;
            }
        }
        return zones;
    }

    /// <summary>Solid placements whose bounds come within <paramref name="range"/> metres of a viewer-space point.</summary>
    public IEnumerable<Collidable> CollidablesNear(Vector3 position, float range)
    {
        var cry = ToCry(position);
        var cx = (int)MathF.Floor(cry.X / CellSize);
        var cy = (int)MathF.Floor(cry.Y / CellSize);
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
            if (_collidables.TryGetValue((cx + dx, cy + dy), out var list))
                foreach (var c in list)
                    if (c.Centre.DistanceTo(position) - c.Radius <= range)
                        yield return c;
    }

    public void Start() => Task.Run(Run);

    public void Stop() => _stopped = true;

    private readonly List<Thread> _loaderThreads = new();

    /// <summary>
    /// Main thread, on quit: stops the loaders and waits (bounded) for any cell build in flight, so no loader touches
    /// engine objects (cached materials, meshes) after the engine starts freeing them. Call after RenderBudget.Release()
    /// so a loader waiting for a frame budget can finish. A build still running after the limit is abandoned; its
    /// exception, if any, is not logged (see Loader).
    /// </summary>
    public void StopAndWait(TimeSpan limit)
    {
        _stopped = true;
        Thread[] threads;
        lock (_loaderThreads)
            threads = _loaderThreads.ToArray();
        var deadline = Stopwatch.StartNew();
        foreach (var thread in threads)
        {
            var left = limit - deadline.Elapsed;
            if (left <= TimeSpan.Zero || !thread.Join(left))
            {
                GD.Print($"World streamer: {thread.Name} still building a cell after {limit.TotalSeconds:F0} s; quitting without it");
                break;
            }
        }
    }

    private void Run()
    {
        try
        {
            _worldRoot = $"game/worlds/{World}";
            var world = System.Xml.Linq.XDocument.Parse(PakFiles.ReadText($"{_worldRoot}/world.xml") ?? "<World/>").Root;
            OceanLevel = (float?)world?.Attribute("oceanLevel") ?? 100f;
            _zones = ParseZones(world);
            var vegetationXml = PakFiles.Read($"{_worldRoot}/vegetation.xml");
            _vegetation = vegetationXml != null ? CellVegetationTable.Read(vegetationXml) : null;
            var surfaceXml = PakFiles.Read($"{_worldRoot}/surface.xml");
            _surfaces = surfaceXml != null ? CellSurfaceTypes.Read(surfaceXml) : null;
            _detail = new TerrainDetail(_bank, _models, _surfaces);
            _oceanWaterProfile = WaterMaterialFactory.OceanProfileFor(_worldRoot);
            if (EnhancedWater && (WaterWaves || WaterDepthEffects))
                WaterMaterialFactory.Prepare(_oceanWaterProfile);
            for (var i = 0; i < LoaderThreads && !_stopped; i++)
            {
                var thread = new Thread(Loader) { IsBackground = true, Name = $"CellLoader{i}" };
                lock (_loaderThreads)
                    _loaderThreads.Add(thread);
                thread.Start();
            }
        }
        catch (Exception e)
        {
            Status = $"World loading failed: {e.Message}";
            GD.PrintErr(e.ToString());
        }
    }

    /// <summary>A loader thread: repeatedly claims the nearest cell that isn't loaded at the detail it should have.</summary>
    private void Loader()
    {
        var waterSettings = new WaterRenderSettings(EnhancedWater, WaterWaves, WaterReflections,
            WaterDepthEffects, OceanVertexWaves);
        var builder = new CellSceneBuilder(_models, _post, _surfaces, _drawnWater, VegetationDistanceScale,
            VegetationShadows, ShowWorldLights, MergeRoads, MergeBrushes, LargerFarBlocks, PreciseMultiMeshBounds,
            CheapFarLod, VisibilityFades, LimitVegetationShadows, waterSettings);
        var watch = Stopwatch.StartNew();
        while (!_stopped)
        {
            var job = Claim();
            if (job == null)
            {
                if (!InitialLoadDone && Idle())
                {
                    InitialLoadDone = true;
                    Status = $"{World}: {LoadedCells} cells in {watch.Elapsed.TotalSeconds:F1} s; {_models.ModelCount} models, {_models.TextureCount} textures";
                    GD.Print(Status);
                }
                Thread.Sleep(50);
                continue;
            }
            var (cell, detail, entry) = job.Value;
            try
            {
                LoadCell(cell, detail, entry, builder);
            }
            catch (Exception e) when (!(_stopped && e is ObjectDisposedException))
            {
                GD.PrintErr($"Cell {cell.X},{cell.Y}: {e}");
            }
            catch (ObjectDisposedException)
            {
                return; // quit while this cell was building: the engine already freed what it used
            }
            lock (_cellsGate)
            {
                entry.Loaded = detail;
                entry.Loading = null;
            }
        }
    }

    private ((int X, int Y) Cell, Detail Detail, CellEntry Entry)? Claim()
    {
        int fx = _focusX, fy = _focusY;
        lock (_cellsGate)
            foreach (var (cell, detail) in WantedCells(fx, fy))
            {
                if (_cells.TryGetValue(cell, out var entry) && (entry.Loading != null || entry.Loaded == detail))
                    continue;
                entry ??= _cells[cell] = new CellEntry();
                entry.Loading = detail;
                return (cell, detail, entry);
            }
        return null;
    }

    /// <summary>True when every wanted cell is on screen at its wanted detail and nothing is loading.</summary>
    private bool Idle()
    {
        int fx = _focusX, fy = _focusY;
        lock (_cellsGate)
            return _cells.Values.All(e => e.Loading == null) &&
                   WantedCells(fx, fy).All(w => _cells.TryGetValue(w.Cell, out var e) && e.Loaded == w.Detail);
    }

    private List<((int X, int Y) Cell, Detail Detail)> WantedCells(int fx, int fy)
    {
        var list = new List<((int, int), Detail, int)>();
        for (var dy = -FarRadius; dy <= FarRadius; dy++)
        for (var dx = -FarRadius; dx <= FarRadius; dx++)
        {
            if (fx + dx < 0 || fy + dy < 0)
                continue;
            var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
            list.Add(((fx + dx, fy + dy), ring <= Radius ? Detail.Full : Detail.Far, dx * dx + dy * dy));
        }
        // Nearest first; full-detail cells before far ones at the same distance.
        return list.OrderBy(c => c.Item3).ThenBy(c => c.Item2).Select(c => (c.Item1, c.Item2)).ToList();
    }

    private void LoadCell((int X, int Y) cell, Detail detail, CellEntry entry, CellSceneBuilder builder)
    {
        var watch = Stopwatch.StartNew();
        var full = detail == Detail.Full;
        var cellRoot = $"{_worldRoot}/cells/{cell.X:000}_{cell.Y:000}/client";
        var name = $"Cell_{cell.X:000}_{cell.Y:000}_{detail}";
        // A replacement version builds hidden and takes over from the old one at the end.
        var container = new Node3D { Name = name, Visible = entry.Loaded == null };
        List<(MultiMeshInstance3D Node, Aabb Bounds)> vegetationShadowNodes = [];
        _post(() => _root.AddChild(container));

        var heightmap = PakFiles.Read($"{cellRoot}/terrain/heightmap.dat");
        if (heightmap != null)
        {
            if (full)
                Status = $"Loading cell {cell.X},{cell.Y} ...";
            float[,] heights;
            using (var stream = new MemoryStream(heightmap))
                heights = TerrainCellReader.Read(stream, out _);
            _heights[cell] = heights;

            var origin = new Vector2((cell.X - OriginCellX) * CellSize, (cell.Y - OriginCellY) * CellSize);
            var ctc = PakFiles.Read($"{cellRoot}/terrain/cover.ctc");
            var cover = ctc == null ? null : TerrainCoverFast.Build(ctc, full ? TerrainTextureLevel : Math.Min(FarTextureLevel, TerrainTextureLevel));
            var layers = cover != null ? _detail?.Build(heightmap) : null;
            var material = CreateTerrainMaterial(cover, layers);
            var terrain = BuildTerrain(heights, full ? TerrainStep : FarTerrainStep, skirt: !full || TerrainLodEnabled);
            var terrainNode = CreateTerrain(terrain, material, "Terrain");
            terrainNode.Position = new Vector3(origin.X + CellSize * 0.5f, 0f, -origin.Y - CellSize * 0.5f);
            if (!full && VisibilityFades)
            {
                terrainNode.VisibilityRangeEnd = 6000f;
                terrainNode.VisibilityRangeEndMargin = VisibilityFades ? 500f : 0f;
                terrainNode.VisibilityRangeFadeMode = VisibilityFades
                    ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self
                    : GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
            }
            _post(() => container.AddChild(terrainNode));
            if (full && TerrainLodEnabled && TerrainStep < FullTerrainLodStep)
            {
                var terrainLod = BuildTerrain(heights, FullTerrainLodStep, skirt: true);
                var terrainLodNode = CreateTerrain(terrainLod, material, "TerrainLod");
                terrainLodNode.Position = terrainNode.Position;
                terrainNode.VisibilityRangeEnd = FullTerrainLodDistance;
                terrainNode.VisibilityRangeEndMargin = VisibilityFades ? FullTerrainLodFadeDistance : 0f;
                terrainNode.VisibilityRangeFadeMode = VisibilityFades
                    ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self
                    : GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
                terrainLodNode.VisibilityRangeBegin = FullTerrainLodDistance;
                terrainLodNode.VisibilityRangeBeginMargin = VisibilityFades ? FullTerrainLodFadeDistance : 0f;
                terrainLodNode.VisibilityRangeFadeMode = VisibilityFades
                    ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self
                    : GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
                _post(() => container.AddChild(terrainLodNode));
            }

            var surfaceIds = TerrainSurfaceReader.ReadSurfaceIds(heightmap);
            _surfaceIds[cell] = surfaceIds;
            var inferredOceanMask = OceanMaskBuilder.Build(heights, surfaceIds, _surfaces, OceanLevel);
            var content = CellObjectReader.ReadCell(file => PakFiles.Read($"{cellRoot}/{file}"), _vegetation);
            var soundService = WorldSoundService.Shared;
            if (soundService != null)
                _post(() => soundService.SetCellEntities(cell, content.Entities, new NVector3(origin.X, origin.Y, 0f)));
            var waterTriangles = BuildWaterSurfaces(content, cell);
            _waterSurfaces[cell] = waterTriangles;
            var explicitOcean = waterTriangles.Any(t => t.Ocean);
            if (explicitOcean)
                _oceanMasks.TryRemove(cell, out _);
            else
                _oceanMasks[cell] = inferredOceanMask;
            var oceanNode = BuildOceanSurface(cell, origin, explicitOcean ? waterTriangles : [], inferredOceanMask);
            if (oceanNode != null)
                _post(() => container.AddChild(oceanNode));
            if (ShowObjects)
            {
                byte[] terrainColors = null;
                var terrainColorSize = 0;
                if (_models.RenderParity && ctc != null &&
                    content.Objects.Vegetation.Concat(content.PaintedVegetation).Any(v => v.UseTerrainColor))
                    terrainColors = TerrainTextureReader.ReadCellRgba(ctc, 0, out terrainColorSize);
                ReleaseWater(entry.WaterKeys); // this version draws the cell's water again
                var stats = builder.Build(content, new NVector3(origin.X, origin.Y, 0f), heights, name, container,
                    far: !full, terrainColors: terrainColors, terrainColorSize: terrainColorSize);
                vegetationShadowNodes = stats.VegetationShadowNodes;
                entry.WaterKeys = stats.WaterKeys;
                if (full)
                    _collidables[cell] = stats.Collidables.ToArray();
                else
                    _collidables.TryRemove(cell, out _);
                GD.Print($"{name} in {watch.ElapsedMilliseconds} ms: {stats}");
            }
        }

        _post(() =>
        {
            var stale = false;
            Node3D old = null;
            lock (_cellsGate)
            {
                if (!_cells.TryGetValue(cell, out var current) || !ReferenceEquals(current, entry))
                    stale = true;
                else if (Math.Max(Math.Abs(cell.X - _focusX), Math.Abs(cell.Y - _focusY)) > FarRadius + 1)
                {
                    _cells.Remove(cell);
                    stale = true;
                }
                else
                {
                    old = entry.Container;
                    entry.Container = container;
                    entry.VegetationShadowNodes = vegetationShadowNodes;
                    container.Visible = true;
                }
            }
            if (stale)
            {
                _heights.TryRemove(cell, out _);
                _surfaceIds.TryRemove(cell, out _);
                _waterSurfaces.TryRemove(cell, out _);
                _oceanMasks.TryRemove(cell, out _);
                _collidables.TryRemove(cell, out _);
                ReleaseWater(entry.WaterKeys);
                WorldSoundService.Shared?.RemoveCell(cell);
                container.QueueFree();
                return;
            }
            old?.QueueFree();
        });
    }

    private static WaterTriangle[] BuildWaterSurfaces(CellContent content, (int X, int Y) cell)
    {
        var offset = new NVector3(cell.X * CellSize, cell.Y * CellSize, 0);
        var result = new List<WaterTriangle>();
        foreach (var water in content.Objects.WaterVolumes.Concat(content.NeighbourBigObjects.OfType<CellWaterVolume>()))
        {
            if (!water.IsVisible)
                continue;
            // Explicit Ocean contours are authoritative where present; use the physics contour when the render polygon is absent.
            NVector3[] positions;
            int[] indices;
            if (water.VolumeType == CellWaterVolumeType.Ocean && water.PhysicsContour.Count >= 3)
            {
                positions = water.PhysicsContour.ToArray();
                indices = CellGeometry.TriangulatePolygon(positions);
            }
            else
                (positions, _, indices) = water.BuildSurfaceMesh();
            for (var i = 0; i + 2 < indices.Length; i += 3)
                result.Add(new WaterTriangle(positions[indices[i]] + offset, positions[indices[i + 1]] + offset,
                    positions[indices[i + 2]] + offset, water.Depth, water.VolumeType == CellWaterVolumeType.Ocean));
        }
        return result.ToArray();
    }

    private void ReleaseWater(List<(ulong, int, int)> keys)
    {
        lock (_drawnWater)
            foreach (var key in keys)
                _drawnWater.Remove(key);
    }

    /// <summary>
    /// The centered terrain grid of one cell with a vertex every <paramref name="step"/> samples. UVs follow the cover texture:
    /// (uy / 512, ux / 512). Far cells get a skirt hanging 20 m down from their edges to hide cracks against finer neighbours.
    /// </summary>
    private TerrainArrays BuildTerrain(float[,] heights, int step, bool skirt)
    {
        var last = TerrainCellReader.Samples - 1;
        var n = last / step + 1;
        var capacity = n * n + (skirt ? 4 * n : 0);
        var vertices = new List<Vector3>(capacity);
        var normals = new List<Vector3>(capacity);
        var colors = new List<Color>(capacity);
        var uvs = new List<Vector2>(capacity);

        for (var j = 0; j < n; j++)
        for (var i = 0; i < n; i++)
        {
            var ux = i * step;
            var uy = j * step;
            var h = heights[ux, uy];
            int xl = Math.Max(ux - step, 0), xr = Math.Min(ux + step, last);
            int yd = Math.Max(uy - step, 0), yu = Math.Min(uy + step, last);
            var dhdx = (heights[xr, uy] - heights[xl, uy]) / ((xr - xl) * TerrainCellReader.UnitMeters);
            var dhdy = (heights[ux, yu] - heights[ux, yd]) / ((yu - yd) * TerrainCellReader.UnitMeters);
            // Cry normal (-dh/dx, -dh/dy, 1) in Godot axes.
            var normal = new Vector3(-dhdx, 1f, dhdy).Normalized();
            vertices.Add(new Vector3(ux * TerrainCellReader.UnitMeters - CellSize * 0.5f, h,
                CellSize * 0.5f - uy * TerrainCellReader.UnitMeters));
            normals.Add(normal);
            colors.Add(TerrainColor(h, normal.Y));
            uvs.Add(new Vector2(uy / (float)last, ux / (float)last));
        }

        // Godot treats clockwise triangles as front faces. Seen from above, +x is east (right) and +j is north (up).
        var indices = new List<int>((n - 1) * (n - 1) * 6 + (skirt ? 48 * n : 0));
        for (var j = 0; j < n - 1; j++)
        for (var i = 0; i < n - 1; i++)
        {
            var a = j * n + i;
            var b = a + 1;
            var c = a + n;
            var d = c + 1;
            indices.AddRange([a, c, d, a, d, b]);
        }

        if (skirt)
        {
            int[][] edges =
            [
                Enumerable.Range(0, n).ToArray(),                          // south row
                Enumerable.Range(0, n).Select(i => (n - 1) * n + i).ToArray(), // north row
                Enumerable.Range(0, n).Select(j => j * n).ToArray(),           // west column
                Enumerable.Range(0, n).Select(j => j * n + n - 1).ToArray(),   // east column
            ];
            foreach (var edge in edges)
            {
                var first = vertices.Count;
                foreach (var v in edge)
                {
                    vertices.Add(vertices[v] - new Vector3(0f, 20f, 0f));
                    normals.Add(normals[v]);
                    colors.Add(colors[v]);
                    uvs.Add(uvs[v]);
                }
                // Both windings, so the skirt shows from either side.
                for (var k = 0; k + 1 < edge.Length; k++)
                {
                    int a = edge[k], b = edge[k + 1], sa = first + k, sb = first + k + 1;
                    indices.AddRange([a, b, sb, a, sb, sa, a, sb, b, a, sa, sb]);
                }
            }
        }
        return new TerrainArrays(vertices.ToArray(), normals.ToArray(), colors.ToArray(), uvs.ToArray(), indices.ToArray());
    }

    /// <summary>Height and slope colouring, used when a cell has no cover texture.</summary>
    private Color TerrainColor(float height, float upness)
    {
        var grass = new Color(0.30f, 0.45f, 0.20f);
        var dryGrass = new Color(0.47f, 0.49f, 0.29f);
        var rock = new Color(0.42f, 0.38f, 0.34f);
        var sand = new Color(0.74f, 0.68f, 0.50f);
        var color = grass.Lerp(dryGrass, Mathf.Clamp((height - OceanLevel) / 700f, 0f, 1f));
        color = sand.Lerp(color, Mathf.Clamp((height - OceanLevel - 1f) / 4f, 0f, 1f));
        return color.Lerp(rock, Mathf.Clamp((0.85f - upness) / 0.25f, 0f, 1f));
    }

    /// <summary>Any thread. Creates one terrain material so LOD meshes of a full cell share the authored cover mapping.</summary>
    private Material CreateTerrainMaterial(Image cover, TerrainDetail.Layers layers)
    {
        RenderBudget.Acquire(2);
        if (cover == null)
            return _fallbackTerrainMaterial;
        if (layers != null)
            return _detail.CreateMaterial(ImageTexture.CreateFromImage(cover), layers,
                new Vector2(OriginCellX * CellSize, OriginCellY * CellSize));
        return new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(cover),
            Roughness = 0.95f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
    }

    /// <summary>Any thread. The terrain mesh node of a cell, not yet in the tree.</summary>
    private static MeshInstance3D CreateTerrain(TerrainArrays terrain, Material material, string name)
    {
        RenderBudget.Acquire();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = terrain.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = terrain.Normals;
        arrays[(int)Mesh.ArrayType.Color] = terrain.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = terrain.UVs;
        arrays[(int)Mesh.ArrayType.Index] = terrain.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return new MeshInstance3D { Name = name, Mesh = mesh };
    }

    /// <summary>Builds ocean geometry from explicit Ocean contours, or the inferred coral-seabed raster where contours are absent.</summary>
    private MeshInstance3D? BuildOceanSurface((int X, int Y) cell, Vector2 origin, WaterTriangle[] explicitOcean, OceanMaskCell inferredMask)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        if (explicitOcean.Length > 0)
        {
            foreach (var triangle in explicitOcean)
            {
                if (!triangle.Ocean)
                    continue;
                var points = new[] { triangle.A, triangle.B, triangle.C };
                foreach (var point in points)
                {
                    vertices.Add(CryAxes.Point(point.X - (cell.X * CellSize + CellSize * 0.5f),
                        point.Y - (cell.Y * CellSize + CellSize * 0.5f), OceanLevel));
                    normals.Add(Vector3.Up);
                    uvs.Add(new Vector2(point.X, point.Y) * 0.02f);
                }
                var first = vertices.Count - 3;
                // CellGeometry triangulates in counter-clockwise Cry coordinates;
                // reverse to the Godot front-face winding used by terrain meshes.
                indices.Add(first);
                indices.Add(first + 2);
                indices.Add(first + 1);
            }
        }
        else if (inferredMask.HasTiles)
        {
            var side = OceanMaskCell.TilesPerSide + 1;
            for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                var localX = x * OceanMaskCell.StepMeters - CellSize * 0.5f;
                var localY = y * OceanMaskCell.StepMeters - CellSize * 0.5f;
                vertices.Add(CryAxes.Point(localX, localY, OceanLevel));
                normals.Add(Vector3.Up);
                uvs.Add(new Vector2(localX, -localY) * 0.02f);
            }
            foreach (var (x, y) in inferredMask.WetTiles())
            {
                var a = y * side + x;
                var b = a + 1;
                var d = (y + 1) * side + x;
                var c = d + 1;
                indices.Add(a); indices.Add(c); indices.Add(b);
                indices.Add(a); indices.Add(d); indices.Add(c);
            }
        }
        if (indices.Count == 0)
            return null;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, WaterMaterialFactory.Get(WaterMaterialFactory.DefaultOceanColor,
            new WaterRenderSettings(EnhancedWater, WaterWaves, WaterReflections, WaterDepthEffects, OceanVertexWaves),
            _oceanWaterProfile));
        return new MeshInstance3D
        {
            Name = "OceanMask",
            Mesh = mesh,
            Position = new Vector3(origin.X + CellSize * 0.5f, 0f, -origin.Y - CellSize * 0.5f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }
}

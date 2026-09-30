using System.Collections.Concurrent;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Surface-driven vegetation: the vegetation.xml groups that surface.xml attaches to terrain surface types
/// (&lt;VegetationGroup Id=".."/&gt;). No cell file stores these instances; CryEngine scatters them at run time around the
/// camera (procedural vegetation, CTerrainNode::GenerateProcObjects) and so does this class, per 64 m terrain sector:
/// <list type="bullet">
/// <item>a grid with the group's fDensity as spacing in metres, each point jittered by up to half a cell, kept where the
/// 2 m surface sample under it is the surface that owns the group and the terrain passes the group's elevation and
/// slope limits;</item>
/// <item>scale fSize + (r - 0.5) * fSizeVar, a random turn about the vertical when bRandomRotation, tilted onto the
/// terrain when bAlignToTerrain; drawn with the group's material override (matName), brightness and shadow flag;</item>
/// <item>only within <see cref="MaxViewDistance"/> of the focus; each instance fades out at its own view distance,
/// radius x scale x fMaxViewDistRatio x <see cref="ViewDistRatioVegetation"/> (see the vegetation shader).</item>
/// </list>
/// The random numbers are seeded by the sector and the group, so a sector always regrows the same plants.
/// Sectors are built on one worker thread; nodes are added and freed through <c>post</c> (main thread).
/// </summary>
internal sealed class ProceduralVegetation
{
    /// <summary>CryEngine terrain sector: 32 heightmap units of 2 m.</summary>
    public const float SectorSize = 64f;

    /// <summary>e_ProcVegetationMaxViewDistance: procedural objects exist only this close to the camera.</summary>
    public const float MaxViewDistance = 128f;

    /// <summary>
    /// e_view_dist_ratio_vegetation of the client's top graphics level (game/config/cvargroups/
    /// option_view_dist_ratio_vegetation.cfg): an instance is drawn out to radius x scale x fMaxViewDistRatio x this.
    /// </summary>
    public const float ViewDistRatioVegetation = 60f;

    /// <summary>
    /// The ratio the editor used for the view distances stored in painted vegetation records: cell 15,14 stores
    /// 66.26 x scale for waterweed_b (radius 2.04, fMaxViewDistRatio 0.8: 40.6) and 60.5 x scale for
    /// small_leaves_bunch_big_c (radius 2.76, 0.5: 43.8).
    /// </summary>
    public const float StoredViewDistRatio = 42f;

    /// <summary>Stored painted-vegetation view distance to the client's run-time one.</summary>
    public const float StoredViewDistanceScale = ViewDistRatioVegetation / StoredViewDistRatio;

    /// <summary>e_proc_vegetation_min_density of the top graphics level: the closest grid spacing, metres.</summary>
    private const float MinSpacing = 0.5f;

    /// <summary>e_ViewDistMin: no object disappears closer than this.</summary>
    private const float ViewDistMin = 10f;

    private const int SectorsPerCell = WorldStreamer.CellSize / (int)SectorSize;

    private readonly ModelLibrary _models;
    private readonly Action<Action> _post;
    private readonly Node3D _root;
    private readonly CellVegetationTable _table;
    private readonly CellSurfaceTypes _surfaces;
    private readonly ConcurrentDictionary<(int X, int Y), float[,]> _heights;
    private readonly ConcurrentDictionary<(int X, int Y), int[,]> _surfaceIds;
    private readonly Func<(int X, int Y), byte[]> _readCover;
    private readonly ConcurrentDictionary<(int X, int Y), (byte[] Rgba, int Size)> _terrainColors = new();
    private readonly Func<float, float, float, Vector3> _toGodot;
    private readonly float _distanceScale;
    private readonly bool _shadows;
    private readonly Dictionary<(int X, int Y), Sector> _sectors = [];
    private readonly List<((int X, int Y) Key, Sector Sector)> _queue = [];
    private readonly Lock _queueGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly HashSet<int> _loggedGroups = [];
    private Thread _worker;
    private volatile bool _stopped;
    private float _focusX, _focusY;
    private Node3D _container;

    private sealed class Sector
    {
        public volatile bool Cancelled;
        public Node3D Container;
    }

    public ProceduralVegetation(ModelLibrary models, Action<Action> post, Node3D root, CellVegetationTable table,
        CellSurfaceTypes surfaces, ConcurrentDictionary<(int X, int Y), float[,]> heights,
        ConcurrentDictionary<(int X, int Y), int[,]> surfaceIds, Func<(int X, int Y), byte[]> readCover,
        Func<float, float, float, Vector3> toGodot, float distanceScale, bool shadows)
    {
        _models = models;
        _post = post;
        _root = root;
        _table = table;
        _surfaces = surfaces;
        _heights = heights;
        _surfaceIds = surfaceIds;
        _readCover = readCover;
        _toGodot = toGodot;
        _distanceScale = Math.Clamp(distanceScale, 0.25f, 2f);
        _shadows = shadows;
    }

    /// <summary>Main thread. Grows the sectors within reach of a Cry position and drops the ones left behind.</summary>
    public void Update(float cryX, float cryY)
    {
        if (_stopped || _table == null || _surfaces == null)
            return;
        if (_container == null)
        {
            _container = new Node3D { Name = "SurfaceVegetation" };
            _root.AddChild(_container);
            _worker = new Thread(Work) { IsBackground = true, Name = "SurfaceVegetation" };
            _worker.Start();
        }
        var reach = MaxViewDistance * _distanceScale;
        var keep = reach + SectorSize;
        lock (_queueGate)
        {
            _focusX = cryX;
            _focusY = cryY;
        }
        var minX = (int)MathF.Floor((cryX - reach) / SectorSize);
        var maxX = (int)MathF.Floor((cryX + reach) / SectorSize);
        var minY = (int)MathF.Floor((cryY - reach) / SectorSize);
        var maxY = (int)MathF.Floor((cryY + reach) / SectorSize);
        for (var sx = minX; sx <= maxX; sx++)
        for (var sy = minY; sy <= maxY; sy++)
        {
            if (sx < 0 || sy < 0 || _sectors.ContainsKey((sx, sy)) || NearestDistance(sx, sy, cryX, cryY) > reach)
                continue;
            var cell = (sx / SectorsPerCell, sy / SectorsPerCell);
            if (!_heights.ContainsKey(cell) || !_surfaceIds.ContainsKey(cell))
                continue; // the cell isn't loaded yet; try again on a later update
            var sector = new Sector();
            _sectors[(sx, sy)] = sector;
            lock (_queueGate)
                _queue.Add(((sx, sy), sector));
            _wake.Set();
        }
        List<(int, int)> dropped = null;
        foreach (var (key, sector) in _sectors)
            if (NearestDistance(key.X, key.Y, cryX, cryY) > keep)
                (dropped ??= []).Add(key);
        if (dropped == null)
            return;
        foreach (var key in dropped)
        {
            var sector = _sectors[key];
            _sectors.Remove(key);
            sector.Cancelled = true;
            if (sector.Container != null && GodotObject.IsInstanceValid(sector.Container))
                sector.Container.QueueFree();
        }
    }

    /// <summary>Stops the worker (it finishes the sector it is building).</summary>
    public void Stop()
    {
        _stopped = true;
        _wake.Set();
    }

    /// <summary>Waits up to <paramref name="limit"/> for the worker to finish after <see cref="Stop"/>.</summary>
    public void Join(TimeSpan limit) => _worker?.Join(limit);

    private static float NearestDistance(int sx, int sy, float x, float y)
    {
        var dx = Math.Max(0f, Math.Max(sx * SectorSize - x, x - (sx + 1) * SectorSize));
        var dy = Math.Max(0f, Math.Max(sy * SectorSize - y, y - (sy + 1) * SectorSize));
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void Work()
    {
        while (!_stopped)
        {
            ((int X, int Y) Key, Sector Sector)? job = null;
            lock (_queueGate)
            {
                _queue.RemoveAll(j => j.Sector.Cancelled);
                if (_queue.Count > 0)
                {
                    // Nearest sector first.
                    var best = 0;
                    var bestDistance = float.MaxValue;
                    for (var i = 0; i < _queue.Count; i++)
                    {
                        var d = NearestDistance(_queue[i].Key.X, _queue[i].Key.Y, _focusX, _focusY);
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            best = i;
                        }
                    }
                    job = _queue[best];
                    _queue.RemoveAt(best);
                }
            }
            if (job == null)
            {
                _wake.WaitOne(250);
                continue;
            }
            try
            {
                Build(job.Value.Key.X, job.Value.Key.Y, job.Value.Sector);
            }
            catch (ObjectDisposedException)
            {
                return; // quitting
            }
            catch (Exception e)
            {
                GD.PrintErr($"Surface vegetation sector {job.Value.Key.X},{job.Value.Key.Y}: {e}");
            }
        }
    }

    private sealed class Batch
    {
        public MeshRef Mesh;
        public bool CastShadow;
        public float ViewDistance;
        public readonly List<Transform3D> Transforms = [];
        public readonly List<Color> Colors = [];
        public readonly List<Color> CustomData = [];
    }

    /// <summary>Worker thread. Scatters every surface group over one sector and queues its nodes.</summary>
    private void Build(int sx, int sy, Sector sector)
    {
        if (sector.Cancelled)
            return;
        var cell = (X: sx / SectorsPerCell, Y: sy / SectorsPerCell);
        if (!_heights.TryGetValue(cell, out var heights) || !_surfaceIds.TryGetValue(cell, out var ids))
            return;
        // Sector bounds in cell metres and heightmap units.
        var x0 = (sx - cell.X * SectorsPerCell) * SectorSize;
        var y0 = (sy - cell.Y * SectorsPerCell) * SectorSize;
        var u0 = (int)(x0 / TerrainCellReader.UnitMeters);
        var v0 = (int)(y0 / TerrainCellReader.UnitMeters);
        var units = (int)(SectorSize / TerrainCellReader.UnitMeters);
        var present = new HashSet<int>();
        for (var u = u0; u < u0 + units; u++)
        for (var v = v0; v < v0 + units; v++)
            if (ids[u, v] >= 0)
                present.Add(ids[u, v]);

        var batches = new Dictionary<string, Batch>();
        foreach (var surfaceId in present)
        {
            if (!_surfaces.ById.TryGetValue(surfaceId, out var surface) || surface.VegetationGroupIds.Count == 0)
                continue;
            foreach (var groupId in surface.VegetationGroupIds)
            {
                if (!_table.Groups.TryGetValue(groupId, out var group) || group.ModelPath == null || group.Size <= 0f)
                    continue;
                var mesh = _models.Request(group.ModelPath, CellPaths.MaterialFile(group.MaterialPath), false, group.ReceiveShadows);
                if (mesh == null)
                    continue;
                Scatter(group, surfaceId, mesh, sx, sy, cell, x0, y0, heights, ids, batches);
            }
        }
        if (sector.Cancelled)
            return;

        var container = new Node3D { Name = $"SurfaceVegetation_{sx}_{sy}" };
        var total = 0;
        foreach (var (key, batch) in batches)
        {
            if (batch.Transforms.Count == 0)
                continue;
            // The node range only has to reach the farthest instance; each instance fades at its own distance.
            var node = _models.CreateInstances(batch.Mesh, batch.Transforms, batch.ViewDistance + SectorSize * 0.75f,
                $"SurfaceVegetation_{sx}_{sy}", batch.CastShadow, _models.RenderParity ? batch.Colors : null,
                _models.RenderParity ? batch.CustomData : null);
            if (node == null)
                continue;
            container.AddChild(node);
            total += batch.Transforms.Count;
        }
        var parent = _container;
        _post(() =>
        {
            if (sector.Cancelled || !GodotObject.IsInstanceValid(parent))
            {
                container.QueueFree();
                return;
            }
            parent.AddChild(container);
            sector.Container = container;
        });
    }

    private void Scatter(CellVegetationGroup group, int surfaceId, MeshRef mesh, int sx, int sy, (int X, int Y) cell,
        float x0, float y0, float[,] heights, int[,] ids, Dictionary<string, Batch> batches)
    {
        var spacing = Math.Max(group.Density, MinSpacing);
        var random = new SectorRandom(sx, sy, group.Id, surfaceId);
        var cellOriginX = cell.X * (float)WorldStreamer.CellSize;
        var cellOriginY = cell.Y * (float)WorldStreamer.CellSize;
        var slopeLimited = group.SlopeMax < 255f || group.SlopeMin > 0f;
        var castShadow = _shadows && group.CastShadow;
        var key = $"{mesh.Key}|{castShadow}";
        if (!batches.TryGetValue(key, out var batch))
            batches[key] = batch = new Batch { Mesh = mesh, CastShadow = castShadow };
        var reach = MaxViewDistance * _distanceScale;
        var brightness = Mathf.Clamp(group.Brightness, 0f, 4f);
        (byte[] Rgba, int Size) terrainColors = default;
        if (_models.RenderParity && group.UseTerrainColor)
            terrainColors = _terrainColors.GetOrAdd(cell, c =>
            {
                var ctc = _readCover(c);
                if (ctc == null)
                    return default;
                var rgba = TerrainTextureReader.ReadCellRgba(ctc, 0, out var size);
                return rgba == null ? default : (rgba, size);
            });

        for (var gx = x0; gx < x0 + SectorSize; gx += spacing)
        for (var gy = y0; gy < y0 + SectorSize; gy += spacing)
        {
            // Draw every random number of a grid point first, so a rejected point doesn't shift the others.
            var jx = (random.Next() - 0.5f) * spacing;
            var jy = (random.Next() - 0.5f) * spacing;
            var sizeRandom = random.Next();
            var angleRandom = random.Next();
            var x = gx + jx;
            var y = gy + jy;
            if (x < x0 || y < y0 || x >= x0 + SectorSize || y >= y0 + SectorSize)
                continue;
            var u = Math.Clamp((int)(x / TerrainCellReader.UnitMeters), 0, TerrainCellReader.Samples - 1);
            var v = Math.Clamp((int)(y / TerrainCellReader.UnitMeters), 0, TerrainCellReader.Samples - 1);
            if (ids[u, v] != surfaceId)
                continue;
            var z = HeightAt(heights, x, y);
            if (z < group.ElevationMin || z > group.ElevationMax)
                continue;
            var normal = NormalAt(heights, x, y);
            if (slopeLimited)
            {
                var slope = MathF.Acos(Math.Clamp(normal.Z, -1f, 1f)) * 180f / MathF.PI;
                if (slope < group.SlopeMin || slope > group.SlopeMax)
                    continue;
            }
            var scale = group.Size + (sizeRandom - 0.5f) * group.SizeVar;
            if (scale <= 0.01f)
                continue;
            var viewDistance = Math.Min(Math.Max(mesh.Radius * scale * group.MaxViewDistRatio * ViewDistRatioVegetation,
                ViewDistMin) * _distanceScale, reach);
            var basis = Basis.FromScale(new Vector3(scale, scale, scale));
            if (group.RandomRotation)
                basis = new Basis(Vector3.Up, angleRandom * Mathf.Tau) * basis;
            if (group.AlignToTerrain)
            {
                var up = CryAxes.Point(normal);
                if (up.Dot(Vector3.Up) < 0.9999f)
                    basis = new Basis(new Quaternion(Vector3.Up, up.Normalized())) * basis;
            }
            var origin = _toGodot(cellOriginX + x, cellOriginY + y, z);
            batch.Transforms.Add(new Transform3D(basis, origin));
            batch.Colors.Add(new Color(brightness, brightness, brightness));
            var terrain = Colors.White;
            var flag = 0f;
            if (terrainColors.Rgba != null)
            {
                terrain = CellSceneBuilder.SampleTerrainColor(new System.Numerics.Vector3(x, y, z), terrainColors.Rgba,
                    terrainColors.Size);
                flag = CellSceneBuilder.TerrainColorFlag;
            }
            batch.CustomData.Add(terrain with { A = CellSceneBuilder.PackViewDistance(viewDistance) + flag });
            batch.ViewDistance = Math.Max(batch.ViewDistance, viewDistance);
        }
        lock (_loggedGroups)
            if (_loggedGroups.Add(group.Id))
                    GD.Print($"Surface vegetation: group {group.Id} {group.Name} on surface {surfaceId}: spacing {spacing} m, " +
                             $"model {group.ModelPath} radius {mesh.Radius:F2}, material {group.MaterialPath ?? "(model's own)"}, " +
                             $"view {mesh.Radius * group.Size * group.MaxViewDistRatio * ViewDistRatioVegetation:F1} m at size {group.Size}");
    }

    private static float HeightAt(float[,] heights, float x, float y)
    {
        var fx = Math.Clamp(x / TerrainCellReader.UnitMeters, 0f, TerrainCellReader.Samples - 1.001f);
        var fy = Math.Clamp(y / TerrainCellReader.UnitMeters, 0f, TerrainCellReader.Samples - 1.001f);
        int ix = (int)fx, iy = (int)fy;
        float tx = fx - ix, ty = fy - iy;
        var h0 = heights[ix, iy] + (heights[ix + 1, iy] - heights[ix, iy]) * tx;
        var h1 = heights[ix, iy + 1] + (heights[ix + 1, iy + 1] - heights[ix, iy + 1]) * tx;
        return h0 + (h1 - h0) * ty;
    }

    /// <summary>Terrain normal in Cry axes (Z up) from the height differences 2 m either side.</summary>
    private static System.Numerics.Vector3 NormalAt(float[,] heights, float x, float y)
    {
        const float d = TerrainCellReader.UnitMeters;
        var dx = HeightAt(heights, x + d, y) - HeightAt(heights, x - d, y);
        var dy = HeightAt(heights, x, y + d) - HeightAt(heights, x, y - d);
        return System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(-dx, -dy, 2f * d));
    }

    /// <summary>Deterministic xorshift generator seeded by the sector, group and surface (same plants every time).</summary>
    private struct SectorRandom(int sx, int sy, int group, int surface)
    {
        private uint _state = Seed(sx, sy, group, surface);

        private static uint Seed(int sx, int sy, int group, int surface)
        {
            var h = 2166136261u;
            foreach (var v in new[] { sx, sy, group, surface })
            {
                h ^= (uint)v;
                h *= 16777619u;
                h ^= h >> 15;
            }
            return h == 0 ? 0x9E3779B9u : h;
        }

        public float Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (_state >> 8) * (1f / 16777216f);
        }
    }
}

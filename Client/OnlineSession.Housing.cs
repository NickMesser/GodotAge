#nullable enable

using AAEmu.GodotViewer.Net;
using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Client;

public partial class OnlineSession
{
    private HousingModelResolver? _housingModels;
    private readonly Dictionary<ushort, uint> _housingUnitIdsByTimeline = [];
    private readonly Dictionary<uint, ushort> _housingTimelinesByUnit = [];
    private readonly Dictionary<uint, HouseStateEvent> _pendingHousingStatesByUnit = [];
    private HousingPlacementCatalog? _housingPlacementCatalog;
    private HousingPlacementRequest? _activeHousingPlacement;
    private Node3D? _housingPlacementGhost;
    private bool _lastPlacementAllowed;
    private string _lastPlacementReason = "not_started";
    private NVector3 _lastPlacementPosition;
    private float _housingPlacementYaw;
    private int _housingPlacementGeneration;
    private HousingPlacementFeedback? _lastPlacementFeedback;

    /// <summary>Rotation increments for the original builder wheel actions, in degrees.</summary>
    /// <remarks>The defaults are provisional until a real housing input capture verifies the angle values.</remarks>
    public float BuilderRotationNormalDegrees { get; set; } = 15f;
    public float BuilderRotationSmallDegrees { get; set; } = 1f;
    public float BuilderRotationLargeDegrees { get; set; } = 45f;

    public event Action<HouseStateEvent>? HousingStateReceived;
    public event Action<HouseBuildProgressEvent>? HousingBuildProgressChanged;
    public event Action<HousePermissionChangedEvent>? HousingPermissionChanged;
    public event Action<HouseDemolishedEvent>? HousingDemolished;
    public event Action<HouseDataEvent>? OwnedHousesReceived;
    public event Action<HouseRemovedEvent>? HousingRemoved;
    public event Action<HouseFarmSummaryEvent>? HousingFarmSummaryReceived;
    public event Action<HouseTaxInfoEvent>? HousingTaxInfoReceived;
    public event Action<HousingPlacementFeedback>? HousingPlacementFeedbackChanged;
    public event Action<HousingPlacementRequest>? HousingPlacementConfirmed;
    public event Action? HousingPlacementCancelled;

    private void InitializeHousing()
    {
        if (File.Exists(GameDatabasePath))
        {
            _housingModels = new HousingModelResolver(GameDatabasePath);
            _housingPlacementCatalog = new HousingPlacementCatalog(GameDatabasePath);
        }
    }

    /// <summary>
    /// Starts the local design-item preview. Call this from the inventory/UI when the selected item is a housing design;
    /// the passed instance id is kept for the eventual server request.
    /// </summary>
    public bool BeginHousingPlacement(uint itemTemplateId, ulong itemId)
    {
        if (_housingPlacementCatalog is null || _housingModels is null || itemId == 0)
            return false;
        var design = _housingPlacementCatalog.ResolveDesign(itemTemplateId);
        if (design is null || design.MainModelId == 0)
            return false;

        CancelHousingPlacement(notify: false);
        _housingPlacementYaw = 0f;
        _activeHousingPlacement = new HousingPlacementRequest(itemTemplateId, design, itemId,
            System.Numerics.Vector3.Zero, _housingPlacementYaw);
        _lastPlacementAllowed = false;
        _lastPlacementReason = "waiting_for_terrain";
        _lastPlacementFeedback = null;
        var generation = ++_housingPlacementGeneration;
        _housingPlacementGhost = new Node3D { Name = "HousingPlacementPreview" };
        AddChild(_housingPlacementGhost);
        QueueHousingPlacementModel(generation, design.MainModelId);
        return true;
    }

    /// <summary>Ends the local builder preview without sending a game packet.</summary>
    public void CancelHousingPlacement() => CancelHousingPlacement(notify: true);

    /// <summary>
    /// Requests confirmation through the C# event surface when the local zone/category hint passes.
    /// The packet sender remains intentionally unavailable until the native CSCreateHouse layout is captured.
    /// </summary>
    public bool TryConfirmHousingPlacement()
    {
        if (_activeHousingPlacement is not { } placement || !_lastPlacementAllowed)
            return false;
        HousingPlacementConfirmed?.Invoke(placement with
        {
            Position = _lastPlacementPosition,
            Yaw = _housingPlacementYaw,
        });
        CancelHousingPlacement(notify: false);
        return true;
    }

    private bool HandleHousingUnhandledInput(InputEvent inputEvent)
    {
        if (_activeHousingPlacement is null)
            return false;

        if (inputEvent.IsActionPressed("builder_rotate_left_normal", exactMatch: true))
        {
            RotateHousing(-BuilderRotationNormalDegrees);
            return true;
        }
        if (inputEvent.IsActionPressed("builder_rotate_right_normal", exactMatch: true))
        {
            RotateHousing(BuilderRotationNormalDegrees);
            return true;
        }
        if (inputEvent.IsActionPressed("builder_rotate_left_small", exactMatch: true))
        {
            RotateHousing(-BuilderRotationSmallDegrees);
            return true;
        }
        if (inputEvent.IsActionPressed("builder_rotate_right_small", exactMatch: true))
        {
            RotateHousing(BuilderRotationSmallDegrees);
            return true;
        }
        if (inputEvent.IsActionPressed("builder_rotate_left_large", exactMatch: true))
        {
            RotateHousing(-BuilderRotationLargeDegrees);
            return true;
        }
        if (inputEvent.IsActionPressed("builder_rotate_right_large", exactMatch: true))
        {
            RotateHousing(BuilderRotationLargeDegrees);
            return true;
        }

        if (inputEvent is not InputEventMouseButton { Pressed: true } mouse)
            return false;
        if (mouse.ButtonIndex == MouseButton.Left)
        {
            TryConfirmHousingPlacement();
            return true;
        }
        if (mouse.ButtonIndex == MouseButton.Right)
        {
            CancelHousingPlacement();
            return true;
        }
        return false;
    }

    private void UpdateHousingPlacement()
    {
        if (_activeHousingPlacement is null)
            return;

        if (!TryGetHousingGroundPoint(out var position, out var zoneKey))
        {
            PublishPlacementFeedback(false, "terrain_unavailable", _lastPlacementPosition, -1);
            return;
        }

        _lastPlacementPosition = position;
        var design = _activeHousingPlacement.Design;
        var areaAllowed = _housingPlacementCatalog?.IsCategoryAllowedInZone(zoneKey, design.CategoryId) == true;
        var canPlace = areaAllowed;
        var reason = areaAllowed ? "area_allowed" : "housing_area_not_allowed";
        if (_housingPlacementGhost != null)
        {
            _housingPlacementGhost.Position = World.ToGodot(position.X, position.Y, position.Z);
            _housingPlacementGhost.Rotation = new Vector3(0, _housingPlacementYaw, 0);
        }
        PublishPlacementFeedback(canPlace, reason, position, zoneKey);
    }

    private bool TryGetHousingGroundPoint(out NVector3 point, out int zoneKey)
    {
        point = default;
        zoneKey = -1;
        var viewport = GetViewport();
        var camera = viewport?.GetCamera3D();
        if (World is null || camera is null)
            return false;

        var cursor = viewport.GetMousePosition();
        var origin = World.ToCry(camera.ProjectRayOrigin(cursor));
        var ray = camera.ProjectRayNormal(cursor);
        var direction = new NVector3(ray.X, -ray.Z, ray.Y);
        if (direction.Z >= -0.01f)
            return false;

        var initialCell = World.HasHeightsAt(origin.X, origin.Y);
        var startingGround = initialCell ? World.TerrainHeightAt(origin.X, origin.Y) : World.OceanLevel;
        var distance = (origin.Z - startingGround) / -direction.Z;
        if (distance < 0 || distance > 12000)
            return false;

        for (var i = 0; i < 8; i++)
        {
            var sample = origin + direction * distance;
            if (!World.HasHeightsAt(sample.X, sample.Y))
                return false;
            var heightDelta = sample.Z - World.TerrainHeightAt(sample.X, sample.Y);
            if (MathF.Abs(heightDelta) <= 0.08f)
            {
                point = new NVector3(sample.X, sample.Y, sample.Z - heightDelta);
                zoneKey = World.ZoneAt(sample.X, sample.Y);
                return zoneKey >= 0;
            }
            distance += heightDelta / -direction.Z;
            if (distance < 0 || distance > 12000)
                return false;
        }
        return false;
    }

    private void QueueHousingPlacementModel(int generation, uint modelId)
    {
        if (_housingModels is null || Models is null)
            return;
        _doodadWork.Add(() =>
        {
            var parts = _housingModels.Resolve(modelId);
            var meshes = new List<(MeshRef Mesh, Transform3D Transform)>(parts.Count);
            foreach (var part in parts)
            {
                var transform = CryAxes.FromRowVector(part.Transform, NVector3.Zero);
                if (Models.Request(part.ModelPath, part.MaterialPath, transform.Basis.Determinant() < 0) is { } mesh)
                    meshes.Add((mesh, transform));
            }

            Post(() =>
            {
                if (generation != _housingPlacementGeneration || _housingPlacementGhost is not { } ghost)
                    return;
                var material = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.2f, 0.95f, 0.3f, 0.45f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                };
                foreach (var (mesh, transform) in meshes)
                    if (Models.GetMesh(mesh) is { } godotMesh)
                        ghost.AddChild(new MeshInstance3D
                        {
                            Mesh = godotMesh,
                            Transform = transform,
                            MaterialOverride = material,
                            Layers = ModelLibrary.ObjectLayer,
                        });
            });
        });
    }

    private void RotateHousing(float degrees)
    {
        _housingPlacementYaw += Mathf.DegToRad(degrees);
        if (_housingPlacementGhost != null)
            _housingPlacementGhost.Rotation = new Vector3(0, _housingPlacementYaw, 0);
    }

    private void PublishPlacementFeedback(bool allowed, string reason, NVector3 position, int zoneKey)
    {
        _lastPlacementAllowed = allowed;
        _lastPlacementReason = reason;
        if (_housingPlacementGhost != null)
        {
            var color = allowed ? new Color(0.2f, 0.95f, 0.3f, 0.45f) : new Color(0.95f, 0.15f, 0.15f, 0.45f);
            foreach (var mesh in _housingPlacementGhost.GetChildren().OfType<MeshInstance3D>())
                if (mesh.MaterialOverride is StandardMaterial3D material)
                    material.AlbedoColor = color;
        }

        var feedback = new HousingPlacementFeedback(position, _housingPlacementYaw, allowed, reason, zoneKey);
        if (_lastPlacementFeedback is not { } previous || previous.CanPlace != feedback.CanPlace ||
            previous.Reason != feedback.Reason || previous.ZoneKey != feedback.ZoneKey ||
            NVector3.Distance(previous.Position, feedback.Position) > 0.1f || MathF.Abs(previous.Yaw - feedback.Yaw) > 0.001f)
        {
            _lastPlacementFeedback = feedback;
            HousingPlacementFeedbackChanged?.Invoke(feedback);
        }
    }

    private void CancelHousingPlacement(bool notify)
    {
        _housingPlacementGeneration++;
        _activeHousingPlacement = null;
        _housingPlacementGhost?.QueueFree();
        _housingPlacementGhost = null;
        _lastPlacementAllowed = false;
        _lastPlacementReason = "not_started";
        _lastPlacementFeedback = null;
        if (notify)
            HousingPlacementCancelled?.Invoke();
    }

    private static bool ActionPressedThisFrame(string action) =>
        InputMap.HasAction(action) && Input.IsActionJustPressed(action);

    /// <summary>Reduces housing packets on the main thread and publishes their C# UI events.</summary>
    private void ApplyHousingEvent(GameEvent value)
    {
        switch (value)
        {
            case HouseStateEvent state:
                _housingUnitIdsByTimeline[state.TimelineId] = state.ObjectId;
                _housingTimelinesByUnit[state.ObjectId] = state.TimelineId;
                if (_units.TryGetValue(state.ObjectId, out var house))
                {
                    house.HousingTimelineId = state.TimelineId;
                    QueueHousing(house, state.ModelId);
                }
                else
                    _pendingHousingStatesByUnit[state.ObjectId] = state;
                HousingStateReceived?.Invoke(state);
                break;
            case HouseBuildProgressEvent progress:
                if (_housingUnitIdsByTimeline.TryGetValue(progress.TimelineId, out var unitId) &&
                    _units.TryGetValue(unitId, out var building))
                    QueueHousing(building, progress.ModelId);
                HousingBuildProgressChanged?.Invoke(progress);
                break;
            case HousePermissionChangedEvent permission:
                HousingPermissionChanged?.Invoke(permission);
                break;
            case HouseDemolishedEvent demolished:
                HousingDemolished?.Invoke(demolished);
                break;
            case HouseDataEvent houses:
                OwnedHousesReceived?.Invoke(houses);
                break;
            case HouseRemovedEvent removed:
                HousingRemoved?.Invoke(removed);
                break;
            case HouseFarmSummaryEvent farm:
                HousingFarmSummaryReceived?.Invoke(farm);
                break;
            case HouseTaxInfoEvent tax:
                HousingTaxInfoReceived?.Invoke(tax);
                break;
            case UnitsRemovedEvent removedUnits:
                foreach (var removedUnitId in removedUnits.UnitIds)
                {
                    if (_housingTimelinesByUnit.Remove(removedUnitId, out var timelineId))
                        _housingUnitIdsByTimeline.Remove(timelineId);
                    _pendingHousingStatesByUnit.Remove(removedUnitId);
                }
                break;
        }
    }

    private void QueueHousingFromSpawn(RemoteUnit unit)
    {
        var modelId = unit.Snapshot.ModelId;
        if (_pendingHousingStatesByUnit.Remove(unit.Snapshot.UnitId, out var pending))
        {
            unit.HousingTimelineId = pending.TimelineId;
            _housingUnitIdsByTimeline[pending.TimelineId] = unit.Snapshot.UnitId;
            _housingTimelinesByUnit[unit.Snapshot.UnitId] = pending.TimelineId;
            modelId = pending.ModelId;
        }
        QueueHousing(unit, modelId);
    }

    /// <summary>
    /// Resolves the house's currently selected model on the existing doodad worker and adds its prefab parts
    /// relative to the server-provided housing transform.
    /// </summary>
    private void QueueHousing(RemoteUnit unit, uint modelId)
    {
        unit.HousingModelId = modelId;
        if (_housingModels is null || Models is null || modelId == 0)
        {
            unit.HousingVisual?.QueueFree();
            unit.HousingVisual = null;
            return;
        }

        var snapshot = unit.Snapshot;
        _doodadWork.Add(() =>
        {
            var parts = _housingModels.Resolve(modelId);
            var meshes = new List<(MeshRef Mesh, Transform3D Transform)>(parts.Count);
            foreach (var part in parts)
            {
                var transform = CryAxes.FromRowVector(part.Transform, NVector3.Zero);
                if (Models.Request(part.ModelPath, part.MaterialPath, transform.Basis.Determinant() < 0) is { } mesh)
                    meshes.Add((mesh, transform));
            }

            Post(() =>
            {
                if (!_units.TryGetValue(snapshot.UnitId, out var current) || current != unit ||
                    current.HousingModelId != modelId || current.Node is null)
                    return;

                current.HousingVisual?.QueueFree();
                current.HousingVisual = null;
                if (meshes.Count == 0)
                    return;

                var visual = new Node3D
                {
                    Name = "HousingModel",
                    Scale = Vector3.One * (snapshot.Scale > 0 ? snapshot.Scale : 1f),
                };
                current.Node.AddChild(visual);
                foreach (var (mesh, transform) in meshes)
                    if (Models.GetMesh(mesh) is { } godotMesh)
                        visual.AddChild(new MeshInstance3D
                        {
                            Mesh = godotMesh,
                            Transform = transform,
                            Layers = ModelLibrary.ObjectLayer,
                        });
                current.HousingVisual = visual;
            });
        });
    }
}

public sealed record HousingPlacementRequest(
    uint ItemTemplateId,
    HousingDesignData Design,
    ulong ItemId,
    NVector3 Position,
    float Yaw);

public sealed record HousingPlacementFeedback(
    NVector3 Position,
    float Yaw,
    bool CanPlace,
    string Reason,
    int ZoneKey);

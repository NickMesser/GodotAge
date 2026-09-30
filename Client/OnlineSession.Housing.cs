#nullable enable

using AAEmu.GodotViewer.Client.Targeting;
using AAEmu.GodotViewer.Net;
using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Client;

/// <summary>The original client's builder steps as BUILDER_STEP reports them, plus the two waits in between.</summary>
public enum HousingBuilderStep
{
    None,
    /// <summary>The ghost follows the cursor (BUILDER_STEP "position").</summary>
    Position,
    /// <summary>The spot is fixed and the wheel turns the ghost (BUILDER_STEP "rotation", build guide shown).</summary>
    Rotation,
    /// <summary>CSConstructHouseTax sent (BUILDER_STEP "none"); waiting for SCConstructHouseTax.</summary>
    Quote,
    /// <summary>HOUSE_BUILD_INFO shown; waiting for NextStepFromBuildCheck / PrevStepFromBuildCheck.</summary>
    Confirm,
}

public partial class OnlineSession
{
    // Builder input (default_binding.g builder_rotate_mode): wheelup/down = builder_rotate_left/right_normal,
    // ALT+wheel = *_small, SHIFT+wheel = *_large, CTRL+wheel = builder_zoom_in/out.
    private const float QuoteTimeoutSeconds = 10f;
    private const string CursorPosition = "game/ui/cursor/housing_position.dds";
    private const string CursorRotation = "game/ui/cursor/housing_rotation.dds";
    private const string CursorInvalid = "game/ui/cursor/housing_invalid.dds";
    /// <summary>HOUSING_TAX_SEAL: this World charges construction and upkeep in tax certificates (Features.TaxItem).</summary>
    public const int HousingTaxSeal = 1;
    /// <summary>The housing manager closes the house window when the player is farther than this (x2game FUN_39728340).</summary>
    private const float HouseInteractionRange = 9f;
    /// <summary>Skills that open the house window when the server lists them first for a house (const_skill_types
    /// is_craft_info 11001, nil_housing_interaction 12106); nil_loot 12079 does nothing (x2game FUN_39886420).</summary>
    private const uint SkillConstructionInfo = 11001, SkillHousingInteraction = 12106, SkillNilLoot = 12079;
    /// <summary>is_demolish: X2House:Demolish casts it on the house (x2game FUN_39998130).</summary>
    private const uint SkillDemolish = 12945;

    private HousingModelResolver? _housingModels;
    private readonly Dictionary<ushort, uint> _housingUnitIdsByTimeline = [];
    private readonly Dictionary<uint, ushort> _housingTimelinesByUnit = [];
    private readonly Dictionary<uint, HouseStateEvent> _pendingHousingStatesByUnit = [];
    private readonly Dictionary<ushort, HouseStateEvent> _houseStates = [];
    private readonly Dictionary<ushort, HouseTaxInfoEvent> _houseTaxes = [];
    private readonly Dictionary<ushort, OwnedHouseRecord> _ownedHouses = [];
    private HousingPlacementCatalog? _housingPlacementCatalog;
    private HousingAreaCatalog? _housingAreas;
    private HousingPlacementRequest? _activeHousingPlacement;
    private Node3D? _housingPlacementGhost;
    private HousingBuilderInput? _housingBuilderInput;
    private bool _lastPlacementAllowed;
    private string _lastPlacementReason = "not_started";
    private NVector3 _lastPlacementPosition;
    private float _housingPlacementYaw;
    private int _housingPlacementGeneration;
    private HousingPlacementFeedback? _lastPlacementFeedback;
    private bool _placementPositionAnnounced;
    private double _quoteSentAt;
    private HouseConstructTaxEvent? _lastQuote;
    private ushort? _interactingHouse;

    /// <summary>The builder_rotate_angle cvar (default 5, degrees), used only by the normal pair.</summary>
    public float BuilderRotateAngle { get; set; } = 5f;

    public HousingBuilderStep BuilderStep { get; private set; }

    public event Action<HouseStateEvent>? HousingStateReceived;
    public event Action<HouseBuildProgressEvent>? HousingBuildProgressChanged;
    public event Action<HousePermissionChangedEvent>? HousingPermissionChanged;
    public event Action<HouseDemolishedEvent>? HousingDemolished;
    public event Action<HouseDataEvent>? OwnedHousesReceived;
    public event Action<HouseRemovedEvent>? HousingRemoved;
    public event Action<HouseFarmSummaryEvent>? HousingFarmSummaryReceived;
    public event Action<HouseTaxInfoEvent>? HousingTaxInfoReceived;
    public event Action<HouseRecoverToggledEvent>? HousingRecoverToggled;
    public event Action<HousingPlacementFeedback>? HousingPlacementFeedbackChanged;
    public event Action<HousingPlacementRequest>? HousingPlacementConfirmed;
    public event Action? HousingPlacementCancelled;
    /// <summary>BUILDER_STEP: "position", "rotation" or "none".</summary>
    public event Action<string>? HousingBuilderStepChanged;
    /// <summary>BUILDER_END.</summary>
    public event Action? HousingBuilderEnded;
    /// <summary>HOUSE_BUILD_INFO with the quote (the build dialog opens).</summary>
    public event Action<HouseConstructTaxEvent, HousingDesignData>? HousingBuildInfoReceived;
    /// <summary>HOUSE_BUILD_INFO without arguments: the build check was cancelled.</summary>
    public event Action? HousingBuildInfoCleared;
    /// <summary>HOUSE_INTERACTION_START for the house with this timeline id.</summary>
    public event Action<ushort>? HousingInteractionStarted;
    /// <summary>HOUSE_INTERACTION_END.</summary>
    public event Action? HousingInteractionEnded;

    public HousingPlacementCatalog? HousingCatalog => _housingPlacementCatalog;
    public IReadOnlyDictionary<ushort, HouseStateEvent> HouseStates => _houseStates;
    public IReadOnlyDictionary<ushort, OwnedHouseRecord> OwnedHouses => _ownedHouses;
    public HouseTaxInfoEvent? HouseTax(ushort timelineId) => _houseTaxes.GetValueOrDefault(timelineId);
    /// <summary>The house whose window is open (X2House's "current" house), or null.</summary>
    public ushort? InteractingHouse => _interactingHouse;
    public HouseStateEvent? InteractingHouseState =>
        _interactingHouse is { } tl ? _houseStates.GetValueOrDefault(tl) : null;
    /// <summary>The spot and yaw the builder currently holds (world metres, radians).</summary>
    public (NVector3 Position, float Yaw) BuilderPlacement => (_lastPlacementPosition, _housingPlacementYaw);
    public HousingPlacementRequest? ActiveHousingPlacement => _activeHousingPlacement;
    public HouseConstructTaxEvent? LastHousingQuote => _lastQuote;

    private void InitializeHousing()
    {
        if (File.Exists(GameDatabasePath))
        {
            _housingModels = new HousingModelResolver(GameDatabasePath);
            _housingPlacementCatalog = new HousingPlacementCatalog(GameDatabasePath);
        }
        _housingAreas = new HousingAreaCatalog();
        WorldHoverPresentation.CursorOverride = () => BuilderCursor;
    }

    private string? BuilderCursor => BuilderStep switch
    {
        HousingBuilderStep.Position => _lastPlacementAllowed ? CursorPosition : CursorInvalid,
        HousingBuilderStep.Rotation => CursorRotation,
        _ => null,
    };

    /// <summary>
    /// Enters the builder for a design item, as using it from the bag does in the original client (the item's own
    /// use skill is not cast). Returns false when the item is not a housing design.
    /// </summary>
    public bool BeginHousingPlacement(uint itemTemplateId, ulong itemId)
    {
        if (_housingPlacementCatalog is null || _housingModels is null || itemId == 0)
            return false;
        var design = _housingPlacementCatalog.ResolveDesign(itemTemplateId);
        if (design is null || design.MainModelId == 0)
            return false;

        if (BuilderStep != HousingBuilderStep.None)
            EndBuilder(notify: true);
        _housingPlacementYaw = 0f; // the captured CSCreateHouse carried yaw 0 when the wheel was never used
        _activeHousingPlacement = new HousingPlacementRequest(itemTemplateId, design, itemId,
            NVector3.Zero, _housingPlacementYaw);
        _lastPlacementAllowed = false;
        _lastPlacementReason = "waiting_for_terrain";
        _lastPlacementFeedback = null;
        _placementPositionAnnounced = false;
        _lastQuote = null;
        BuilderStep = HousingBuilderStep.Position;
        var generation = ++_housingPlacementGeneration;
        _housingPlacementGhost = new Node3D { Name = "HousingPlacementPreview" };
        AddChild(_housingPlacementGhost);
        if (_housingBuilderInput is null || !IsInstanceValid(_housingBuilderInput))
        {
            _housingBuilderInput = new HousingBuilderInput(this) { Name = "HousingBuilderInput" };
            GetTree().Root.CallDeferred(Node.MethodName.AddChild, _housingBuilderInput);
        }
        QueueHousingPlacementModel(generation, design.MainModelId);
        GD.Print($"[housing] builder started: item {itemTemplateId}/{itemId} design {design.DesignId} " +
                 $"model {design.MainModelId} radius {design.GardenRadius}");
        return true;
    }

    /// <summary>Ends the builder (BUILDER_END) without sending anything.</summary>
    public void CancelHousingPlacement() => EndBuilder(notify: true);

    /// <summary>The builder's left click: position -> rotation -> quote request.</summary>
    public bool AdvanceHousingBuilder()
    {
        switch (BuilderStep)
        {
            case HousingBuilderStep.Position when _lastPlacementAllowed:
                SetBuilderStep(HousingBuilderStep.Rotation, "rotation");
                return true;
            case HousingBuilderStep.Position:
                GD.Print($"[housing] placement refused locally: {_lastPlacementReason}");
                return true;
            case HousingBuilderStep.Rotation when _activeHousingPlacement is { } placement:
                SetBuilderStep(HousingBuilderStep.Quote, "none");
                _quoteSentAt = Time.GetTicksMsec() / 1000.0;
                Client.SendGame(HousingRequests.CSConstructHouseTax,
                    HousingRequests.ConstructHouseTax(placement.Design.DesignId, _lastPlacementPosition));
                GD.Print($"[housing] CSConstructHouseTax design {placement.Design.DesignId} at " +
                         $"{_lastPlacementPosition.X:F1},{_lastPlacementPosition.Y:F1},{_lastPlacementPosition.Z:F2} " +
                         $"yaw {Mathf.RadToDeg(_housingPlacementYaw):F1}");
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// X2House:NextStepFromBuildCheck (the build dialog's OK): sends CSCreateHouse once and ends the builder, as the
    /// original did (BUILDER_END right after the request; the server answers with the item task and house state).
    /// </summary>
    public bool NextStepFromBuildCheck()
    {
        if (BuilderStep != HousingBuilderStep.Confirm || _activeHousingPlacement is not { } placement)
            return false;
        // moneyAmount is the quote's total (0 in the capture, whose quote total was 0); ht is a client global that was
        // also 0 there.
        var money = _lastQuote?.TotalTax ?? 0;
        Client.SendGame(HousingRequests.CSCreateHouse, HousingRequests.CreateHouse(placement.Design.DesignId,
            _lastPlacementPosition, _housingPlacementYaw, placement.ItemId, money, 0));
        GD.Print($"[housing] CSCreateHouse design {placement.Design.DesignId} item {placement.ItemId} at " +
                 $"{_lastPlacementPosition.X:F1},{_lastPlacementPosition.Y:F1},{_lastPlacementPosition.Z:F2} " +
                 $"yaw {_housingPlacementYaw:F4} money {money}");
        HousingPlacementConfirmed?.Invoke(placement with { Position = _lastPlacementPosition, Yaw = _housingPlacementYaw });
        EndBuilder(notify: true);
        return true;
    }

    /// <summary>X2House:PrevStepFromBuildCheck (the build dialog's Cancel): back to the rotation step.</summary>
    public bool PrevStepFromBuildCheck()
    {
        if (BuilderStep is not (HousingBuilderStep.Confirm or HousingBuilderStep.Quote))
            return false;
        HousingBuildInfoCleared?.Invoke();
        SetBuilderStep(HousingBuilderStep.Rotation, "rotation");
        return true;
    }

    /// <summary>Kept for callers of the earlier preview API: the builder's left click.</summary>
    public bool TryConfirmHousingPlacement() => AdvanceHousingBuilder();

    internal bool HandleHousingUnhandledInput(InputEvent inputEvent)
    {
        if (BuilderStep == HousingBuilderStep.None)
            return false;

        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } &&
            BuilderStep is HousingBuilderStep.Position or HousingBuilderStep.Rotation)
        {
            EndBuilder(notify: true);
            return true;
        }

        if (BuilderStep == HousingBuilderStep.Rotation && inputEvent is InputEventMouseButton { Pressed: true } wheel &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            if (wheel.CtrlPressed)
                return false; // CTRL-wheel is builder_zoom: the camera keeps it
            RotateHousing(RotationDelta(wheel.ButtonIndex == MouseButton.WheelUp,
                wheel.AltPressed ? 0 : wheel.ShiftPressed ? 2 : 1));
            return true;
        }
        if (BuilderStep == HousingBuilderStep.Rotation)
        {
            foreach (var (action, left, size) in RotateActions)
                if (inputEvent.IsActionPressed(action, exactMatch: true))
                {
                    RotateHousing(RotationDelta(left, size));
                    return true;
                }
        }

        // Only the left button belongs to the builder: in the real client a right click during placement kept the
        // builder (it turned the camera / picked a target) and Escape ended it with BUILDER_END.
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mouse)
            return false;
        if (BuilderStep is HousingBuilderStep.Quote or HousingBuilderStep.Confirm)
            return true; // the dialog is up: world clicks do nothing
        if (!mouse.Pressed)
            return true; // swallow the release so targeting does not pick through the ghost
        AdvanceHousingBuilder(); // an invalid spot ignores the click, as the original did
        return true;
    }

    private static readonly (string Action, bool Left, int Size)[] RotateActions =
    [
        ("builder_rotate_left_normal", true, 1), ("builder_rotate_right_normal", false, 1),
        ("builder_rotate_left_small", true, 0), ("builder_rotate_right_small", false, 0),
        ("builder_rotate_left_large", true, 2), ("builder_rotate_right_large", false, 2),
    ];

    /// <summary>
    /// Yaw change per input, in radians, as x2game FUN_390cae70 computes it: normal = builder_rotate_angle * -pi/180
    /// for left (the opposite sign of the other pairs), small = +-0.01 rad, large = +-pi/12, left positive.
    /// </summary>
    private float RotationDelta(bool left, int size) => size switch
    {
        0 => left ? 0.01f : -0.01f,
        2 => left ? MathF.PI / 12f : -MathF.PI / 12f,
        _ => (left ? -1f : 1f) * BuilderRotateAngle * MathF.PI / 180f,
    };

    private void UpdateHousingPlacement()
    {
        UpdateHouseInteractionRange();
        if (_activeHousingPlacement is null)
            return;

        if (BuilderStep == HousingBuilderStep.Quote &&
            Time.GetTicksMsec() / 1000.0 - _quoteSentAt > QuoteTimeoutSeconds)
        {
            // The World answered with an error (or nothing): the original returns to the rotation step on refusal.
            GD.Print("[housing] no construction quote; back to rotation");
            SetBuilderStep(HousingBuilderStep.Rotation, "rotation");
        }
        if (BuilderStep != HousingBuilderStep.Position)
            return;

        if (!TryGetHousingGroundPoint(out var cursorPoint, out _))
        {
            PublishPlacementFeedback(false, "terrain_unavailable", _lastPlacementPosition, -1);
            return;
        }

        var design = _activeHousingPlacement.Design;
        var x = SnapToLattice(cursorPoint.X, design.ViewSize);
        var y = SnapToLattice(cursorPoint.Y, design.ViewSize);
        var (z, zReason) = PlotHeight(design, x, y);
        var position = new NVector3(x, y, z ?? cursorPoint.Z);
        var zoneKey = World.ZoneAt(x, y);
        _lastPlacementPosition = position;
        var (allowed, reason) = zReason is null
            ? EvaluatePlacement(design, position, zoneKey)
            : (false, zReason);
        if (_housingPlacementGhost != null)
        {
            _housingPlacementGhost.Position = World.ToGodot(position.X, position.Y, position.Z);
            _housingPlacementGhost.Rotation = new Vector3(0, _housingPlacementYaw, 0);
        }
        PublishPlacementFeedback(allowed, reason, position, zoneKey);
        if (!_placementPositionAnnounced)
        {
            _placementPositionAnnounced = true;
            HousingBuilderStepChanged?.Invoke("position");
        }
    }

    /// <summary>
    /// The builder's 4 m lattice (x2game FUN_39731130): with a footprint of <paramref name="viewSize"/> metres
    /// (housing_view_sizes.value, cells = size / 4), cell = (int)((v - half + 2) / 4) * 4 and v' = cell + half.
    /// The captured garden (8 m) landed on 15344/14632.
    /// </summary>
    private static float SnapToLattice(float value, int viewSize)
    {
        if (viewSize <= 0)
            return value;
        var half = viewSize / 2f;
        return (int)((value - half + 2f) * 0.25f) * 4 + half;
    }

    /// <summary>
    /// The plot's height from the terrain under its footprint: the highest sample for auto_z designs
    /// (x2game FUN_39736d30), otherwise the lowest, refused when the ground spans more than 6 m (FUN_397311b0).
    /// </summary>
    private (float? Z, string? Error) PlotHeight(HousingDesignData design, float x, float y)
    {
        var radius = design.GardenRadius > 0 ? design.GardenRadius : 1f;
        var min = float.MaxValue;
        var max = float.MinValue;
        var cos = MathF.Cos(_housingPlacementYaw);
        var sin = MathF.Sin(_housingPlacementYaw);
        var steps = Math.Max(1, (int)MathF.Ceiling(radius * 2f));
        for (var i = 0; i <= steps; i++)
        for (var j = 0; j <= steps; j++)
        {
            var dx = -radius + 2f * radius * i / steps;
            var dy = -radius + 2f * radius * j / steps;
            var sx = x + dx * cos - dy * sin;
            var sy = y + dx * sin + dy * cos;
            if (!World.HasHeightsAt(sx, sy))
                return (null, "terrain_unavailable");
            var h = World.TerrainHeightAt(sx, sy);
            min = MathF.Min(min, h);
            max = MathF.Max(max, h);
        }
        if (design.AutoZ)
            return (max, null);
        return max - min <= 6f ? (min, null) : (min, "house_cannot_locate_terrain_too_low");
    }

    /// <summary>
    /// Client-side placement feedback: the plot's corners must lie in the client's housing-area shapes whose
    /// housing group admits the category, and must not overlap another house's plot. The server decides.
    /// </summary>
    private (bool Allowed, string Reason) EvaluatePlacement(HousingDesignData design, NVector3 position, int zoneKey)
    {
        if (zoneKey < 0)
            return (false, "terrain_unavailable");
        var radius = design.GardenRadius > 0 ? design.GardenRadius : 1f;
        var corners = PlotCorners(position.X, position.Y, radius, _housingPlacementYaw);
        var shapes = _housingAreas?.ForZone(zoneKey) ?? [];
        foreach (var corner in corners.Append(new System.Numerics.Vector2(position.X, position.Y)))
        {
            var area = shapes.FirstOrDefault(shape => shape.Contains(corner.X, corner.Y));
            if (area is null)
                return (false, "house_cannot_locate_invalid_area");
            if (_housingPlacementCatalog?.IsCategoryAllowedInArea(area.AreaId, design.CategoryId) != true)
                return (false, "house_cannot_loacate_invalid_category_area");
        }
        foreach (var other in _houseStates.Values)
        {
            var otherDesign = _housingPlacementCatalog?.DesignById(other.TemplateId);
            var otherPosition = other.Position.Value;
            var otherRadius = otherDesign?.GardenRadius is > 0 ? otherDesign.GardenRadius : 1f;
            var otherYaw = _housingUnitIdsByTimeline.TryGetValue(other.TimelineId, out var unitId) &&
                           _units.TryGetValue(unitId, out var unit) ? unit.Snapshot.Yaw : 0f;
            if (PlotsOverlap(corners, PlotCorners(otherPosition.X, otherPosition.Y, otherRadius, otherYaw)))
                return (false, "house_cannot_locate_overlap_house");
        }
        return (true, "area_allowed");
    }

    private static System.Numerics.Vector2[] PlotCorners(float x, float y, float radius, float yaw)
    {
        var cos = MathF.Cos(yaw);
        var sin = MathF.Sin(yaw);
        return
        [
            Corner(-radius, -radius), Corner(radius, -radius), Corner(radius, radius), Corner(-radius, radius),
        ];

        System.Numerics.Vector2 Corner(float dx, float dy) =>
            new(x + dx * cos - dy * sin, y + dx * sin + dy * cos);
    }

    /// <summary>Separating-axis test for two convex quadrilaterals (touching edges do not overlap).</summary>
    private static bool PlotsOverlap(System.Numerics.Vector2[] a, System.Numerics.Vector2[] b)
    {
        foreach (var polygon in new[] { a, b })
            for (var i = 0; i < polygon.Length; i++)
            {
                var edge = polygon[(i + 1) % polygon.Length] - polygon[i];
                var axis = new System.Numerics.Vector2(-edge.Y, edge.X);
                var (minA, maxA) = Project(a, axis);
                var (minB, maxB) = Project(b, axis);
                if (maxA <= minB + 0.01f || maxB <= minA + 0.01f)
                    return false;
            }
        return true;

        static (float Min, float Max) Project(System.Numerics.Vector2[] polygon, System.Numerics.Vector2 axis)
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var point in polygon)
            {
                var value = System.Numerics.Vector2.Dot(point, axis);
                min = MathF.Min(min, value);
                max = MathF.Max(max, value);
            }
            return (min, max);
        }
    }

    private bool TryGetHousingGroundPoint(out NVector3 point, out int zoneKey)
    {
        point = default;
        zoneKey = -1;
        var viewport = GetViewport();
        var camera = viewport?.GetCamera3D();
        if (World is null || camera is null)
            return false;

        // The pointer as the last motion event placed it (scripted input moves it without an OS cursor).
        var cursor = _builderPointer ?? viewport!.GetMousePosition();
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
                // The original builder draws the design's own model (housing_build_invalid while the spot is refused).
                foreach (var (mesh, transform) in meshes)
                    if (Models.GetMesh(mesh) is { } godotMesh)
                        ghost.AddChild(new MeshInstance3D
                        {
                            Mesh = godotMesh,
                            Transform = transform,
                            Layers = ModelLibrary.ObjectLayer,
                            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                        });
                ApplyGhostTint(_lastPlacementAllowed || BuilderStep != HousingBuilderStep.Position);
            });
        });
    }

    /// <summary>
    /// materials/special/housing_build_invalid.mtl, which x2game FUN_397321a0 puts on the ghost while the spot has a
    /// placement error: an untextured red Illum (Diffuse 0.737/0.013/0.013) with GlowAmount 2.
    /// </summary>
    private static readonly StandardMaterial3D InvalidGhostMaterial = new()
    {
        AlbedoColor = new Color(0.7372f, 0.0127f, 0.0127f),
        EmissionEnabled = true,
        Emission = new Color(0.7372f, 0.0127f, 0.0127f),
        EmissionEnergyMultiplier = 2f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>A valid spot shows the design's own materials; an invalid one the housing_build_invalid material.</summary>
    private void ApplyGhostTint(bool valid)
    {
        if (_housingPlacementGhost == null)
            return;
        foreach (var mesh in _housingPlacementGhost.GetChildren().OfType<MeshInstance3D>())
            mesh.MaterialOverride = valid ? null : InvalidGhostMaterial;
    }

    /// <summary>SetRotation(GetYaw() + delta): the yaw is kept as sent, never clamped or quantized.</summary>
    private void RotateHousing(float radians)
    {
        _housingPlacementYaw = Mathf.Wrap(_housingPlacementYaw + radians, -Mathf.Pi, Mathf.Pi);
        if (_housingPlacementGhost != null)
            _housingPlacementGhost.Rotation = new Vector3(0, _housingPlacementYaw, 0);
    }

    private void SetBuilderStep(HousingBuilderStep step, string luaStep)
    {
        BuilderStep = step;
        ApplyGhostTint(step != HousingBuilderStep.Position || _lastPlacementAllowed);
        GD.Print($"[housing] BUILDER_STEP {luaStep}");
        HousingBuilderStepChanged?.Invoke(luaStep);
    }

    private void PublishPlacementFeedback(bool allowed, string reason, NVector3 position, int zoneKey)
    {
        var changed = _lastPlacementAllowed != allowed;
        _lastPlacementAllowed = allowed;
        _lastPlacementReason = reason;
        if (changed)
            ApplyGhostTint(allowed);

        var feedback = new HousingPlacementFeedback(position, _housingPlacementYaw, allowed, reason, zoneKey);
        var previous = _lastPlacementFeedback;
        if (previous is null || previous.CanPlace != feedback.CanPlace ||
            previous.Reason != feedback.Reason || previous.ZoneKey != feedback.ZoneKey ||
            NVector3.Distance(previous.Position, feedback.Position) > 0.1f || MathF.Abs(previous.Yaw - feedback.Yaw) > 0.001f)
        {
            if (previous is null || previous.CanPlace != feedback.CanPlace || previous.Reason != feedback.Reason)
                GD.Print($"[housing] placement {(allowed ? "valid" : "invalid")} ({reason}) at " +
                         $"{position.X:F0},{position.Y:F0},{position.Z:F2} zone {zoneKey}");
            _lastPlacementFeedback = feedback;
            HousingPlacementFeedbackChanged?.Invoke(feedback);
        }
    }

    private void EndBuilder(bool notify)
    {
        var wasActive = BuilderStep != HousingBuilderStep.None;
        if (BuilderStep is HousingBuilderStep.Confirm)
            HousingBuildInfoCleared?.Invoke();
        _housingPlacementGeneration++;
        _activeHousingPlacement = null;
        _housingPlacementGhost?.QueueFree();
        _housingPlacementGhost = null;
        _lastPlacementAllowed = false;
        _lastPlacementReason = "not_started";
        _lastPlacementFeedback = null;
        BuilderStep = HousingBuilderStep.None;
        if (!notify || !wasActive)
            return;
        GD.Print("[housing] BUILDER_END");
        HousingPlacementCancelled?.Invoke();
        HousingBuilderEnded?.Invoke();
    }

    // ---------------------------------------------------------------------------------------------------------
    // House interaction (X2House's current house)

    /// <summary>
    /// Opens a house's window the way x2game FUN_398861b0 does: CSRequestHouseTax {tl, nameplate doodad or 0}, the
    /// house becomes X2House's current house, then HOUSE_INTERACTION_START("housing", viewType, alwaysPublic).
    /// It runs when the server's interaction skill list names 11001/12106 first, or when the house's
    /// DoodadFuncParentInfo nameplate is used.
    /// </summary>
    public bool OpenHouseInteraction(ushort timelineId, uint nameplateDoodadId = 0)
    {
        if (!_houseStates.ContainsKey(timelineId))
            return false;
        if (_interactingHouse is { } open && open != timelineId)
            EndHouseInteraction();
        Client.SendGame(HousingRequests.CSRequestHouseTax, HousingRequests.RequestHouseTax(timelineId, nameplateDoodadId));
        _interactingHouse = timelineId;
        _interactingNameplate = nameplateDoodadId;
        GD.Print($"[housing] HOUSE_INTERACTION_START tl {timelineId} (nameplate {nameplateDoodadId})");
        HousingInteractionStarted?.Invoke(timelineId);
        return true;
    }

    private uint _interactingNameplate;
    private Vector2? _builderPointer;

    internal void TrackBuilderPointer(Vector2 position) => _builderPointer = position;

    /// <summary>
    /// HOUSE_INTERACTION_START's viewType from housings.category_id (x2game FUN_398852e0): 7 seafarm; 8, 16, 17, 21
    /// farm; 9-12, 15, 18, 22-33 house; anything else nil.
    /// </summary>
    public static string? HouseViewType(uint categoryId) => categoryId switch
    {
        7 => "seafarm",
        8 or 16 or 17 or 21 => "farm",
        >= 9 and <= 12 or 15 or 18 or >= 22 and <= 33 => "house",
        _ => null,
    };

    /// <summary>Closes the house window context (HOUSE_INTERACTION_END).</summary>
    public void EndHouseInteraction()
    {
        if (_interactingHouse is null)
            return;
        _interactingHouse = null;
        GD.Print("[housing] HOUSE_INTERACTION_END");
        HousingInteractionEnded?.Invoke();
    }

    /// <summary>The house timeline id of a spatial house unit, when known.</summary>
    public ushort? HouseTimelineOf(uint unitId) =>
        _housingTimelinesByUnit.TryGetValue(unitId, out var tl) ? tl : null;

    /// <summary>The spatial unit of a house timeline, when in sight.</summary>
    public uint? HouseUnitOf(ushort timelineId) =>
        _housingUnitIdsByTimeline.TryGetValue(timelineId, out var unit) ? unit : null;

    /// <summary>
    /// Right click on a house unit: CSStartInteraction, as captured (house, 0, extraInfo 1, pickId -1, mouse 2,
    /// modifiers 0). Nothing opens locally; the server's SCNpcInteractionSkillList answer decides (see
    /// <see cref="UseHouseInteractionSkill"/>).
    /// </summary>
    private bool BeginHouseInteraction(ITargetable target)
    {
        if (Client.Stage != ClientStage.InWorld || HouseTimelineOf(target.Id) is null)
            return false;
        Client.SendGame(CombatPacketWriters.StartNpcInteraction(
            target.Id, objectId: 0, extraInfo: 1, pickId: -1, mouseButton: 2, modifierKeys: 0));
        FaceTarget(target);
        GD.Print($"[housing] CSStartInteraction house unit {target.Id}");
        return true;
    }

    /// <summary>
    /// The client's handling of an interaction skill list for a house unit (x2game FUN_39884a70/FUN_39886420): only
    /// skills[0] is used. 11001/12106 open the house window, 12079 does nothing, any other skill (the build step's)
    /// is cast on the house with StartSkill (caster unit, target unit, no skill object).
    /// </summary>
    private void UseHouseInteractionSkill(NpcInteractionSkillsEvent list, ushort timelineId)
    {
        if (list.SkillIds.Count == 0)
            return;
        var skill = list.SkillIds[0];
        GD.Print($"[housing] interaction skill list for house tl {timelineId}: [{string.Join(",", list.SkillIds)}]");
        if (skill is SkillConstructionInfo or SkillHousingInteraction)
            OpenHouseInteraction(timelineId);
        else if (skill != SkillNilLoot && _houseStates.TryGetValue(timelineId, out var state) &&
                 state.OwnerId == Entered.CharacterId)
            Client.SendGame(CombatPacketWriters.StartSkillOnUnit(skill, Entered.UnitId, list.NpcUnitId));
        // Someone else's house: the original first asks for confirmation (FUN_39888730); not offered here.
    }

    /// <summary>The house's DoodadFuncParentInfo nameplate doodad in sight (spawned with the finished house), if any.</summary>
    public uint? HouseNameplateOf(ushort timelineId)
    {
        if (HouseUnitOf(timelineId) is not { } houseUnit)
            return null;
        foreach (var (id, unit) in _units)
            if (unit.Snapshot.Kind == UnitKind.Doodad && unit.Snapshot.ParentUnitId == houseUnit &&
                Doodads.HasParentInfoFunc(unit.Snapshot.TemplateId, unit.Snapshot.PhaseId))
                return id;
        return null;
    }

    /// <summary>
    /// Test driver: handles an interaction skill list for a house as if SCNpcInteractionSkillList had arrived with
    /// these skills (the local World never answers CSStartInteraction for houses).
    /// </summary>
    public bool SimulateHouseInteractionSkills(ushort timelineId, params uint[] skills)
    {
        if (HouseUnitOf(timelineId) is not { } unitId)
            return false;
        UseHouseInteractionSkill(new NpcInteractionSkillsEvent(unitId, 0, 1, -1, 2, skills, true, 0), timelineId);
        return true;
    }

    /// <summary>A DoodadFuncParentInfo nameplate opens its parent house's window (x2game FUN_3987b670).</summary>
    private bool OpenHouseFromNameplate(uint doodadId, uint parentUnitId)
    {
        if (HouseTimelineOf(parentUnitId) is not { } tl)
            return false;
        return OpenHouseInteraction(tl, doodadId);
    }

    private float HouseRadius(ushort timelineId) =>
        _houseStates.TryGetValue(timelineId, out var state) &&
        _housingPlacementCatalog?.DesignById(state.TemplateId) is { GardenRadius: > 0 } design
            ? design.GardenRadius
            : 0f;

    private void UpdateHouseInteractionRange()
    {
        if (_interactingHouse is not { } tl)
            return;
        if (!_houseStates.TryGetValue(tl, out var state))
        {
            EndHouseInteraction();
            return;
        }
        var position = state.Position.Value;
        if (NVector3.Distance(position, Player.CryPosition) > HouseInteractionRange + HouseRadius(tl))
            EndHouseInteraction();
    }

    /// <summary>The build step the house is on (0-based), or -1 when finished or unknown.</summary>
    public int HouseCurrentStepIndex(ushort timelineId)
    {
        if (!_houseStates.TryGetValue(timelineId, out var state) || state.AllSteps == 0 ||
            state.CurrentStep >= state.AllSteps || _housingPlacementCatalog is null)
            return -1;
        var done = (int)state.CurrentStep;
        foreach (var step in _housingPlacementCatalog.BuildSteps(state.TemplateId))
        {
            if (done < step.NumActions)
                return step.Step;
            done -= step.NumActions;
        }
        return -1;
    }

    /// <summary>
    /// Casts the construction skill of the house's current build step on the house (CSStartSkill, unit target);
    /// the World's Building interaction consumes the step material and answers with SCHouseBuildProgress.
    /// </summary>
    public bool ConstructHouseStep(ushort timelineId)
    {
        if (_housingPlacementCatalog is null || !_houseStates.TryGetValue(timelineId, out var state) ||
            HouseUnitOf(timelineId) is not { } unitId)
            return false;
        var index = HouseCurrentStepIndex(timelineId);
        var step = _housingPlacementCatalog.BuildSteps(state.TemplateId).FirstOrDefault(s => s.Step == index);
        if (step is null || step.SkillId == 0)
            return false;
        Client.SendGame(CombatPacketWriters.StartSkillOnUnit(step.SkillId, Entered.UnitId, unitId));
        GD.Print($"[housing] construct tl {timelineId} step {index}: skill {step.SkillId} on unit {unitId}");
        return true;
    }

    /// <summary>
    /// X2House:Demolish(package, sealCount) on the house whose window is open: x2game FUN_39998130 casts is_demolish
    /// (12945) on the house with skill object 0x11 {bool package, u32 sealCount}.
    /// </summary>
    public bool DemolishInteractingHouse(bool package = false, uint sealCount = 0)
    {
        if (InteractingHouseState is not { } state || state.OwnerId != Entered.CharacterId ||
            HouseUnitOf(state.TimelineId) is not { } unitId)
            return false;
        Client.SendGame(HousingRequests.CSStartSkill,
            HousingRequests.Demolish(SkillDemolish, Entered.UnitId, unitId, package, sealCount));
        GD.Print($"[housing] demolish tl {state.TimelineId} unit {unitId}: StartSkill {SkillDemolish} package {package} seals {sealCount}");
        return true;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Server state

    /// <summary>Reduces housing packets on the main thread and publishes their C# UI events.</summary>
    private void ApplyHousingEvent(GameEvent value)
    {
        switch (value)
        {
            case HouseStateEvent state:
                _houseStates[state.TimelineId] = state;
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
            case NpcInteractionSkillsEvent interaction when HouseTimelineOf(interaction.NpcUnitId) is { } houseTimeline:
                UseHouseInteractionSkill(interaction, houseTimeline);
                break;
            case HouseBuildProgressEvent progress:
                var progressChanged = true;
                if (_houseStates.TryGetValue(progress.TimelineId, out var known))
                {
                    progressChanged = known.ModelId != progress.ModelId || known.CurrentStep != (uint)Math.Max(0, progress.CurrentStep);
                    // Finished houses report (0, 0) steps in SCHouseState; the progress packet reports cur == all.
                    var finished = progress.CurrentStep >= progress.AllSteps;
                    _houseStates[progress.TimelineId] = known with
                    {
                        ModelId = progress.ModelId,
                        AllSteps = finished ? 0u : (uint)Math.Max(0, progress.AllSteps),
                        CurrentStep = finished ? 0u : (uint)Math.Max(0, progress.CurrentStep),
                    };
                }
                if (_housingUnitIdsByTimeline.TryGetValue(progress.TimelineId, out var unitId) &&
                    _units.TryGetValue(unitId, out var building))
                    QueueHousing(building, progress.ModelId);
                GD.Print($"[housing] build progress tl {progress.TimelineId} model {progress.ModelId} " +
                         $"{progress.CurrentStep}/{progress.AllSteps}");
                // HOUSE_STEP_INFO_UPDATED only when the model or the step changed (x2game FUN_3971e2a0).
                if (progressChanged)
                    HousingBuildProgressChanged?.Invoke(progress);
                break;
            case HousePermissionChangedEvent permission:
                if (_houseStates.TryGetValue(permission.TimelineId, out var permitted))
                    _houseStates[permission.TimelineId] = permitted with { Permission = permission.Permission };
                if (_ownedHouses.TryGetValue(permission.TimelineId, out var ownedPermission))
                    _ownedHouses[permission.TimelineId] = ownedPermission with { Permission = permission.Permission };
                GD.Print($"[housing] permission tl {permission.TimelineId} -> {permission.Permission}");
                HousingPermissionChanged?.Invoke(permission);
                break;
            case HouseDemolishedEvent demolished:
                GD.Print($"[housing] demolished tl {demolished.TimelineId}");
                ForgetHouse(demolished.TimelineId);
                HousingDemolished?.Invoke(demolished);
                break;
            case HouseDataEvent houses:
                foreach (var record in houses.Houses)
                    _ownedHouses[record.TimelineId] = record;
                OwnedHousesReceived?.Invoke(houses);
                break;
            case HouseRemovedEvent removed:
                _ownedHouses.Remove(removed.TimelineId);
                HousingRemoved?.Invoke(removed);
                break;
            case HouseFarmSummaryEvent farm:
                HousingFarmSummaryReceived?.Invoke(farm);
                break;
            case HouseTaxInfoEvent tax:
                _houseTaxes[tax.TimelineId] = tax;
                HousingTaxInfoReceived?.Invoke(tax);
                break;
            case HouseRecoverToggledEvent recover:
                if (_houseStates.TryGetValue(recover.TimelineId, out var recoverable))
                    _houseStates[recover.TimelineId] = recoverable with { AllowRecover = recover.AllowRecover };
                HousingRecoverToggled?.Invoke(recover);
                break;
            case HouseConstructTaxEvent quote:
                if (BuilderStep == HousingBuilderStep.Quote && _activeHousingPlacement is { } placement &&
                    quote.DesignId == placement.Design.DesignId)
                {
                    _lastQuote = quote;
                    BuilderStep = HousingBuilderStep.Confirm;
                    GD.Print($"[housing] quote design {quote.DesignId}: base {quote.BaseTax} deposit {quote.DepositTax} " +
                             $"total {quote.TotalTax} weekly {quote.WeeklyTax} heavy {quote.IsHeavyTaxHouse} " +
                             $"({quote.HeavyTaxHouseCount}/{quote.NormalTaxHouseCount}) hostile {quote.HostileTaxRate}");
                    HousingBuildInfoReceived?.Invoke(quote, placement.Design);
                }
                break;
            case TeleportedEvent:
                // The original quits the builder and any house window on teleport (OnGameModeQuit reason teleport).
                EndBuilder(notify: true);
                EndHouseInteraction();
                break;
            case UnitsRemovedEvent removedUnits:
                foreach (var removedUnitId in removedUnits.UnitIds)
                {
                    if (_housingTimelinesByUnit.Remove(removedUnitId, out var timelineId))
                    {
                        _housingUnitIdsByTimeline.Remove(timelineId);
                        _houseStates.Remove(timelineId);
                        if (_interactingHouse == timelineId)
                            EndHouseInteraction();
                    }
                    _pendingHousingStatesByUnit.Remove(removedUnitId);
                }
                break;
        }
    }

    private void ForgetHouse(ushort timelineId)
    {
        if (_interactingHouse == timelineId)
            EndHouseInteraction();
        _houseStates.Remove(timelineId);
        _houseTaxes.Remove(timelineId);
        _ownedHouses.Remove(timelineId);
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

/// <summary>
/// Receives unhandled input before the session's other nodes (it is the scene root's last child) so the builder
/// owns the mouse buttons and the wheel while it is active; everything else passes through.
/// </summary>
public sealed partial class HousingBuilderInput : Node
{
    private readonly OnlineSession? _session;

    public HousingBuilderInput() { }

    public HousingBuilderInput(OnlineSession session) => _session = session;

    public override void _Input(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseMotion motion && _session is not null && IsInstanceValid(_session))
            _session.TrackBuilderPointer(motion.Position);
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (_session is null || !IsInstanceValid(_session))
        {
            QueueFree();
            return;
        }
        if (_session.HandleHousingUnhandledInput(inputEvent))
            GetViewport().SetInputAsHandled();
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

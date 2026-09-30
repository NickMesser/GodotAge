#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Client;

/// <summary>
/// Network-independent ArcheAge-style target selection, picking, input, and presentation.
/// Assign <see cref="Registry"/> and <see cref="Camera"/> before adding this node to the tree.
/// </summary>
public partial class TargetingController : Node
{
    public static readonly StringName TargetNextAction = "target_next";
    public static readonly StringName TargetPreviousAction = "target_prev";
    public static readonly StringName TargetSelfAction = "target_self";
    public static readonly StringName TargetClearAction = "target_clear";
    public static readonly StringName[] PartyActions =
    [
        "target_party_1", "target_party_2", "target_party_3", "target_party_4",
    ];

    public event Action<ITargetable?>? TargetChanged;
    public event Action<ITargetable?>? TargetOfTargetChanged;
    public event Action<ITargetable?>? TargetDoubleClicked;
    public event Action<ITargetable>? TargetRightClicked;

    public ITargetableRegistry? Registry { get; set; }
    public Camera3D? Camera { get; set; }
    public Func<ITargetable?>? ResolveSelf { get; set; }
    public Func<int, ITargetable?>? ResolvePartyMember { get; set; }

    /// <summary>Matches the recovered <c>tab_targeting_fan_dist</c> client default.</summary>
    [Export(PropertyHint.Range, "1,200,1,suffix:m")]
    public float MaxTabRange { get; set; } = 50f;

    /// <summary>Full width of the camera-forward targeting fan; the recovered client default is 60 degrees.</summary>
    [Export(PropertyHint.Range, "1,180,1,suffix:°")]
    public float TabFanAngleDegrees { get; set; } = 60f;

    /// <summary>Recovered <c>tab_targeting_z_limit</c> client default.</summary>
    [Export(PropertyHint.Range, "1,100,1,suffix:m")]
    public float TabVerticalLimit { get; set; } = 20f;

    [Export(PropertyHint.Range, "1,200,1,suffix:m")]
    public float MaxNameplateRange { get; set; } = 50f;

    [Export(PropertyHint.Range, "1,128,1")]
    public int MaxNameplates { get; set; } = 48;

    [Export(PropertyHint.Range, "1,48,1")]
    public int NameplateUpdatesPerFrame { get; set; } = 8;

    [Export] public bool EnableClickTargeting { get; set; } = true;
    [Export] public bool EnableVisuals { get; set; } = true;
    /// <summary>Allows an online session to route the client's x2_* bindings without also firing demo hotkeys.</summary>
    public bool EnableHotkeys { get; set; } = true;

    public ITargetable? Target { get; private set; }
    public ITargetable? TargetOfTarget { get; private set; }

    private readonly List<uint> _cycleIds = [];
    private int _cycleIndex = -1;
    private TargetingVisuals? _visuals;
    private Vector2 _leftPressPosition;
    private Vector2 _rightPressPosition;
    private bool _leftPressPending;
    private bool _leftPressDragged;
    private bool _leftPressSuppressed;
    private bool _leftPressDoubleClick;
    private bool _rightPressPending;
    private bool _rightPressDragged;
    private bool _rightPressSuppressed;
    private bool _rightMouseDown;

    public override void _Ready()
    {
        RegisterInputActions();
        if (Camera == null)
            Camera = GetViewport()?.GetCamera3D();
        if (EnableVisuals)
        {
            _visuals = new TargetingVisuals
            {
                Name = "TargetingVisuals",
                Controller = this,
            };
            AddChild(_visuals);
        }
    }

    public override void _Process(double delta)
    {
        var next = ResolveTargetOfTarget();
        if (!ReferenceEquals(next, TargetOfTarget))
        {
            TargetOfTarget = next;
            TargetOfTargetChanged?.Invoke(next);
        }

        if (Target != null && (!GodotObject.IsInstanceValid(Target.Node) || !Target.Node.IsInsideTree()))
            Clear();
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Right } rightButton)
        {
            _rightMouseDown = rightButton.Pressed;
            if (rightButton.Pressed)
            {
                _rightPressPosition = rightButton.Position;
                _rightPressPending = true;
                _rightPressDragged = false;
                _rightPressSuppressed = Input.IsMouseButtonPressed(MouseButton.Left);
            }
            else
            {
                var selectOnRelease = EnableClickTargeting && _rightPressPending && !_rightPressDragged && !_rightPressSuppressed;
                _rightPressPending = false;
                if (selectOnRelease && Pick(rightButton.Position) is { } rightHit)
                {
                    SetTarget(rightHit);
                    TargetRightClicked?.Invoke(rightHit);
                    GetViewport().SetInputAsHandled();
                }
            }
            return;
        }

        if (inputEvent is InputEventMouseMotion motion)
        {
            if (_leftPressPending && motion.Position.DistanceTo(_leftPressPosition) > 6f)
                _leftPressDragged = true;
            if (_rightPressPending && motion.Position.DistanceTo(_rightPressPosition) > 6f)
                _rightPressDragged = true;
            return;
        }

        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } leftPress)
        {
            _leftPressPosition = leftPress.Position;
            _leftPressPending = true;
            _leftPressDragged = false;
            _leftPressSuppressed = _rightMouseDown;
            _leftPressDoubleClick = leftPress.DoubleClick;
            if (_rightMouseDown)
                _rightPressSuppressed = true;
            return;
        }

        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } leftRelease)
        {
            var selectOnRelease = EnableClickTargeting && _leftPressPending && !_leftPressDragged && !_leftPressSuppressed;
            _leftPressPending = false;
            if (selectOnRelease && Pick(leftRelease.Position) is { } leftHit)
            {
                SetTarget(leftHit);
                if (_leftPressDoubleClick)
                    TargetDoubleClicked?.Invoke(leftHit);
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (EnableHotkeys)
        {
        if (inputEvent.IsActionPressed(TargetPreviousAction, exactMatch: true))
        {
            SelectNext(reverse: true);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (inputEvent.IsActionPressed(TargetNextAction, exactMatch: true))
        {
            SelectNext(reverse: false);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (inputEvent.IsActionPressed(TargetClearAction, exactMatch: true))
        {
            Clear();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (inputEvent.IsActionPressed(TargetSelfAction, exactMatch: true))
        {
            SetTarget(ResolveSelf?.Invoke());
            GetViewport().SetInputAsHandled();
            return;
        }
        for (var i = 0; i < PartyActions.Length; i++)
        {
            if (!inputEvent.IsActionPressed(PartyActions[i], exactMatch: true))
                continue;
            SetTarget(ResolvePartyMember?.Invoke(i));
            GetViewport().SetInputAsHandled();
            return;
        }
        }

    }

    /// <summary>Selects the next living hostile in the camera's forward hemisphere, nearest first.</summary>
    public void SelectNext(bool reverse)
    {
        if (Registry == null || Camera == null)
            return;

        var origin = Camera.GlobalPosition;
        var forward = -Camera.GlobalBasis.Z.Normalized();
        var minForwardDot = Mathf.Cos(Mathf.DegToRad(TabFanAngleDegrees * 0.5f));
        var candidates = Registry.All
            .Where(IsLive)
            .Where(t => t.Relation == TargetRelation.Hostile && !t.Dead)
            .Select(t => (Target: t, Delta: TargetCenter(t) - origin))
            .Where(x => x.Delta.LengthSquared() <= MaxTabRange * MaxTabRange)
            .Where(x => Mathf.Abs(x.Delta.Y) <= TabVerticalLimit)
            .Where(x => x.Delta.Normalized().Dot(forward) >= minForwardDot)
            .OrderBy(x => x.Delta.LengthSquared())
            .ThenBy(x => x.Target.Id)
            .Select(x => x.Target)
            .ToList();

        if (candidates.Count == 0)
        {
            _cycleIds.Clear();
            _cycleIndex = -1;
            return;
        }

        var ids = candidates.Select(t => t.Id).ToList();
        if (!_cycleIds.SequenceEqual(ids))
        {
            _cycleIds.Clear();
            _cycleIds.AddRange(ids);
            _cycleIndex = reverse ? _cycleIds.Count - 1 : 0;
        }
        else
        {
            var selectedIndex = Target == null ? -1 : _cycleIds.IndexOf(Target.Id);
            var basis = selectedIndex >= 0 ? selectedIndex : _cycleIndex;
            _cycleIndex = reverse
                ? (basis <= 0 ? _cycleIds.Count - 1 : basis - 1)
                : (basis + 1) % _cycleIds.Count;
        }

        SetTarget(candidates.First(t => t.Id == _cycleIds[_cycleIndex]), preserveCycle: true);
    }

    public bool Select(uint id)
    {
        var target = Registry?.All.FirstOrDefault(t => t.Id == id && IsLive(t));
        if (target == null)
            return false;
        SetTarget(target);
        return true;
    }

    public bool SelectTargetOfTarget() => TargetOfTarget != null && Select(TargetOfTarget.Id);

    public void Clear() => SetTarget(null);

    public ITargetable? Pick(Vector2 viewportPosition)
    {
        if (Registry == null || Camera == null)
            return null;
        var origin = Camera.ProjectRayOrigin(viewportPosition);
        var direction = Camera.ProjectRayNormal(viewportPosition).Normalized();
        var bestDistance = float.PositiveInfinity;
        ITargetable? best = null;
        foreach (var target in Registry.All)
        {
            if (!IsLive(target))
                continue;
            var distance = target.Bounds is { } bounds
                ? IntersectLocalAabb(origin, direction, target.Node.GlobalTransform, bounds)
                : IntersectCapsule(origin, direction, target.Node.GlobalPosition, target.Height);
            if (distance is >= 0f && distance < bestDistance)
            {
                bestDistance = distance.Value;
                best = target;
            }
        }
        return best;
    }

    public static void RegisterInputActions()
    {
        AddKeyAction(TargetNextAction, Key.Tab);
        AddKeyAction(TargetPreviousAction, Key.Tab, shift: true);
        AddKeyAction(TargetSelfAction, Key.F1);
        AddKeyAction(TargetClearAction, Key.Escape);
        for (var i = 0; i < PartyActions.Length; i++)
            AddKeyAction(PartyActions[i], Key.F2 + i);
    }

    private void SetTarget(ITargetable? target, bool preserveCycle = false)
    {
        if (ReferenceEquals(Target, target))
            return;
        Target = target;
        if (!preserveCycle)
        {
            _cycleIds.Clear();
            _cycleIndex = -1;
        }
        TargetChanged?.Invoke(target);
    }

    private ITargetable? ResolveTargetOfTarget()
    {
        if (Target is not ITargetOfTargetSource { TargetId: { } id } || Registry == null)
            return null;
        return Registry.All.FirstOrDefault(t => t.Id == id && IsLive(t));
    }

    private static bool IsLive(ITargetable target) =>
        target.Node != null && GodotObject.IsInstanceValid(target.Node) && target.Node.IsInsideTree();

    internal static Vector3 TargetCenter(ITargetable target) =>
        target.Node.GlobalPosition + Vector3.Up * Mathf.Max(0.1f, target.Height * 0.5f);

    private static void AddKeyAction(StringName action, Key key, bool shift = false)
    {
        if (!InputMap.HasAction(action))
            InputMap.AddAction(action);
        foreach (var existing in InputMap.ActionGetEvents(action))
            if (existing is InputEventKey eventKey && eventKey.Keycode == key && eventKey.ShiftPressed == shift)
                return;
        InputMap.ActionAddEvent(action, new InputEventKey { Keycode = key, ShiftPressed = shift });
    }

    private static float? IntersectLocalAabb(Vector3 rayOrigin, Vector3 rayDirection, Transform3D transform, Aabb bounds)
    {
        var inverse = transform.AffineInverse();
        var origin = inverse * rayOrigin;
        var direction = inverse.Basis * rayDirection;
        var min = bounds.Position;
        var max = bounds.End;
        var near = 0f;
        var far = 10000f;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = origin[axis];
            var d = direction[axis];
            if (Mathf.Abs(d) < 0.000001f)
            {
                if (o < min[axis] || o > max[axis])
                    return null;
                continue;
            }
            var a = (min[axis] - o) / d;
            var b = (max[axis] - o) / d;
            if (a > b)
                (a, b) = (b, a);
            near = Mathf.Max(near, a);
            far = Mathf.Min(far, b);
            if (near > far)
                return null;
        }
        return near;
    }

    private static float? IntersectCapsule(Vector3 origin, Vector3 direction, Vector3 feet, float requestedHeight)
    {
        var height = Mathf.Max(0.5f, requestedHeight);
        var radius = Mathf.Clamp(height * 0.22f, 0.25f, 0.75f);
        var lower = feet + Vector3.Up * radius;
        var upper = feet + Vector3.Up * Mathf.Max(radius, height - radius);
        var best = float.PositiveInfinity;

        // Upright finite cylinder.
        var rel = origin - feet;
        var a = direction.X * direction.X + direction.Z * direction.Z;
        var b = 2f * (rel.X * direction.X + rel.Z * direction.Z);
        var c = rel.X * rel.X + rel.Z * rel.Z - radius * radius;
        if (a > 0.000001f)
        {
            var discriminant = b * b - 4f * a * c;
            if (discriminant >= 0f)
            {
                var root = Mathf.Sqrt(discriminant);
                foreach (var t in new[] { (-b - root) / (2f * a), (-b + root) / (2f * a) })
                {
                    var y = origin.Y + direction.Y * t;
                    if (t >= 0f && y >= lower.Y && y <= upper.Y)
                        best = Mathf.Min(best, t);
                }
            }
        }
        best = Mathf.Min(best, IntersectSphere(origin, direction, lower, radius));
        best = Mathf.Min(best, IntersectSphere(origin, direction, upper, radius));
        return float.IsPositiveInfinity(best) ? null : best;
    }

    private static float IntersectSphere(Vector3 origin, Vector3 direction, Vector3 center, float radius)
    {
        var rel = origin - center;
        var b = rel.Dot(direction);
        var c = rel.LengthSquared() - radius * radius;
        var discriminant = b * b - c;
        if (discriminant < 0f)
            return float.PositiveInfinity;
        var near = -b - Mathf.Sqrt(discriminant);
        if (near >= 0f)
            return near;
        var far = -b + Mathf.Sqrt(discriminant);
        return far >= 0f ? far : float.PositiveInfinity;
    }
}

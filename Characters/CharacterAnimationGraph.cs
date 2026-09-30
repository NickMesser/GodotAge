using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Per-character AnimationTree used by <see cref="CharacterNode"/>. Locomotion is the base input and actions are
/// passed through a OneShot. While the character moves, ordinary actions are filtered to the upper-body subtree;
/// death is always full-body. Clip decoding/baking is supplied by CharacterBuilder and therefore stays off-thread.
/// </summary>
internal sealed class CharacterAnimationGraph
{
    internal delegate void ClipLoader(string clip, Action<Animation> ready);

    private const string DynamicLibraryPrefix = "__action_";
    private readonly CharacterNode _owner;
    private readonly Skeleton3D _skeleton;
    private readonly AnimationLibrary _library;
    private readonly ClipLoader _load;
    private readonly AnimationTree _tree;
    private readonly AnimationNodeAnimation _locomotion = new();
    private readonly AnimationNodeAnimation _action = new();
    private readonly AnimationNodeOneShot _oneShot = new();
    private readonly Dictionary<string, string> _installed = new(StringComparer.OrdinalIgnoreCase);
    private int _request;
    private int _combatIdleRequest;
    private int _movementRequest;
    private string _currentLocomotion = "";
    internal string CurrentAnimation => _currentLocomotion;
    /// <summary>Length in seconds of the clip most recently started through <see cref="Start"/> (0 before the first).</summary>
    internal float LastActionLength { get; private set; }
    /// <summary>Counts the clips started through <see cref="Start"/>; a caller compares it to tell whether its clip really began.</summary>
    internal int ActionSerial { get; private set; }
    private MovementPose _movementPose;
    private string _movementAnimation = "";
    private string _combatIdle = "";
    private bool _actionActive;
    private bool _actionUpperBody;
    private bool _actionHolds;
    private bool _frozen;
    private ulong _actionEndsAt;
    private PendingAction _queued;
    private readonly Queue<PendingAction> _sequence = new();
    private Action _sequenceReachedLoop;

    private sealed record PendingAction(Animation Clip, string Name, float Blend, bool Loop, bool UpperBody, bool Hold);

    internal CharacterAnimationGraph(
        CharacterNode owner,
        Skeleton3D skeleton,
        AnimationLibrary library,
        ClipLoader load)
    {
        _owner = owner;
        _skeleton = skeleton;
        _library = library;
        _load = load;

        var blendTree = new AnimationNodeBlendTree();
        blendTree.AddNode("locomotion", _locomotion, new Vector2(-400, -80));
        blendTree.AddNode("action", _action, new Vector2(-400, 100));
        blendTree.AddNode("one_shot", _oneShot, new Vector2(-160, 0));
        blendTree.ConnectNode("one_shot", 0, "locomotion");
        blendTree.ConnectNode("one_shot", 1, "action");
        blendTree.ConnectNode("output", 0, "one_shot");

        _tree = new AnimationTree
        {
            Name = "AnimationTree",
            // AnimationTree and AnimationPlayer are siblings under CharacterNode.
            AnimPlayer = new NodePath("../Animations"),
            TreeRoot = blendTree
        };
        _owner.AddChild(_tree);
        ConfigureUpperBodyFilter();
        _tree.Active = true;
    }

    internal void Update(float speed, MovementPose pose)
    {
        if (_frozen) return;
        if (pose != _movementPose)
        {
            _movementPose = pose;
            _movementAnimation = "";
            var ticket = ++_movementRequest;
            if (pose is not (MovementPose.Idle or MovementPose.Walk or MovementPose.Run))
                _load("@movement:" + pose, baked =>
                {
                    if (ticket != _movementRequest || baked == null || !GodotObject.IsInstanceValid(_owner)) return;
                    _movementAnimation = Install(baked, "movement_" + pose,
                        loop: pose is MovementPose.Fall or MovementPose.SwimIdle or MovementPose.SwimMove or
                            MovementPose.SwimDive or MovementPose.Glide or MovementPose.Mounted or MovementPose.MountedRun,
                        hold: false);
                    _currentLocomotion = "";
                });
        }

        var wanted = _movementAnimation.Length > 0 ? _movementAnimation
            : speed > 3.5f ? Pick("run", "walk", "idle")
            : speed > 0.2f ? Pick("walk", "run", "idle")
            : _combatIdle.Length > 0 ? _combatIdle : Pick("idle", "walk", "run");
        if (!string.IsNullOrEmpty(wanted) && wanted != _currentLocomotion)
        {
            _locomotion.Animation = wanted;
            _currentLocomotion = wanted;
        }

        if (_actionActive && _actionUpperBody && !_actionHolds)
            _oneShot.FilterEnabled = speed > 0.2f;

        if (_queued != null && Time.GetTicksMsec() >= _actionEndsAt)
        {
            var next = _queued;
            _queued = null;
            Start(next.Clip, next.Name, next.Blend, next.Loop, next.UpperBody, next.Hold);
        }
        else if (_sequence.Count > 0 && _queued == null && _actionEndsAt != ulong.MaxValue &&
                 Time.GetTicksMsec() >= _actionEndsAt)
        {
            var next = _sequence.Dequeue();
            Start(next.Clip, next.Name, next.Blend, next.Loop, next.UpperBody, next.Hold);
            if (next.Loop) TakeReachedLoop()?.Invoke();
        }
        else if (_actionActive && !_actionHolds && _queued == null && _sequence.Count == 0 && _actionEndsAt != ulong.MaxValue &&
                 Time.GetTicksMsec() >= _actionEndsAt)
        {
            _actionActive = false;
            _oneShot.FilterEnabled = false;
        }
    }

    internal void PlayAction(string clip, float blend, bool loop, bool fullBody = false, Action started = null)
    {
        var ticket = ++_request;
        _queued = null;
        _sequence.Clear();
        _sequenceReachedLoop = null;
        if (string.IsNullOrWhiteSpace(clip))
        {
            started?.Invoke();
            return;
        }
        _load(clip, baked =>
        {
            if (ticket != _request || !GodotObject.IsInstanceValid(_owner)) return;
            if (baked != null) Start(baked, clip, blend, loop, upperBody: !fullBody, hold: false);
            started?.Invoke(); // also when the clip does not exist: the caller reveals the character on the locomotion idle
        });
    }

    internal void PlayCastLoop(string startClip, string loopClip, float blend, bool fullBody = false, Action started = null)
    {
        var ticket = ++_request;
        _queued = null;
        _sequence.Clear();
        _sequenceReachedLoop = null;
        Animation start = null, loop = null;
        var startDone = false;
        var loopDone = false;

        void BeginWhenReady()
        {
            if (ticket != _request || !startDone || !loopDone)
                return;
            if (start == null)
            {
                if (loop != null)
                    Start(loop, loopClip, blend, loop: true, upperBody: !fullBody, hold: false);
                started?.Invoke(); // neither clip exists: still let the caller reveal the character
                return;
            }
            Start(start, startClip, blend, loop: false, upperBody: !fullBody, hold: false);
            started?.Invoke();
            if (loop != null)
                _queued = new PendingAction(loop, loopClip, blend, Loop: true, UpperBody: !fullBody, Hold: false);
        }

        _load(startClip, baked =>
        {
            if (ticket != _request || !GodotObject.IsInstanceValid(_owner)) return;
            start = baked;
            startDone = true;
            BeginWhenReady();
        });
        _load(loopClip, baked =>
        {
            if (ticket != _request || !GodotObject.IsInstanceValid(_owner)) return;
            loop = baked;
            loopDone = true;
            BeginWhenReady();
        });
    }

    internal void EndCastLoop(string endClip, float blend)
    {
        var ticket = ++_request;
        _queued = null;
        _sequence.Clear();
        _sequenceReachedLoop = null;
        Load(endClip, ticket, baked => Start(baked, endClip, blend, loop: false, upperBody: true, hold: false));
    }

    internal void SetCombatIdle(string clip)
    {
        var ticket = ++_combatIdleRequest;
        if (string.IsNullOrWhiteSpace(clip))
        {
            _combatIdle = "";
            return;
        }
        _load(clip, baked =>
        {
            if (ticket != _combatIdleRequest || baked == null || !GodotObject.IsInstanceValid(_owner)) return;
            _combatIdle = Install(baked, clip, loop: true, hold: false);
            if (_owner.Speed <= 0.2f)
                _currentLocomotion = "";
        });
    }

    internal void PlayDeath(string clip, float blend)
    {
        var ticket = ++_request;
        _queued = null;
        Load(clip, ticket, baked => Start(baked, clip, blend, loop: false, upperBody: false, hold: true));
    }

    internal void StopAction(float blend)
    {
        ++_request;
        _queued = null;
        _sequence.Clear();
        _sequenceReachedLoop = null;
        _actionActive = false;
        _actionHolds = false;
        _oneShot.FadeOutTime = Math.Max(0, blend);
        _tree.Set("parameters/one_shot/request", (int)AnimationNodeOneShot.OneShotRequest.FadeOut);
    }

    internal void SetFrozen(bool frozen)
    {
        _frozen = frozen;
        _tree.Active = !frozen;
    }

    private Action TakeReachedLoop()
    {
        var callback = _sequenceReachedLoop;
        _sequenceReachedLoop = null;
        return callback;
    }

    /// <summary>
    /// Plays clips back to back. <paramref name="reachedLoop"/> runs once the final (looping) clip has started, or at once when
    /// none of the clips exists for this character, so a caller can tell when the sequence is over.
    /// </summary>
    internal void PlayPresentationSequence(IReadOnlyList<string> clips, bool loopLast = true, float blend = 0.2f, Action started = null,
        Action reachedLoop = null)
    {
        var names = clips.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (names.Length == 0)
        {
            reachedLoop?.Invoke();
            return;
        }
        var ticket = ++_request;
        _queued = null;
        _sequence.Clear();
        _sequenceReachedLoop = reachedLoop;
        var loaded = new Animation[names.Length];
        var completed = new bool[names.Length];
        void BeginWhenReady()
        {
            if (ticket != _request || completed.Any(value => !value)) return;
            var available = Enumerable.Range(0, names.Length).Where(i => loaded[i] != null).ToArray();
            if (available.Length == 0)
            {
                started?.Invoke(); // nothing to play for this character (e.g. a race without class clips): still reveal it
                TakeReachedLoop()?.Invoke();
                return;
            }
            var onlyClipLoops = loopLast && available.Length == 1;
            Start(loaded[available[0]], names[available[0]], blend, loop: onlyClipLoops, upperBody: false, hold: false);
            started?.Invoke();
            if (onlyClipLoops) TakeReachedLoop()?.Invoke();
            for (var i = 1; i < available.Length; i++)
            {
                var last = i == available.Length - 1;
                var index = available[i];
                _sequence.Enqueue(new PendingAction(loaded[index], names[index], blend,
                    Loop: loopLast && last, UpperBody: false, Hold: false));
            }
        }
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            _load(names[index], baked =>
            {
                if (ticket != _request || !GodotObject.IsInstanceValid(_owner)) return;
                loaded[index] = baked;
                completed[index] = true;
                BeginWhenReady();
            });
        }
    }

    private void Load(string clip, int ticket, Action<Animation> ready)
    {
        if (string.IsNullOrWhiteSpace(clip)) return;
        _load(clip, baked =>
        {
            if (ticket == _request && baked != null && GodotObject.IsInstanceValid(_owner))
                ready(baked);
        });
    }

    private void Start(Animation baked, string clip, float blend, bool loop, bool upperBody, bool hold)
    {
        if (baked == null) return;
        var animationName = Install(baked, clip, loop, hold);
        LastActionLength = (float)baked.Length;
        ActionSerial++;
        _action.Animation = animationName;
        _oneShot.FadeInTime = Math.Max(0, blend);
        _oneShot.FadeOutTime = hold || loop ? 0 : Math.Max(0, blend);
        _oneShot.FilterEnabled = upperBody && !hold && _owner.Speed > 0.2f;
        _actionActive = true;
        _actionUpperBody = upperBody;
        _actionHolds = hold;
        _actionEndsAt = loop || hold
            ? ulong.MaxValue
            : Time.GetTicksMsec() + (ulong)(Math.Max(0, baked.Length - blend) * 1000.0);
        _tree.Set("parameters/one_shot/request", (int)AnimationNodeOneShot.OneShotRequest.Fire);
    }

    private string Install(Animation baked, string clip, bool loop, bool hold)
    {
        var key = $"{clip}|{loop}|{hold}";
        if (_installed.TryGetValue(key, out var existing))
            return existing;
        var copy = (Animation)baked.Duplicate();
        copy.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        // A non-looping AnimationTree input returns to its base when its length expires. Extending death keeps the
        // final keys sampled, so the corpse holds the last pose without replaying the fall.
        if (hold)
            copy.Length = Math.Max(copy.Length, 24 * 60 * 60);
        var name = DynamicLibraryPrefix + _installed.Count;
        _library.AddAnimation(name, copy);
        _installed[key] = name;
        return name;
    }

    private void ConfigureUpperBodyFilter()
    {
        var spine = -1;
        for (var i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            var n = _skeleton.GetBoneName(i).ToString();
            if (n.Contains("spine", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("chest", StringComparison.OrdinalIgnoreCase))
            {
                spine = i;
                break;
            }
        }
        if (spine < 0)
            return;
        for (var i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            var ancestor = i;
            while (ancestor >= 0 && ancestor != spine)
                ancestor = _skeleton.GetBoneParent(ancestor);
            if (ancestor == spine)
                _oneShot.SetFilterPath(new NodePath($"Skeleton:{_skeleton.GetBoneName(i)}"), true);
        }
    }

    private string Pick(params string[] names) =>
        names.FirstOrDefault(name => _library.HasAnimation(name));
}

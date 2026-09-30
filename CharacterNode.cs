using Godot;
using AAEmu.GodotViewer.Net;

namespace AAEmu.GodotViewer;

/// <summary>
/// An animated character: a Skeleton3D ("Skeleton") played by an AnimationPlayer whose library holds the character's
/// baked "idle", "walk" and "run" clips (in place, root motion removed). Set <see cref="Speed"/> every frame; the clip
/// follows it with a short cross-fade. Origin at the feet, facing -Z (Godot forward), like the CryEngine model facing +Y.
/// </summary>
public partial class CharacterNode : Node3D
{
    private const float FadeSeconds = 0.2f;

    public override void _ExitTree() => WorldSoundService.Shared?.ForgetCharacter(this);

    /// <summary>Horizontal speed in metres per second, picks the locomotion clip.</summary>
    public float Speed { get; set; }

    /// <summary>Current full-body locomotion presentation; combat one-shots remain layered over it.</summary>
    public MovementPose MovementPose { get; set; }

    internal Skeleton3D Skeleton;
    internal AnimationPlayer Animations;
    internal CharacterAnimationGraph AnimationGraph;
    /// <summary>Pak path of the character's .cal (animation list); its $AnimEventDatabase holds the clips' effect events.</summary>
    internal string AnimationListPath = "";

    private string _current;
    private bool _frozen;
    private string _stageIdle = "";
    private string _stageFidget = "";
    private double _nextStageFidget;
    private bool _revealAfterAnimation;
    private double _revealDeadline;
    private readonly Random _stageRandom = new();
    public string CurrentAnimation => AnimationGraph?.CurrentAnimation ?? _current ?? "none";

    public override void _Process(double delta)
    {
        WorldSoundService.Shared?.TickCharacter(this, delta);
        // A hidden lobby character must never stay hidden: a missing clip or a frozen graph would otherwise leave it invisible.
        if (_revealDeadline > 0 && Time.GetTicksMsec() / 1000.0 >= _revealDeadline)
        {
            _revealDeadline = 0;
            _revealAfterAnimation = false;
            Visible = true;
        }
        if (Animations == null || _frozen)
        {
            if (_frozen && _revealAfterAnimation)
            {
                _revealAfterAnimation = false;
                _revealDeadline = 0;
                Visible = true;
            }
            return;
        }
        if (AnimationGraph != null)
        {
            AnimationGraph.Update(Speed, MovementPose);
            if (_revealAfterAnimation)
            {
                _revealAfterAnimation = false;
                _revealDeadline = 0;
                Visible = true;
            }
            if (_stageIdle.Length > 0 && _stageFidget.Length > 0 && Time.GetTicksMsec() / 1000.0 >= _nextStageFidget)
            {
                if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
                    GD.Print($"[x2] stage fidget {_stageFidget} at {Time.GetTicksMsec() / 1000.0:F1} s");
                AnimationGraph.PlayAction(_stageFidget, FadeSeconds, loop: false, fullBody: true,
                    started: () =>
                    {
                        // The clip is long (fist_ac_stage_rand_1 runs 18 s): the pause before the next one counts from its end,
                        // otherwise the next fidget would cut the running one off mid-clip.
                        _nextStageFidget = Time.GetTicksMsec() / 1000.0 + AnimationGraph.LastActionLength + _stageRandom.Next(6, 13);
                        if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
                            GD.Print($"[x2] stage fidget started, clip {AnimationGraph.LastActionLength:F2} s at {Time.GetTicksMsec() / 1000.0:F1} s, next at {_nextStageFidget:F1} s");
                    });
                _nextStageFidget = Time.GetTicksMsec() / 1000.0 + _stageRandom.Next(6, 13);
            }
            return;
        }
        // In-air poses use dedicated clips when the model carries them (Space Bunny does); Pick returns
        // null for models without them, falling through to the speed-based locomotion below.
        var wanted = MovementPose switch
        {
            MovementPose.JumpRise => Pick("jump"),
            MovementPose.Fall => Pick("fall", "jump"),
            _ => null,
        } ?? (Speed > 3.5f ? Pick("run", "walk", "idle")
            : Speed > 0.2f ? Pick("walk", "run", "idle")
            : Pick("idle", "walk", "run"));
        if (wanted == null || wanted == _current)
            return;
        Animations.Play(wanted, _current == null ? 0 : FadeSeconds);
        _current = wanted;
    }

    /// <summary>Lazy-loads and plays a .cal clip name over locomotion.</summary>
    public void PlayAction(string clip, float blend = FadeSeconds, bool loop = false) =>
        AnimationGraph?.PlayAction(clip, blend, loop);

    /// <summary>Plays a full-body action instead of the upper-body combat layer.</summary>
    public void PlayPresentationAction(string clip, float blend = FadeSeconds, bool loop = false, Action started = null) =>
        AnimationGraph?.PlayAction(clip, blend, loop, fullBody: true, started);

    /// <summary>Keep a newly built lobby character hidden until its first CAL action has installed.</summary>
    public void RevealWhenPresentationReady() => _revealAfterAnimation = true;

    /// <summary>
    /// Hides a freshly built lobby character until its first presentation clip has been applied (no T-pose flash); it is shown at the
    /// latest after <paramref name="timeout"/> seconds even if no clip ever loads.
    /// </summary>
    public void HideUntilPresentationReady(float timeout = 1.5f)
    {
        Visible = false;
        _revealDeadline = Time.GetTicksMsec() / 1000.0 + timeout;
    }

    /// <summary>Plays a cast start once and then holds the cast loop until <see cref="EndCastLoop"/>.</summary>
    public void PlayCastLoop(string startClip, string loopClip, float blend = FadeSeconds) =>
        AnimationGraph?.PlayCastLoop(startClip, loopClip, blend);

    /// <summary>Runs a full-body start clip and then holds its paired loop clip.</summary>
    public void PlayPresentationLoop(string startClip, string loopClip, float blend = FadeSeconds, Action started = null) =>
        AnimationGraph?.PlayCastLoop(startClip, loopClip, blend, fullBody: true, started);

    public void PlayPresentationSequence(IReadOnlyList<string> clips, bool loopLast = true, Action started = null,
        Action reachedLoop = null) =>
        AnimationGraph?.PlayPresentationSequence(clips, loopLast, FadeSeconds, started, reachedLoop);

    public void SetFrozen(bool frozen)
    {
        // The lobby applies its state every frame; only a real change may restart the fidget timer, or it never elapses.
        if (_frozen == frozen) return;
        _frozen = frozen;
        AnimationGraph?.SetFrozen(frozen);
        if (!frozen && _stageIdle.Length > 0)
            _nextStageFidget = Time.GetTicksMsec() / 1000.0 + _stageRandom.Next(6, 13);
    }

    public void EnableStageFidgets(string idle, string fidget)
    {
        _stageIdle = idle;
        _stageFidget = fidget;
        AnimationGraph?.SetCombatIdle(idle);
        _nextStageFidget = Time.GetTicksMsec() / 1000.0 + _stageRandom.Next(6, 13);
    }

    public void DisableStageFidgets()
    {
        _stageIdle = _stageFidget = "";
        AnimationGraph?.SetCombatIdle("");
    }

    /// <summary>Stops the current cast loop and plays its end clip once.</summary>
    public void EndCastLoop(string endClip, float blend = FadeSeconds) =>
        AnimationGraph?.EndCastLoop(endClip, blend);

    /// <summary>Replaces idle locomotion with a lazy-loaded combat stance. Pass an empty name to clear it.</summary>
    public void SetCombatIdle(string clip) => AnimationGraph?.SetCombatIdle(clip);

    /// <summary>Plays a full-body death animation once and keeps its final pose.</summary>
    public void PlayDeath(string clip, float blend = FadeSeconds) =>
        AnimationGraph?.PlayDeath(clip, blend);

    /// <summary>Fades the current action out and returns to locomotion.</summary>
    public void StopAction(float blend = FadeSeconds) => AnimationGraph?.StopAction(blend);

    public void ApplyMovement(UnitMovement movement, bool wasGrounded)
    {
        Speed = MovementPresentation.HorizontalSpeed(movement);
        MovementPose = MovementPresentation.FromUnitMovement(movement, wasGrounded);
    }

    public void SetMovementPose(MovementPose pose) => MovementPose = pose;

    private string Pick(params string[] names) => names.FirstOrDefault(name => Animations.HasAnimation(name));
}

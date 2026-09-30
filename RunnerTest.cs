using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer;

/// <summary>
/// Headless smoke test for the Space Bunny runner: attaches the model to a real <see cref="PlayerController"/>,
/// drives it through idle/walk/run/jump/fall poses and an auto-run + physical jump, and prints
/// "RUNNERTEST name=pass/fail" lines with a final RESULT and a matching exit code.
/// Run: godot --headless --path . res://RunnerTest.tscn
/// </summary>
public partial class RunnerTest : Node3D
{
    private PlayerController _player;
    private CharacterNode _bunny;
    private double _t;
    private bool _stepsDone;
    private bool _airborneSeen;
    private bool _landedSeen;
    private Vector3 _start;
    private bool _spaceSent, _spaceReleased;

    private bool _okAttached, _okClips, _okIdle, _okRun, _okWalk, _okJump, _okFall, _okBackIdle,
        _okMoved, _okAirborne, _okLanded;

    public override void _Ready()
    {
        if (!SpaceBunnyRunner.Available)
        {
            // the placeholder model is a local file that is not in the repository
            GD.Print($"RUNNERTEST RESULT=SKIPPED ({SpaceBunnyRunner.ScenePath} is not in this checkout)");
            GetTree().Quit(0);
            return;
        }
        var ground = new StaticBody3D { Name = "Ground" }; // default layer 1: PlayerController's default mask collides with it
        ground.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(60, 1, 60) },
            Position = new Vector3(0, -0.5f, 0),
        });
        AddChild(ground);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, 35, 0) });
        _player = new PlayerController { Name = "Player" };
        AddChild(_player);
        _bunny = SpaceBunnyRunner.Build();
        if (_bunny != null)
            _player.SetModel(_bunny);
        _player.Teleport(new NVector3(0, 0, 0), 0);
        _start = _player.GlobalPosition;
    }

    public override void _Process(double delta)
    {
        if (_player == null)
            return; // skipped: no placeholder model in this checkout
        _t += delta;
        if (_player.MovementPose is MovementPose.JumpRise or MovementPose.Fall && !_player.OnGround)
            _airborneSeen = true;
        if (_airborneSeen && _player.OnGround)
            _landedSeen = true;

        // Phase 1: clip selection straight on the CharacterNode. Speed is re-asserted every frame
        // because PlayerController._PhysicsProcess overwrites it from the (zero) movement velocity.
        if (_t > 1.0 && !_okIdle)
        {
            Report("attached", _okAttached = _bunny != null && IsAncestorOf(_bunny) && ReferenceEquals(_player.GetNodeOrNull("SpaceBunny"), _bunny), _bunny?.CurrentAnimation ?? "null");
            var clips = _bunny?.Animations != null && new[] { "idle", "walk", "run", "jump", "fall" }.All(c => _bunny.Animations.HasAnimation(c));
            Report("clips", _okClips = clips, "idle/walk/run/jump/fall");
            Report("idle", _okIdle = _bunny?.CurrentAnimation == "idle", _bunny?.CurrentAnimation ?? "null");
        }
        if (_t > 2.0 && _okIdle && !_okRun)
        {
            _bunny.Speed = 5.4f;
            _bunny.MovementPose = MovementPose.Run;
            if (_bunny.CurrentAnimation != "run")
            {
                Report("run", false, _bunny.CurrentAnimation);
                return;
            }
            Report("run", _okRun = true, _bunny.CurrentAnimation);
        }
        if (_t > 2.8 && _okRun && !_okWalk)
        {
            _bunny.Speed = 2.0f;
            _bunny.MovementPose = MovementPose.Walk;
            if (_bunny.CurrentAnimation != "walk")
            {
                Report("walk", false, _bunny.CurrentAnimation);
                return;
            }
            Report("walk", _okWalk = true, _bunny.CurrentAnimation);
            _bunny.Speed = 0f;
        }
        if (_t > 3.6 && _okWalk && !_okJump)
        {
            Report("jump-pose", _okJump = _bunny.CurrentAnimation == "jump", _bunny.CurrentAnimation);
            _bunny.MovementPose = MovementPose.Fall;
        }
        if (_t > 4.0 && _okJump && !_okFall)
        {
            Report("fall-pose", _okFall = _bunny.CurrentAnimation == "fall", _bunny.CurrentAnimation);
            _bunny.MovementPose = MovementPose.Idle;
        }
        if (_t > 4.4 && _okFall && !_okBackIdle)
        {
            Report("back-to-idle", _okBackIdle = _bunny.CurrentAnimation == "idle", _bunny.CurrentAnimation);
            _player.AutoRunSeconds = 3.0;
            _start = _player.GlobalPosition;
            _stepsDone = true;
        }

        // Phase 2: real physics — auto-run forward plus a pushed Space jump.
        if (_stepsDone && !_spaceSent && _t > 4.6)
            PushKey(Key.Space, true);
        if (_spaceSent && !_spaceReleased && _t > 4.8)
            PushKey(Key.Space, false);

        if (_t > 8.5 && _spaceReleased)
        {
            var moved = new Vector2(_player.GlobalPosition.X - _start.X, _player.GlobalPosition.Z - _start.Z).Length();
            Report("auto-run-distance", _okMoved = moved > 2f, $"{moved:F1} m");
            Report("airborne", _okAirborne = _airborneSeen, player: true);
            Report("landed", _okLanded = _landedSeen, player: true);
            var pass = _okAttached && _okClips && _okIdle && _okRun && _okWalk && _okJump && _okFall
                && _okBackIdle && _okMoved && _okAirborne && _okLanded;
            GD.Print($"RUNNERTEST RESULT={(pass ? "PASS" : "FAIL")}");
            GetTree().Quit(pass ? 0 : 1);
        }
    }

    private void PushKey(Key key, bool pressed)
    {
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = pressed });
        if (pressed) _spaceSent = true; else _spaceReleased = true;
    }

    private void Report(string name, bool ok, string detail = "", bool player = false)
    {
        var extra = player ? $"pose={_player.MovementPose} onGround={_player.OnGround} anim={_bunny?.CurrentAnimation}" : detail;
        GD.Print($"RUNNERTEST {name}={(ok ? "pass" : "FAIL")} {extra}");
    }
}

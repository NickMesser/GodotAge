using System.Collections.Concurrent;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>Viewer-copy smoke scene for one player, one NPC, lazy actions, cast loops and a held death pose.</summary>
public partial class AnimationPreviewTest : Node3D
{
    private static string GamePak => ClientPaths.Pak;
    private static string GameDb => ClientPaths.Database;
    private const long PlayerModel = 10;
    private const long NpcTemplate = 2; // Castle Guard, models.id 1641 (dw_male02.cdf)

    private readonly ConcurrentQueue<Action> _mainThread = new();
    private ModelLibrary _models;
    private CharacterBuilder _characters;
    private CharacterNode _player;
    private CharacterNode _npc;
    private Label _status;
    private bool _sequenceStarted;
    private bool _awaitingClip;
    private ulong _captureAt;
    private int _stage = -1;

    private static readonly (string Name, ActionType Type, string A, string B, int DelayMs)[] Stages =
    [
        ("combat_idle", ActionType.CombatIdle, "fist_ba_combat_idle", "", 800),
        ("bow_10499", ActionType.Action, "bow_co_sk_launch_2", "", 750),
        ("resurrection_10546", ActionType.Action, "all_co_sk_spell_launch_union", "", 750),
        ("channel_loop_10626", ActionType.CastLoop, "all_co_sk_spell_cast_spread", "all_co_sk_spell_channel_sorb", 1500),
        ("cast_end_transition", ActionType.CastEnd, "all_co_sk_spell_launch_union", "", 650),
        ("two_hand_10644", ActionType.Action, "twohand_co_sk_weapon_blunt_3_launch", "", 750),
        ("meteor_10664", ActionType.Action, "all_co_sk_spell_launch_meteor", "", 750),
        ("death", ActionType.Death, "all_re_combat_dead_start", "", 1400),
    ];

    private enum ActionType { CombatIdle, Action, CastLoop, CastEnd, Death }

    public override void _Ready()
    {
        RenderBudget.SetMainThread();
        SetupPreviewStage();
        _models = new ModelLibrary(useNormalMaps: false);
        _models.CreateDefaults();
        _status = new Label
        {
            Name = "TestStatus",
            Position = new Vector2(28, 24),
            Text = "Opening game pak and building Player 10 + NPC 2...",
        };
        _status.AddThemeFontSizeOverride("font_size", 22);
        _status.AddThemeColorOverride("font_color", Colors.White);
        _status.AddThemeColorOverride("font_outline_color", Colors.Black);
        _status.AddThemeConstantOverride("outline_size", 5);
        var canvas = new CanvasLayer();
        AddChild(canvas);
        canvas.AddChild(_status);

        _ = Task.Run(() =>
        {
            if (!PakFiles.Open(GamePak))
            {
                Post(() => _status.Text = $"Could not open {GamePak}");
                return;
            }
            if (!File.Exists(GameDb))
            {
                Post(() => _status.Text = $"Could not read {GameDb}");
                return;
            }
            _characters = new CharacterBuilder(_models, Post, GameDb);
            _characters.BuildPlayerDefault(PlayerModel, node =>
            {
                _player = node;
                _player.Position = new Vector3(-0.85f, 0, 0);
                AddChild(_player);
                GD.Print($"ANIMTEST player ready: model {PlayerModel}");
            });
            _characters.BuildNpc(NpcTemplate, node =>
            {
                _npc = node;
                _npc.Position = new Vector3(0.85f, 0, 0);
                AddChild(_npc);
                GD.Print($"ANIMTEST npc ready: template {NpcTemplate}");
            });
        });
    }

    public override void _Process(double delta)
    {
        RenderBudget.NewFrame(delta * 1000.0);
        while (_mainThread.TryDequeue(out var work))
            work();

        if (!_sequenceStarted)
        {
            if (_player == null || _npc == null)
                return;

            // Keep the player moving in place to exercise the moving upper-body action filter.
            _player.Speed = 4.0f;
            _npc.Speed = 0;
            _sequenceStarted = true;
            BeginStage(0);
            return;
        }

        if (_awaitingClip && Time.GetTicksMsec() >= _captureAt)
        {
            _awaitingClip = false;
            CaptureStage();
        }
    }

    public override void _ExitTree() => _characters?.Dispose();

    private void BeginStage(int index)
    {
        _stage = index;
        if (index >= Stages.Length)
        {
            GD.Print("ANIMTEST complete");
            GetTree().Quit();
            return;
        }

        var stage = Stages[index];
        _status.Text = $"Player 10 + NPC 2  |  {stage.Name}  |  {stage.A}";
        switch (stage.Type)
        {
            case ActionType.CombatIdle:
                _player.Speed = 0;
                _player.SetCombatIdle(stage.A);
                break;
            case ActionType.Action:
                if (_stage > 1)
                    _player.Speed = 4.0f;
                _player.PlayAction(stage.A, 0.12f, loop: false);
                break;
            case ActionType.CastLoop:
                _player.Speed = 0;
                _player.PlayCastLoop(stage.A, stage.B, 0.12f);
                break;
            case ActionType.CastEnd:
                _player.EndCastLoop(stage.A, 0.12f);
                break;
            case ActionType.Death:
                _player.PlayDeath(stage.A, 0.12f);
                break;
        }
        _awaitingClip = true;
        _captureAt = Time.GetTicksMsec() + (ulong)stage.DelayMs;
    }

    private void CaptureStage()
    {
        var stage = Stages[_stage];
        var directory = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "deliver", "screenshots"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{_stage:00}_{stage.Name}.png");
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        if (error != Error.Ok)
            GD.PrintErr($"ANIMTEST screenshot failed: {path} ({error})");
        else
            GD.Print($"ANIMTEST screenshot: {path}");
        BeginStage(_stage + 1);
    }

    private void Post(Action action) => _mainThread.Enqueue(action);

    private void SetupPreviewStage()
    {
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.10f, 0.14f, 0.19f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.72f, 0.78f, 0.88f),
            AmbientLightEnergy = 0.8f,
        };
        AddChild(new WorldEnvironment { Environment = environment });
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-36, 26, 0),
            LightEnergy = 1.25f,
            ShadowEnabled = true,
        });

        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(16, 12) },
            Position = new Vector3(0, -0.03f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.17f, 0.21f, 0.24f), Roughness = 0.95f },
        };
        AddChild(ground);

        var camera = new Camera3D
        {
            Name = "PreviewCamera",
            Current = true,
            Fov = 42,
            Near = 0.1f,
            Far = 100,
            Position = new Vector3(0, 1.35f, -5.8f),
        };
        AddChild(camera);
        camera.LookAt(new Vector3(0, 1.0f, 0), Vector3.Up);
    }
}

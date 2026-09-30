#nullable enable
using Godot;
using AAEmu.GodotViewer;

namespace AAEmu.GodotViewer.Client;

/// <summary>Standalone visual/input demo. It does not create or use a network client.</summary>
public partial class TargetingDemo : Node3D
{
    private readonly DemoRegistry _registry = new();
    private TargetingController? _targeting;
    private Camera3D? _camera;
    private int _captureFrames = -1;

    public override void _Ready()
    {
        if (System.IO.File.Exists(ClientPaths.Pak))
            PakFiles.Open(ClientPaths.Pak);

        CreateWorld();
        CreateTargets();
        CreateTargeting();
        CreateOverlay();

        _targeting!.Select(1);
        if (OS.GetCmdlineUserArgs().Contains("--targeting-capture"))
            _captureFrames = 12;
    }

    public override void _Process(double delta)
    {
        if (_captureFrames < 0 || --_captureFrames > 0)
            return;
        _captureFrames = -1;
        var image = GetViewport().GetTexture().GetImage();
        var path = ProjectSettings.GlobalizePath("res://targeting-demo.png");
        var error = image.SavePng(path);
        GD.Print($"TARGETING_CAPTURE path={path} size={image.GetWidth()}x{image.GetHeight()} error={error}");
        GetTree().Quit(error == Error.Ok ? 0 : 1);
    }

    private void CreateWorld()
    {
        RenderingServer.SetDefaultClearColor(new Color(0.025f, 0.045f, 0.075f));
        var environment = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.035f, 0.07f, 0.12f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.43f, 0.52f, 0.68f),
                AmbientLightEnergy = 0.65f,
            },
        };
        AddChild(environment);
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52f, -25f, 0f),
            LightColor = new Color(1f, 0.88f, 0.70f),
            LightEnergy = 1.4f,
            ShadowEnabled = true,
        });

        var groundMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.075f, 0.12f, 0.13f),
            Roughness = 0.92f,
        };
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(45f, 55f), Material = groundMaterial },
        });

        _camera = new Camera3D
        {
            Name = "Camera",
            Position = new Vector3(0f, 8.2f, 17f),
            Current = true,
            Fov = 58f,
        };
        AddChild(_camera);
        _camera.LookAt(new Vector3(0f, 1.7f, -8f), Vector3.Up);
    }

    private void CreateTargets()
    {
        var relations = new[]
        {
            TargetRelation.Hostile, TargetRelation.Neutral, TargetRelation.Friendly, TargetRelation.Party,
        };
        var labels = new[] { "Crimson Watch", "Wandering Merchant", "Nuian Scout", "Party Vanguard" };
        var id = 1u;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 5; column++)
        {
            var relation = relations[(row + column) % relations.Length];
            var height = 1.65f + ((row * 5 + column) % 3) * 0.25f;
            var position = new Vector3((column - 2) * 3.1f + (row % 2) * 0.6f, 0f, -2.5f - row * 5.1f);
            var material = new StandardMaterial3D
            {
                AlbedoColor = TargetingVisuals.RelationColor(relation).Darkened(0.28f),
                Metallic = 0.12f,
                Roughness = 0.58f,
            };
            var node = new Node3D
            {
                Name = $"Dummy{id}",
                Position = position,
            };
            node.AddChild(new MeshInstance3D
            {
                Name = "Body",
                Position = Vector3.Up * height * 0.5f,
                Mesh = new CapsuleMesh { Radius = 0.48f, Height = height, Material = material },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            });
            AddChild(node);
            _registry.Items.Add(new DemoTarget
            {
                Id = id,
                Node = node,
                Name = $"{labels[(int)((id - 1) % labels.Length)]} {id:00}",
                Kind = id % 6 == 0 ? TargetKind.Mate : id % 5 == 0 ? TargetKind.Player : TargetKind.Npc,
                Relation = relation,
                Height = height,
                GuildOrTitle = relation == TargetRelation.Party ? "<Dawn Company>" : row == 0 ? "Lv. 55" : string.Empty,
                HealthFraction = 0.28f + ((id * 17) % 68) / 100f,
                TargetId = id == 1 ? 4u : null,
            });
            id++;
        }
    }

    private void CreateTargeting()
    {
        _targeting = new TargetingController
        {
            Name = "TargetingController",
            Registry = _registry,
            Camera = _camera,
            ResolveSelf = () => _registry.Items[2],
            ResolvePartyMember = index => _registry.Items.Where(t => t.Relation == TargetRelation.Party).ElementAtOrDefault(index),
            MaxNameplates = 20,
            NameplateUpdatesPerFrame = 20,
        };
        AddChild(_targeting);
    }

    private void CreateOverlay()
    {
        var panel = new ColorRect
        {
            Position = new Vector2(22f, 22f),
            Size = new Vector2(420f, 118f),
            Color = new Color(0.015f, 0.025f, 0.04f, 0.86f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var label = new Label
        {
            Position = new Vector2(42f, 36f),
            Text = "TARGETING DEMO\nTab / Shift+Tab   Cycle hostiles\nLeft click   Select      Esc   Clear\nF1   Self      F2-F5   Party",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_color", new Color(0.88f, 0.94f, 1f));
        label.AddThemeFontSizeOverride("font_size", 18);
        AddChild(panel);
        AddChild(label);
    }

    private sealed class DemoRegistry : ITargetableRegistry
    {
        public List<DemoTarget> Items { get; } = [];
        public IEnumerable<ITargetable> All => Items;
    }

    private sealed class DemoTarget : ITargetable, ITargetableNameplate, ITargetOfTargetSource
    {
        public uint Id { get; init; }
        public required Node3D Node { get; init; }
        public required string Name { get; init; }
        public TargetKind Kind { get; init; }
        public TargetRelation Relation { get; init; }
        public bool Dead { get; init; }
        public float Height { get; init; }
        public Aabb? Bounds => null;
        public string GuildOrTitle { get; init; } = string.Empty;
        public float HealthFraction { get; init; } = 1f;
        public uint? TargetId { get; init; }
    }
}

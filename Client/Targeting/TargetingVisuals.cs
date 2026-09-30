#nullable enable
using Godot;
using AAEmu.GodotViewer;

namespace AAEmu.GodotViewer.Client;

/// <summary>Pooled, bounded-cost target ring and world nameplate renderer.</summary>
internal partial class TargetingVisuals : Node3D
{
    public required TargetingController Controller { get; init; }

    private readonly List<Nameplate> _pool = [];
    private MeshInstance3D? _ring;
    private StandardMaterial3D? _ringMaterial;
    private Font? _font;
    private Texture2D? _white;
    private readonly Dictionary<TargetRelation, Texture2D> _ringTextures = [];
    private int _cursor;
    private int _framesUntilSync;
    private uint _ringTargetId;

    public override void _Ready()
    {
        TopLevel = true;
        _font = LoadOriginalFont();
        _white = CreateWhiteTexture();
        CreateRing();
        SyncAssignments();
    }

    public override void _Process(double delta)
    {
        UpdateRing();
        if (--_framesUntilSync <= 0)
        {
            SyncAssignments();
            _framesUntilSync = 15;
        }
        if (_pool.Count == 0)
            return;

        var count = Math.Min(Controller.NameplateUpdatesPerFrame, _pool.Count);
        for (var i = 0; i < count; i++)
        {
            _cursor %= _pool.Count;
            UpdateNameplate(_pool[_cursor++]);
        }
    }

    private void CreateRing()
    {
        var fallback = CreateFallbackRing();
        _ringTextures[TargetRelation.Hostile] = LoadPakTexture("game/textures/decal/target/target_enemy04_red.dds") ?? fallback;
        _ringTextures[TargetRelation.Neutral] = LoadPakTexture("game/textures/decal/target/target_enemy04_yellow.dds") ?? fallback;
        _ringTextures[TargetRelation.Friendly] = LoadPakTexture("game/textures/decal/target/target_pointer_ring.dds") ?? fallback;
        _ringTextures[TargetRelation.Party] = LoadPakTexture("game/textures/decal/target/target_enemy04_green.dds") ?? fallback;
        _ringMaterial = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = _ringTextures[TargetRelation.Hostile],
            AlbedoColor = Colors.White,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = false,
            VertexColorUseAsAlbedo = true,
        };
        _ring = new MeshInstance3D
        {
            Name = "SelectionRing",
            Mesh = new QuadMesh { Size = new Vector2(2.5f, 2.5f) },
            MaterialOverride = _ringMaterial,
            RotationDegrees = new Vector3(-90f, 0f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_ring);
    }

    private void UpdateRing()
    {
        if (_ring == null || _ringMaterial == null)
            return;
        var target = Controller.Target;
        if (target == null || !GodotObject.IsInstanceValid(target.Node) || !target.Node.IsInsideTree())
        {
            _ring.Visible = false;
            _ringTargetId = 0;
            return;
        }

        _ring.Visible = true;
        _ring.GlobalPosition = target.Node.GlobalPosition + Vector3.Up * 0.035f;
        if (_ringTargetId == target.Id)
            return;
        _ringTargetId = target.Id;
        var width = target.Bounds is { } bounds
            ? Mathf.Max(bounds.Size.X, bounds.Size.Z)
            : Mathf.Clamp(target.Height * 0.9f, 1.5f, 4.5f);
        ((QuadMesh)_ring.Mesh).Size = Vector2.One * Mathf.Max(1.5f, width * 1.35f);
        _ringMaterial.AlbedoTexture = _ringTextures[target.Relation];
        _ringMaterial.AlbedoColor = RelationColor(target.Relation, target.Dead) with { A = 0.92f };
    }

    private void SyncAssignments()
    {
        var registry = Controller.Registry;
        var camera = Controller.Camera;
        if (registry == null || camera == null)
            return;

        var maxSq = Controller.MaxNameplateRange * Controller.MaxNameplateRange;
        var targetId = Controller.Target?.Id;
        var chosen = registry.All
            // The original client draws name tags for units, not for doodads (their names show in the tooltip).
            .Where(t => t.Kind != TargetKind.Doodad || t.Id == targetId)
            .Where(t => t.Node != null && GodotObject.IsInstanceValid(t.Node) && t.Node.IsInsideTree())
            .Select(t => (Target: t, DistanceSq: (TargetingController.TargetCenter(t) - camera.GlobalPosition).LengthSquared()))
            .Where(x => x.DistanceSq <= maxSq)
            .OrderByDescending(x => x.Target.Id == targetId)
            .ThenBy(x => x.DistanceSq)
            .Take(Controller.MaxNameplates)
            .Select(x => x.Target)
            .ToList();

        while (_pool.Count < chosen.Count)
            _pool.Add(CreateNameplate());
        for (var i = 0; i < _pool.Count; i++)
        {
            _pool[i].Target = i < chosen.Count ? chosen[i] : null;
            _pool[i].Root.Visible = i < chosen.Count;
            if (i < chosen.Count)
                ApplyStaticNameplateData(_pool[i], chosen[i]);
        }
    }

    private Nameplate CreateNameplate()
    {
        var root = new Node3D { Name = $"Nameplate{_pool.Count:00}", TopLevel = true };
        var title = new Label3D
        {
            Name = "Title",
            Font = _font,
            FontSize = 18,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            OutlineSize = 5,
            OutlineModulate = new Color(0f, 0f, 0f, 0.9f),
            Position = new Vector3(0f, 0.23f, 0f),
            NoDepthTest = true,
        };
        var name = new Label3D
        {
            Name = "Name",
            Font = _font,
            FontSize = 24,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            OutlineSize = 6,
            OutlineModulate = new Color(0f, 0f, 0f, 0.95f),
            NoDepthTest = true,
        };
        var hpBack = MakeBar("HpBack", new Color(0.035f, 0.035f, 0.04f, 0.9f), -0.29f);
        var hpFill = MakeBar("HpFill", new Color(0.78f, 0.08f, 0.06f, 0.96f), -0.29f);
        root.AddChild(title);
        root.AddChild(name);
        root.AddChild(hpBack);
        root.AddChild(hpFill);
        AddChild(root);
        return new Nameplate(root, name, title, hpBack, hpFill);
    }

    private Sprite3D MakeBar(string name, Color color, float y) => new()
    {
        Name = name,
        Texture = _white,
        Modulate = color,
        Position = new Vector3(0f, y, 0f),
        PixelSize = 0.01f,
        Scale = new Vector3(90f, 7f, 1f),
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        NoDepthTest = true,
        RenderPriority = name == "HpFill" ? 2 : 1,
    };

    private static void ApplyStaticNameplateData(Nameplate plate, ITargetable target)
    {
        // Names and titles from the DB may only exist in Korean; the shared translator holds their English.
        // Only write on change: every Label3D text or modulate write rebuilds its mesh.
        var translator = AAEmu.GodotViewer.Ui.X2.UiTranslator.Shared;
        var name = translator.Translate(target.Name);
        if (plate.Name.Text != name)
            plate.Name.Text = name;
        var title = translator.Translate(target is ITargetableNameplate data ? data.GuildOrTitle : string.Empty);
        if (plate.Title.Text != title)
        {
            plate.Title.Text = title;
            plate.Title.Visible = !string.IsNullOrWhiteSpace(title);
        }
    }

    private void UpdateNameplate(Nameplate plate)
    {
        var target = plate.Target;
        var camera = Controller.Camera;
        if (target == null || camera == null || !GodotObject.IsInstanceValid(target.Node) || !target.Node.IsInsideTree())
        {
            plate.Root.Visible = false;
            return;
        }

        plate.Root.Visible = true;
        plate.Root.GlobalPosition = target.Node.GlobalPosition + Vector3.Up * Mathf.Max(0.4f, target.Height + 0.22f);
        var distance = plate.Root.GlobalPosition.DistanceTo(camera.GlobalPosition);
        var scale = Mathf.Clamp(0.78f + distance * 0.022f, 0.9f, 1.65f);
        plate.Root.Scale = Vector3.One * scale;
        var fadeStart = Controller.MaxNameplateRange * 0.7f;
        var alpha = 1f - Mathf.Clamp((distance - fadeStart) / Mathf.Max(1f, Controller.MaxNameplateRange - fadeStart), 0f, 1f);
        var color = RelationColor(target.Relation, target.Dead);
        if (plate.NameColor != color)
        {
            plate.NameColor = color;
            plate.Name.Modulate = color;
        }
        // Fade through the per-instance transparency, which needs no mesh rebuild; quantised to skip tiny changes.
        var fade = Mathf.Round((1f - alpha) * 32f) / 32f;
        if (fade != plate.Fade)
        {
            plate.Fade = fade;
            plate.Name.Transparency = fade;
            plate.Title.Transparency = fade;
            plate.HpBack.Transparency = fade;
            plate.HpFill.Transparency = fade;
        }

        var showHealth = ReferenceEquals(target, Controller.Target) || target.Relation == TargetRelation.Hostile;
        if (plate.ShowHealth != showHealth)
        {
            plate.ShowHealth = showHealth;
            plate.HpBack.Visible = showHealth;
            plate.HpFill.Visible = showHealth;
        }
        if (showHealth)
        {
            var health = target is ITargetableNameplate data ? Mathf.Clamp(data.HealthFraction, 0f, 1f) : 1f;
            health = Mathf.Round(health * 90f) / 90f;
            if (health != plate.Health)
            {
                plate.Health = health;
                plate.HpFill.Scale = new Vector3(90f * health, 7f, 1f);
                plate.HpFill.Position = new Vector3(-0.45f * (1f - health), -0.29f, -0.002f);
            }
        }
    }

    internal static Color RelationColor(TargetRelation relation, bool dead = false)
    {
        if (dead)
            return new Color(0.56f, 0.56f, 0.59f);
        return relation switch
        {
            TargetRelation.Hostile => new Color(1f, 0.19f, 0.14f),
            TargetRelation.Neutral => new Color(1f, 0.84f, 0.18f),
            TargetRelation.Friendly => new Color(0.20f, 0.82f, 0.96f),
            TargetRelation.Party => new Color(0.25f, 1f, 0.36f),
            _ => Colors.White,
        };
    }

    private static Texture2D? LoadPakTexture(string path)
    {
        try
        {
            var bytes = PakFiles.Read(path);
            if (bytes == null)
                return null;
            var image = new Image();
            return image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) == Error.Ok && !image.IsEmpty()
                ? ImageTexture.CreateFromImage(image)
                : null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Target texture '{path}' could not be loaded: {exception.Message}");
            return null;
        }
    }

    private static Font LoadOriginalFont()
    {
        var bytes = PakFiles.Read("game/ui/font/roboto.ttf") ?? PakFiles.Read("game/ui/font/tahoma_regular_font.ttf");
        return bytes == null
            ? ThemeDB.FallbackFont
            : new FontFile
            {
                Data = bytes,
                Antialiasing = TextServer.FontAntialiasing.Gray,
                Hinting = TextServer.Hinting.Light,
                SubpixelPositioning = TextServer.SubpixelPositioning.Disabled,
            };
    }

    private static Texture2D CreateWhiteTexture()
    {
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        image.SetPixel(0, 0, Colors.White);
        return ImageTexture.CreateFromImage(image);
    }

    private static Texture2D CreateFallbackRing()
    {
        const int size = 128;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = (x + 0.5f) / size * 2f - 1f;
            var dy = (y + 0.5f) / size * 2f - 1f;
            var distance = Mathf.Sqrt(dx * dx + dy * dy);
            var alpha = Mathf.Clamp(1f - Mathf.Abs(distance - 0.78f) * 22f, 0f, 1f);
            image.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private sealed record Nameplate(
        Node3D Root,
        Label3D Name,
        Label3D Title,
        Sprite3D HpBack,
        Sprite3D HpFill)
    {
        public ITargetable? Target { get; set; }
        // Last values written to the nodes. Label3D rebuilds its text mesh on every modulate change, so writes are
        // skipped unless something actually changed (writing them every frame cost ~15 ms per frame on an iGPU).
        public Color? NameColor;
        public float Fade = -1f;
        public float Health = -1f;
        public bool? ShowHealth;
    }
}

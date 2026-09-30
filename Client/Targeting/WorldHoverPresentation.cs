#nullable enable

using Godot;

namespace AAEmu.GodotViewer.Client.Targeting;

/// <summary>Extra world-hover facts supplied by live unit views.</summary>
public interface ITargetableHoverInfo
{
    int Level { get; }
    bool HasDialogue { get; }
    bool CanLoot { get; }
    DoodadInteractionKind Interaction { get; }
}

/// <summary>Hover cursors, cursor visibility and a compact unit name/level tooltip.</summary>
public partial class WorldHoverPresentation : CanvasLayer
{
    private const string FontPath = "game/ui/font/yd_ygo540.ttf";
    private const float MaxHoverDistance = 50f;

    // The PE resources have numeric IDs only; these role assignments follow their artwork.
    private static readonly IReadOnlyDictionary<HoverCursor, (string Path, Vector2? Hotspot)> CursorAssets = new Dictionary<HoverCursor, (string, Vector2?)>
    {
        [HoverCursor.Normal] = ("res://Assets/Cursors/cursor-1-lang-0.png", Vector2.Zero),
        [HoverCursor.Attack] = ("res://Assets/Cursors/cursor-10-lang-1042.png", Vector2.Zero),
        [HoverCursor.Talk] = ("res://Assets/Cursors/cursor-8-lang-1042.png", Vector2.Zero),
        [HoverCursor.Loot] = ("res://Assets/Cursors/cursor-12-lang-1042.png", Vector2.Zero),
        [HoverCursor.Work] = ("res://Assets/Cursors/cursor-15-lang-1042.png", Vector2.Zero),
        [HoverCursor.Cannot] = ("res://Assets/Cursors/cursor-26-lang-1042.png", Vector2.Zero),
        [HoverCursor.Rotate] = ("game/ui/cursor/housing_rotation.dds", null),
        [HoverCursor.Use] = ("res://Assets/Cursors/cursor-6-lang-1042.png", Vector2.Zero),
    };

    private readonly Dictionary<HoverCursor, Texture2D?> _cursors = [];
    private Control? _root;
    private PanelContainer? _tooltipPanel;
    private Label? _tooltipLabel;
    private Font? _font;
    private HoverCursor _currentCursor = (HoverCursor)(-1);
    private uint? _lastId;
    private ITargetable? _hovered;

    public TargetingController? Targeting { get; set; }
    public ITargetableRegistry? Registry { get; set; }
    public Camera3D? Camera { get; set; }
    public OrbitCamera? OrbitCamera { get; set; }
    public LoadingScreenOverlay? LoadingScreen { get; set; }

    public ITargetable? Hovered => _hovered;

    public override void _Ready()
    {
        Layer = 40;
        BuildTooltip();
        LoadCursors();
        SetCursor(HoverCursor.Normal);
    }

    public override void _Process(double delta)
    {
        if (LoadingScreen?.Visible == true || Camera == null || Targeting == null || Registry == null ||
            OrbitCamera?.IsRightMouseHeld == true)
        {
            SetHover(null);
            SetCursor(HoverCursor.Normal);
            return;
        }

        if (OrbitCamera?.IsLeftMouseHeld == true)
        {
            SetHover(null);
            SetCursor(HoverCursor.Rotate);
            return;
        }

        var guiHover = GetViewport()?.GuiGetHoveredControl();
        if (guiHover is { Visible: true } && guiHover.MouseFilter != Control.MouseFilterEnum.Ignore)
        {
            SetHover(null);
            SetCursor(HoverCursor.Normal);
            return;
        }

        var target = Targeting.Pick(GetViewport().GetMousePosition());
        if (target != null && Camera.GlobalPosition.DistanceTo(TargetingController.TargetCenter(target)) > MaxHoverDistance)
            target = null;

        SetHover(target);
        SetCursor(CursorFor(target));
        PlaceTooltip(GetViewport().GetMousePosition());
    }

    /// <summary>Loads original client cursor resources plus the pak cursor used while orbiting.</summary>
    public void LoadCursors()
    {
        if (PakFiles.Read(FontPath) is { } fontBytes)
            _font = new FontFile { Data = fontBytes, Antialiasing = TextServer.FontAntialiasing.Gray };

        foreach (var (kind, asset) in CursorAssets)
        {
            if (_cursors.ContainsKey(kind))
                continue;
            _cursors[kind] = LoadCursor(asset.Path);
        }
        if (_tooltipLabel != null && _font != null)
            _tooltipLabel.AddThemeFontOverride("font", _font);
    }

    private void BuildTooltip()
    {
        _root = new Control { Name = "WorldHoverRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.025f, 0.02f, 0.015f, 0.9f),
            BorderColor = new Color(0.44f, 0.32f, 0.19f, 0.95f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ContentMarginLeft = 8,
            ContentMarginTop = 5,
            ContentMarginRight = 8,
            ContentMarginBottom = 5,
        };
        _tooltipPanel = new PanelContainer { Name = "HoverTooltip", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _tooltipPanel.AddThemeStyleboxOverride("panel", style);
        _root.AddChild(_tooltipPanel);
        _tooltipLabel = new Label
        {
            Name = "NameAndLevel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _tooltipLabel.AddThemeColorOverride("font_color", new Color(1f, 0.91f, 0.72f));
        _tooltipLabel.AddThemeColorOverride("font_outline_color", new Color(0.03f, 0.02f, 0.01f, 1f));
        _tooltipLabel.AddThemeFontSizeOverride("font_size", 14);
        _tooltipLabel.AddThemeConstantOverride("outline_size", 2);
        _tooltipPanel.AddChild(_tooltipLabel);
    }

    private Texture2D? LoadCursor(string path)
    {
        if (path.StartsWith("res://", StringComparison.Ordinal))
            // Assets/Cursors is a local copy; without it the cursor is read from archeage.exe next to the pak, or the default cursor stays
            return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : ClientCursors.Load(path);
        if (PakFiles.Read(path) is not { } bytes)
            return null;
        var image = new Image();
        if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) != Error.Ok || image.IsEmpty())
            return null;
        return ImageTexture.CreateFromImage(image);
    }

    private void SetHover(ITargetable? target)
    {
        _hovered = target;
        if (_tooltipPanel == null || _tooltipLabel == null)
            return;
        if (target == null || target.Kind is not (TargetKind.Npc or TargetKind.Player or TargetKind.Mate or TargetKind.Vehicle))
        {
            _tooltipPanel.Visible = false;
            _lastId = null;
            return;
        }

        var info = target as ITargetableHoverInfo;
        var level = info?.Level ?? 0;
        var text = level > 0 ? $"{target.Name}\nLv. {level}" : target.Name;
        if (_lastId != target.Id || _tooltipLabel.Text != text)
            _tooltipLabel.Text = text;
        _lastId = target.Id;
        _tooltipPanel.Visible = !string.IsNullOrWhiteSpace(target.Name);
    }

    private void PlaceTooltip(Vector2 mouse)
    {
        if (_tooltipPanel is not { Visible: true } panel || _root == null)
            return;
        var viewport = GetViewport().GetVisibleRect().Size;
        var size = panel.GetCombinedMinimumSize();
        var position = mouse + new Vector2(18, 18);
        if (position.X + size.X > viewport.X) position.X = Mathf.Max(0, mouse.X - size.X - 12);
        if (position.Y + size.Y > viewport.Y) position.Y = Mathf.Max(0, mouse.Y - size.Y - 12);
        panel.Position = position;
    }

    private void SetCursor(HoverCursor cursor)
    {
        if (_currentCursor == cursor)
            return;
        _currentCursor = cursor;
        if (!_cursors.TryGetValue(cursor, out var texture) || texture == null)
        {
            Input.SetCustomMouseCursor(null);
            return;
        }
        var size = texture.GetSize();
        var hotspot = CursorAssets.TryGetValue(cursor, out var asset) && asset.Hotspot is { } configured
            ? configured : size.X >= 32 || size.Y >= 32 ? size / 2f : Vector2.Zero;
        Input.SetCustomMouseCursor(texture, Input.CursorShape.Arrow, hotspot);
    }

    private static HoverCursor CursorFor(ITargetable? target)
    {
        if (target == null)
            return HoverCursor.Normal;
        if (target.Relation == TargetRelation.Hostile && !target.Dead)
            return HoverCursor.Attack;
        if (target.Dead && target is ITargetableHoverInfo { CanLoot: true })
            return HoverCursor.Loot;
        if (target.Kind == TargetKind.Npc && target is ITargetableHoverInfo { HasDialogue: true })
            return HoverCursor.Talk;
        if (target is ITargetableHoverInfo hover)
            return hover.Interaction switch
            {
                DoodadInteractionKind.Loot => HoverCursor.Loot,
                DoodadInteractionKind.Gather or
                DoodadInteractionKind.Mine or
                DoodadInteractionKind.Log or
                DoodadInteractionKind.Fish => HoverCursor.Work,
                DoodadInteractionKind.Use => HoverCursor.Use,
                // No extracted resource has source-backed read semantics.
                DoodadInteractionKind.Read => HoverCursor.Normal,
                _ => target.Kind == TargetKind.Doodad ? HoverCursor.Cannot : HoverCursor.Normal,
            };
        return target.Kind == TargetKind.Doodad ? HoverCursor.Cannot : HoverCursor.Normal;
    }

    private enum HoverCursor { Normal, Attack, Talk, Loot, Work, Cannot, Rotate, Use }
}

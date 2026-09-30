#nullable enable

using Godot;

namespace AAEmu.GodotViewer.Client;

/// <summary>Minimal unit information consumed by <see cref="OverheadPresentation"/>.</summary>
/// <remarks>This is deliberately independent of <c>OnlineSession</c> and <c>UnitRegistry</c>.</remarks>
public readonly record struct OverheadUnit(
    uint UnitId,
    Node3D? Node,
    OverheadUnitKind Kind,
    uint TemplateId,
    float Height);

/// <summary>Unit kinds relevant to overhead presentation.</summary>
public enum OverheadUnitKind { Other, Player, Npc, Mate, Vehicle }

/// <summary>Quest state supplied by the host's authoritative quest resolver.</summary>
public enum OverheadQuestState { None, Available, InProgress, Completable }

/// <summary>DB quest detail/category, kept explicit for category-aware selection and tinting by the host.</summary>
public enum OverheadQuestKind
{
    Unknown = 0, Normal = 1, Main = 2, Saga = 3, Tutorial = 4, Hidden = 5, Daily = 7,
    Livelihood = 8, Group = 9, DailyHunt = 10, DailyLivelihood = 11, DailyGroup = 12,
    Today = 13, Hero = 14, Weekly = 15, Expedition = 16,
}

/// <summary>
/// A quest marker selected by the host. The client package has no verified NPC-head !/? atlas: the fallback
/// presentation uses the original <c>ui/cinema.dds</c> journal marks and therefore remains intentionally named
/// <see cref="JournalFallback"/>.
/// </summary>
public readonly record struct OverheadQuestMarker(
    OverheadQuestState State,
    OverheadQuestKind Kind = OverheadQuestKind.Unknown,
    bool JournalFallback = true)
{
    public static readonly OverheadQuestMarker None = new(OverheadQuestState.None, OverheadQuestKind.Unknown, false);
}

/// <summary>Native static model/material pair recovered from the game pak for a 3D NPC quest marker.</summary>
public readonly record struct OverheadQuestModelAsset(string ModelPath, string MaterialPath);

/// <summary>Chat categories rendered above a unit.</summary>
public enum OverheadChatKind { Say, Shout, Npc }

/// <summary>Animation and chat text produced by an emotion-id resolver.</summary>
public readonly record struct OverheadEmotion(string? Action, string? ChatText);

/// <summary>
/// Self-contained 3D marker and bubble renderer. It neither reads nor sends gameplay packets; callers provide
/// current units and resolve quest/emotion state from their own session reducers.
/// </summary>
public partial class OverheadPresentation : Node3D
{
    private const string HudTexturePath = "game/ui/common/hud.dds";
    private const string HudGeometryPath = "game/ui/common/hud.g";
    private const string JournalTexturePath = "game/ui/cinema.dds";
    private const string JournalGeometryPath = "game/ui/cinema.g";
    private const float PixelSize = 0.006f;

    private readonly Dictionary<uint, UnitVisual> _visuals = [];
    private readonly Dictionary<uint, OverheadUnit> _units = [];
    private Texture2D? _hudTexture;
    private Texture2D? _journalTexture;
    private Region? _bubbleBackground;
    private Region? _tailNormal;
    private Region? _tailThink;
    private Region? _questStart;
    private Region? _questComplete;
    private Font? _font;
    private double _processElapsed;

    /// <summary>Optional update interval for world labels and markers; zero processes every frame.</summary>
    public float UpdateInterval { get; set; }

    /// <summary>Returns a marker for an NPC, or <see cref="OverheadQuestMarker.None"/>.</summary>
    public Func<OverheadUnit, OverheadQuestMarker>? QuestMarkerResolver { get; set; }

    /// <summary>Maps a state to a pak-backed 3D quest marker mesh. The host can create it through ModelLibrary.</summary>
    public Func<OverheadQuestMarker, Mesh?>? QuestMeshResolver { get; set; }

    /// <summary>Optional final text translation, normally <c>UiTranslator.Shared.Translate</c>.</summary>
    public Func<string, string>? TranslateText { get; set; }

    /// <summary>Maps an emotion id into an animation action and a chat line.</summary>
    public Func<uint, uint, uint, OverheadEmotion?>? EmotionResolver { get; set; }

    /// <summary>Called for the source unit after a resolved emotion is received.</summary>
    public Action<uint, string>? PlayAction { get; set; }

    /// <summary>Receives resolved emotion text for the caller's normal chat log.</summary>
    public Action<string>? WriteChat { get; set; }

    /// <summary>Maximum concurrent overhead bubbles. Least recently refreshed bubbles are removed first.</summary>
    [Export] public int MaximumBubbles { get; set; } = 64;

    /// <summary>Default bubble lifetime before its one-second fade, matching the original Lua.</summary>
    [Export] public float DefaultBubbleSeconds { get; set; } = 5f;

    /// <summary>Supplies current scene units. Call this on the main thread whenever unit membership changes.</summary>
    public void SetUnits(IEnumerable<OverheadUnit>? units)
    {
        _units.Clear();
        if (units is null) return;
        foreach (var unit in units)
            if (unit.UnitId != 0 && unit.Node is not null)
                _units[unit.UnitId] = unit;
    }

    /// <summary>Shows or refreshes a say, shout, or NPC speech bubble above <paramref name="unitId"/>.</summary>
    public void ShowChat(uint unitId, string? speakerName, string? text, OverheadChatKind kind = OverheadChatKind.Say,
        float? seconds = null, bool showSpeakerName = true)
    {
        if (unitId == 0 || string.IsNullOrWhiteSpace(text)) return;
        EnsureAssets();
        if (!_units.TryGetValue(unitId, out var unit) || unit.Node is null) return;
        var visual = GetVisual(unit);
        var localized = TranslateText?.Invoke(text) ?? text;
        visual.ShowBubble(speakerName ?? "", localized, kind, Mathf.Max(0.1f, seconds ?? DefaultBubbleSeconds),
            _bubbleBackground, kind == OverheadChatKind.Npc ? _tailThink : _tailNormal, _font, showSpeakerName);
        TrimBubbles();
    }

    /// <summary>Shows the resolver-selected marker immediately. This is also refreshed automatically each process tick.</summary>
    public void ShowMarker(uint unitId)
    {
        EnsureAssets();
        if (!_units.TryGetValue(unitId, out var unit) || unit.Kind != OverheadUnitKind.Npc || unit.Node is null) return;
        var marker = QuestMarkerResolver?.Invoke(unit) ?? OverheadQuestMarker.None;
        GetVisual(unit).ShowMarker(marker, ResolveQuestMesh(marker), MarkerFor(marker));
    }

    /// <summary>Handles a parsed SCEmotionExpressed payload without coupling this node to a packet type.</summary>
    public void ShowEmotion(uint sourceUnitId, uint targetUnitId, uint emotionId)
    {
        var emotion = EmotionResolver?.Invoke(sourceUnitId, targetUnitId, emotionId);
        if (emotion is not { } value) return;
        if (!string.IsNullOrWhiteSpace(value.Action)) PlayAction?.Invoke(sourceUnitId, value.Action);
        if (!string.IsNullOrWhiteSpace(value.ChatText)) WriteChat?.Invoke(TranslateText?.Invoke(value.ChatText) ?? value.ChatText);
    }

    public override void _Process(double delta)
    {
        _processElapsed += delta;
        if (UpdateInterval > 0f && _processElapsed < UpdateInterval)
            return;
        var elapsed = (float)_processElapsed;
        _processElapsed = 0;
        EnsureAssets();
        foreach (var (id, unit) in _units)
        {
            if (unit.Node is null || !GodotObject.IsInstanceValid(unit.Node) || !unit.Node.IsInsideTree())
            {
                if (_visuals.Remove(id, out var stale)) stale.QueueFree();
                continue;
            }
            var visual = GetVisual(unit);
            visual.GlobalPosition = unit.Node.GlobalPosition + Vector3.Up * Mathf.Max(0.4f, unit.Height + 0.25f);
            if (unit.Kind == OverheadUnitKind.Npc)
            {
                var marker = QuestMarkerResolver?.Invoke(unit) ?? OverheadQuestMarker.None;
                visual.ShowMarker(marker, ResolveQuestMesh(marker), MarkerFor(marker));
            }
            visual.Tick(elapsed);
        }
    }

    private UnitVisual GetVisual(OverheadUnit unit)
    {
        if (_visuals.TryGetValue(unit.UnitId, out var visual)) return visual;
        visual = new UnitVisual { Name = $"Overhead{unit.UnitId}" };
        AddChild(visual);
        _visuals[unit.UnitId] = visual;
        return visual;
    }

    private Region? MarkerFor(OverheadQuestMarker marker) => marker.State switch
    {
        // These two cells are verified original journal marks. They are a temporary, documented visual fallback;
        // no claim is made that they are the native NPC-head !/? atlas.
        OverheadQuestState.Available or OverheadQuestState.InProgress => _questStart,
        OverheadQuestState.Completable => _questComplete,
        _ => null,
    };

    private Mesh? ResolveQuestMesh(OverheadQuestMarker marker) => marker.State == OverheadQuestState.None
        ? null
        : QuestMeshResolver?.Invoke(marker);

    /// <summary>Pak paths for the recovered native status models. Material/status pairing remains caller-controlled.</summary>
    public static OverheadQuestModelAsset? NativeQuestModel(OverheadQuestState state) => state switch
    {
        OverheadQuestState.Available => new("game/objects/ui/quest_mark/quest_mark_in_new.cgf",
            "game/objects/ui/quest_mark/quest_mark_yellow.mtl"),
        OverheadQuestState.InProgress => new("game/objects/ui/quest_mark/quest_mark_in_progress.cgf",
            "game/objects/ui/quest_mark/quest_mark_gray.mtl"),
        OverheadQuestState.Completable => new("game/objects/ui/quest_mark/quest_mark_in_ready.cgf",
            "game/objects/ui/quest_mark/quest_mark_yellow.mtl"),
        _ => null,
    };

    private void TrimBubbles()
    {
        var active = _visuals.Values.Where(v => v.HasBubble).OrderBy(v => v.LastBubbleUse).ToList();
        foreach (var visual in active.Take(Math.Max(0, active.Count - MaximumBubbles))) visual.HideBubble();
    }

    private void EnsureAssets()
    {
        if (_hudTexture is null) _hudTexture = LoadTexture(HudTexturePath);
        if (_journalTexture is null) _journalTexture = LoadTexture(JournalTexturePath);
        if (_bubbleBackground is null) _bubbleBackground = LoadRegion(HudGeometryPath, HudTexturePath, "chat_bubble_bg", _hudTexture);
        if (_tailNormal is null) _tailNormal = LoadRegion(HudGeometryPath, HudTexturePath, "chat_bubble_tail_normal", _hudTexture);
        if (_tailThink is null) _tailThink = LoadRegion(HudGeometryPath, HudTexturePath, "chat_bubble_tail_think", _hudTexture);
        if (_questStart is null) _questStart = LoadRegion(JournalGeometryPath, JournalTexturePath, "quest_mark00", _journalTexture);
        if (_questComplete is null) _questComplete = LoadRegion(JournalGeometryPath, JournalTexturePath, "quest_mark01", _journalTexture);
        _font ??= LoadFont();
    }

    private static Texture2D? LoadTexture(string path)
    {
        try
        {
            var bytes = PakFiles.Read(path);
            if (bytes is null) return null;
            var image = new Image();
            return image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) == Error.Ok && !image.IsEmpty()
                ? ImageTexture.CreateFromImage(image) : null;
        }
        catch (Exception exception) { GD.PushWarning($"Overhead texture '{path}': {exception.Message}"); return null; }
    }

    private static Region? LoadRegion(string geometryPath, string texturePath, string name, Texture2D? texture)
    {
        if (texture is null) return null;
        try
        {
            var source = PakFiles.ReadText(geometryPath);
            var item = source is null ? null : UiGeometryReader.Parse(source, texturePath).Regions
                .FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item?.Rect is not { } rect) return null;
            return new Region(new AtlasTexture { Atlas = texture, Region = new Rect2(rect.X, rect.Y, rect.Width, rect.Height) },
                rect.Width, rect.Height, item.Insets);
        }
        catch (Exception exception) { GD.PushWarning($"Overhead geometry '{geometryPath}/{name}': {exception.Message}"); return null; }
    }

    private static Font LoadFont()
    {
        var bytes = PakFiles.Read("game/ui/font/roboto.ttf") ?? PakFiles.Read("game/ui/font/tahoma_regular_font.ttf");
        return bytes is null ? ThemeDB.FallbackFont : new FontFile { Data = bytes };
    }

    private sealed record Region(Texture2D Texture, float Width, float Height, UiNineSliceInsets? Insets);

    private sealed partial class UnitVisual : Node3D
    {
        private readonly Sprite3D _marker = NewSprite();
        private readonly MeshInstance3D _nativeMarker = new() { Name = "NativeQuestMarker", Scale = Vector3.One * 0.5f };
        private readonly Node3D _bubble = new() { Name = "Bubble" };
        private readonly Label3D _name = NewLabel(16);
        private readonly Label3D _text = NewLabel(18);
        private Sprite3D? _background;
        private Sprite3D? _tail;
        private float _remaining;

        public bool HasBubble => _bubble.Visible;
        public ulong LastBubbleUse { get; private set; }

        public override void _Ready()
        {
            AddChild(_marker); _marker.Position = Vector3.Up * 0.35f;
            AddChild(_nativeMarker); _nativeMarker.Position = Vector3.Up * 0.35f; _nativeMarker.Visible = false;
            AddChild(_bubble); _bubble.AddChild(_name); _bubble.AddChild(_text);
            _bubble.Visible = false;
        }

        public void ShowMarker(OverheadQuestMarker marker, Mesh? mesh, Region? region)
        {
            _nativeMarker.Mesh = mesh;
            _nativeMarker.Visible = mesh is not null && marker.State != OverheadQuestState.None;
            _marker.Texture = region?.Texture;
            _marker.Visible = !_nativeMarker.Visible && marker.JournalFallback && region is not null &&
                marker.State != OverheadQuestState.None;
            _marker.Modulate = marker.State switch
            {
                OverheadQuestState.Available => new Color(1f, .85f, .39f),
                OverheadQuestState.Completable => new Color(1f, .85f, .39f),
                OverheadQuestState.InProgress => new Color(.72f, .72f, .72f),
                _ => Colors.White,
            };
        }

        public void ShowBubble(string speaker, string text, OverheadChatKind kind, float seconds, Region? background, Region? tail,
            Font? font, bool showSpeakerName)
        {
            _remaining = seconds + 1f; LastBubbleUse = Time.GetTicksMsec(); _bubble.Visible = true;
            _name.Text = speaker; _name.Font = font; _name.Visible = showSpeakerName;
            _text.Text = text; _text.Font = font;
            // The HUD bubble is white. Its native textbox uses dark foreground colours without a shadow.
            _name.Modulate = kind == OverheadChatKind.Npc ? new Color(.12f, .38f, .55f) : new Color(.18f, .18f, .18f);
            _text.Modulate = kind == OverheadChatKind.Shout ? new Color(.55f, .30f, .04f) : new Color(.15f, .15f, .15f);
            var width = Mathf.Clamp(70f + text.Length * 8f, 140f, 300f);
            _text.Width = width - 20f; _text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            // chatbubble.alb puts the name tag above the textbox and omits it for the local player.
            // Keep the message in the centre of the white body so the background cannot hide it.
            _text.Position = new Vector3(0, .20f, -.01f);
            _name.Position = new Vector3(0, .50f, -.01f);
            SetSprite(ref _background, background, new Vector3(0, .20f, .01f), new Vector2(width, 90));
            SetSprite(ref _tail, tail, new Vector3(0, -.12f, .02f), Vector2.Zero);
            ApplyBubbleAlpha(1f);
        }

        public void Tick(float delta)
        {
            if (!_bubble.Visible) return;
            _remaining -= delta;
            if (_remaining <= 0) { HideBubble(); return; }
            if (_remaining < 1f) ApplyBubbleAlpha(_remaining);
        }

        public void HideBubble() => _bubble.Visible = false;

        private void ApplyBubbleAlpha(float alpha)
        {
            static Color Alpha(Color color, float value) => new(color.R, color.G, color.B, value);
            _name.Modulate = Alpha(_name.Modulate, alpha);
            _text.Modulate = Alpha(_text.Modulate, alpha);
            if (_background is not null) _background.Modulate = Alpha(_background.Modulate, alpha);
            if (_tail is not null) _tail.Modulate = Alpha(_tail.Modulate, alpha);
        }

        private void SetSprite(ref Sprite3D? sprite, Region? region, Vector3 position, Vector2 desiredPixels)
        {
            if (region is null) { if (sprite is not null) sprite.Visible = false; return; }
            sprite ??= NewSprite();
            if (sprite.GetParent() is null) _bubble.AddChild(sprite);
            // Sprites are created after the labels. Put them behind those labels in the transparent draw order.
            _bubble.MoveChild(sprite, 0);
            sprite.RenderPriority = -1;
            sprite.Texture = region.Texture; sprite.Position = position; sprite.Visible = true;
            var size = desiredPixels == Vector2.Zero ? new Vector2(region.Width, region.Height) : desiredPixels;
            sprite.Scale = new Vector3(size.X / region.Width, size.Y / region.Height, 1);
        }

        private static Sprite3D NewSprite() => new()
        {
            PixelSize = PixelSize, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true, Shaded = false,
        };

        private static Label3D NewLabel(int size) => new()
        {
            FontSize = size, OutlineSize = 0,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
            PixelSize = PixelSize, HorizontalAlignment = HorizontalAlignment.Center,
        };
    }
}

using AAEmu.GodotViewer.Ui.X2.Scripting;
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// The original ArcheAge login UI in Godot: runs the client's own x2ui Lua (connect, then character_select) on the
/// widget model and draws it with the pak's textures and fonts. Drop-in for <see cref="LoginFlow"/>: same backend,
/// same <see cref="EnteredWorld"/> event. Requires <see cref="PakFiles.Open"/> first.
/// </summary>
public partial class X2UiLayer : Control
{
    /// <summary>The client database (<see cref="ClientPaths.Database"/>).</summary>
    private static string DefaultDatabase => ClientPaths.Database;

    private readonly X2LoginSession _session;
    private readonly X2TextureCache _textures;
    private readonly X2FontCache _fonts;
    private readonly SqliteUiTextSource _texts;
    private readonly List<string> _log = [];
    private double _caretClock;
    private bool _started;

    public event Action<CharacterInfo> EnteredWorld;
    /// <summary>True while the original quest-directing cinema scripts own the screen.</summary>
    public event Action<bool>? QuestDirectingModeChanged;
    /// <summary>Script/engine log lines (errors, stubs, login progress).</summary>
    public event Action<string> Log;

    /// <param name="backend">Login backend (NetLoginBackend for the real server).</param>
    /// <param name="host">Login server host.</param>
    /// <param name="port">Login server port.</param>
    /// <param name="account">Account pre-filled in the id box.</param>
    /// <param name="launcherAccount">Typing this account logs in with the local launcher's dev account (empty account/password to the backend).</param>
    /// <param name="gameDatabase">Client database with ui_texts (localised UI strings).</param>
    public X2UiLayer(ILoginBackend backend, string host = "127.0.0.1", int port = 1237, string account = "",
        string launcherAccount = "", string gameDatabase = null, string locale = "en_us", bool startInWorld = false)
    {
        gameDatabase = string.IsNullOrEmpty(gameDatabase) ? DefaultDatabase : gameDatabase;
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        X2NativeWidgetTypes.Register(); // client-native widget types (chat window, slots, maps, ...) before any script runs
        _startInWorld = startInWorld;
        _gameDatabase = gameDatabase;
        var files = new PakUiFiles();
        var texts = _texts = new SqliteUiTextSource(gameDatabase, locale);
        var options = new X2LoginOptions
        {
            Host = host,
            Port = port,
            Account = account ?? "",
            LauncherAccount = launcherAccount ?? "",
            Locale = locale,
            GameDatabase = gameDatabase,
            IsShiftKeyDown = () => Godot.Input.IsKeyPressed(Key.Shift),
            Translator = UiTranslator.Shared = LoadTranslations(),
            Details = backend is Client.NetLoginBackend net ? CachedDetails(net, texts) : null,
        };
        _session = new X2LoginSession(files, texts, new LazyMeasurer(this), backend, options, Emit);
        // X2_LUA_INIT=<file> (debugging): runs the file in the world stage's Lua state right after its scripts load
        if (System.Environment.GetEnvironmentVariable("X2_LUA_INIT") is { Length: > 0 } luaInit)
            _session.StageBuilt += stage =>
            {
                Emit($"[x2] lua init: stage {stage}, file exists {System.IO.File.Exists(luaInit)}");
                if (stage == Scripting.X2Engine.StageWorld && System.IO.File.Exists(luaInit))
                {
                    _session.Host?.Define("X2Debug", "Log", a => { Emit("[x2 lua init] " + a.Str(0)); return null; }); // "[x2" lines reach the console live
                    _session.Host?.RunString(System.IO.File.ReadAllText(luaInit), "=lua_init");
                }
            };
        _textures = new X2TextureCache(_session.Settings, OverridesPath);
        _fonts = new X2FontCache(_session.Settings);
        _session.EnteredWorld += c =>
        {
            if (_session.Options.HudInWorld)
            {
                // the in-game UI (stage 6) replaces the login screens: no backdrop, clicks outside widgets reach the world
                InWorld = true;
                MouseFilter = MouseFilterEnum.Pass;
                StartWorldBridge(c);
            }
            else Hide();
            EnteredWorld?.Invoke(c);
        };
        _session.Engine.ExitRequested += () => Emit("[x2] exit requested (menu)");

        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        ClipContents = true;
    }

    public X2UiLayer() : this(new MockLoginBackend()) { }

    /// <summary>True once the character is in the world and the layer shows the in-game HUD.</summary>
    public bool InWorld { get; private set; }
    private bool _uiCapture;

    /// <summary>English for strings the client data only has in Chinese/Korean, keyed by the original text.</summary>
    public const string TranslationsPath = "res://Ui/X2/Translations/strings.json";
    /// <summary>English replacements for textures with baked-in Chinese text: Overrides/&lt;pak path without extension&gt;.png.</summary>
    public const string OverridesPath = "res://Ui/X2/Overrides";

    /// <summary>Every *.json in Ui/X2/Translations (strings.json plus the db_*.json name/description tables).</summary>
    /// <summary>
    /// All translation files (strings.json plus the db_*.json tables, ~50k entries, ~0.1 s), loaded once per process and
    /// published as <see cref="UiTranslator.Shared"/> for code outside the UI (nameplates, chat bubbles).
    /// </summary>
    public static UiTranslator LoadTranslations()
    {
        if (_translations != null) return _translations;
        _translations = ReadTranslations();
        UiTranslator.Shared = _translations;
        return _translations;
    }

    private static UiTranslator? _translations;

    private static UiTranslator ReadTranslations()
    {
        var folder = TranslationsPath[..TranslationsPath.LastIndexOf('/')];
        var files = new List<(string, string)>();
        using var dir = Godot.DirAccess.Open(folder);
        if (dir != null)
            foreach (var name in dir.GetFiles())
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    files.Add((name, Godot.FileAccess.GetFileAsString($"{folder}/{name}")));
        var translator = UiTranslator.FromFiles(files);
        translator.AddExact(ServerMessageTranslations());
        return translator;
    }

    /// <summary>
    /// Korean → English pairs for text the server sends verbatim (skill requirement failures such as "공포 상태에서는 기술을 사용할 수 없습니다",
    /// world message effects, replacement chat texts, tower-defence progress), from the client database's localized_texts.
    /// </summary>
    private static IEnumerable<(string, string)> ServerMessageTranslations()
    {
        var result = new List<(string, string)>();
        try
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DefaultDatabase};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT ko, en_us FROM localized_texts
                WHERE ((tbl_name = 'skill_reqs' AND tbl_column_name = 'message')
                    OR (tbl_name = 'world_message_effects' AND tbl_column_name = 'message')
                    OR (tbl_name = 'replace_chat_texts' AND tbl_column_name = 'text')
                    OR (tbl_name = 'tower_def_progs' AND tbl_column_name = 'msg'))
                  AND COALESCE(ko, '') <> '' AND COALESCE(en_us, '') <> ''
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add((r.GetString(0), r.GetString(1)));
        }
        catch (Exception) { }
        return result;
    }

    private static string ReadProjectText(string path)
        => Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : null;

    private static Func<CharacterInfo, X2CharacterDetails> CachedDetails(Client.NetLoginBackend net, SqliteUiTextSource texts)
    {
        var cache = new Dictionary<ulong, X2CharacterDetails>();
        return c =>
        {
            if (!cache.TryGetValue(c.Id, out var d)) cache[c.Id] = d = X2NetDetails.Read(net, c.Id, texts);
            return d;
        };
    }

    public ILoginBackend Backend { get; }
    public X2LoginSession Session => _session;
    public UiRoot Root => _session.Root;
    /// <summary>UI pixels per screen pixel (1 = the client's native size).</summary>
    public float UiScale { get; set; } = 1;
    /// <summary>Texture drawn behind the UI where the client shows its 3D login stage.</summary>
    public string Backdrop { get; set; } = "ui/login_stage/login_bg.dds";
    public IReadOnlyList<string> LogLines => _log;
    public X2TextureCache Textures => _textures;

    private long _emittedTotal;

    private void Emit(string line)
    {
        _emittedTotal++;
        _log.Add(line);
        if (_log.Count > 5000) _log.RemoveRange(0, 1000);
        if (Log != null) Log(line);
        // without a listener (e.g. the world viewer HUD) script errors still reach the console
        else if (line.StartsWith("[lua error]") || line.StartsWith("[x2")) GD.Print(line.Length > 2000 ? line[..2000] : line);
    }

    /// <summary>Text metrics come from the Godot fonts once the layer exists.</summary>
    private sealed class LazyMeasurer(X2UiLayer owner) : ITextMeasurer
    {
        public float Width(string fontKey, float size, string text) => owner._fonts.Width(fontKey, size, text);
        public float LineHeight(string fontKey, float size) => owner._fonts.LineHeight(fontKey, size);
    }

    public override void _Ready()
    {
        InitializeModelViews();
        UpdateScreenSize();
        Resized += UpdateScreenSize;
        InstallLeftLoadingFilter();
        if (_startInWorld)
        {
            InWorld = true;
            MouseFilter = MouseFilterEnum.Pass;
            StartWorldBridge();
            _session.StartInWorld();
        }
        else _session.Start();
        _started = true;
        GrabFocus();
    }

    public override void _ExitTree()
    {
        RemoveLeftLoadingFilter();
        DisposeModelViews();
        DisposeProtocolBinding();
        _combatBinding?.Dispose();
        Bridge?.Dispose();
        _session.Dispose();
    }

    private void UpdateScreenSize()
    {
        var s = Math.Max(0.1f, UiScale);
        _session.UiScale = s;
        _session.SetScreenSize(Size.X / s, Size.Y / s);
    }

    public override void _Process(double delta)
    {
        // X2_HIDE_HUD=1 (profiling): scripts keep running, nothing is drawn
        if (HideForProfiling && Visible) Visible = false;
        if (!_started) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Bridge?.Pump();
        if (LiveSession?.Player is { } player)
            Bridge?.UpdatePlayerPosition(player.CryPosition.X, player.CryPosition.Y, player.CryPosition.Z);
        ParityTick(delta);
        KeepWorldFocus();
        ScenarioTick(delta);
        LoadingTick(delta);
        _session.Update(delta * 1000);
        UpdateModelViews();
        _profUpdate += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        ProfileTick(delta);
        _caretClock += delta;
        QueueRedraw();
    }

    // ------------------------------------------------------------------ drawing

    public override void _Draw()
    {
        var root = _session.Root;
        if (root == null) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { DrawRoot(root); }
        finally { _profDraw += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; }
    }

    // X2_PROFILE=1: logs the UI's per-frame cost (script update, drawing) every 5 seconds
    private static readonly bool HideForProfiling = System.Environment.GetEnvironmentVariable("X2_HIDE_HUD") == "1";
    private static readonly bool Profile = System.Environment.GetEnvironmentVariable("X2_PROFILE") == "1";
    private double _profUpdate, _profDraw, _profClock;
    private int _profFrames;

    // X2_PROFILE_OFF=plates,overhead (profiling only): disables the world nameplates/selection visuals (TargetingVisuals) and/or
    // the overhead markers and bubbles (OverheadPresentation) so their share of the frame can be measured.
    private static readonly string[] ProfileOff = (System.Environment.GetEnvironmentVariable("X2_PROFILE_OFF") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private readonly HashSet<string> _profileDisabled = [];

    private void ApplyProfileOff()
    {
        foreach (var what in ProfileOff)
        {
            if (_profileDisabled.Contains(what)) continue;
            // "<what>hide" only hides (keeps the per-frame script work), to split script from render cost
            var hideOnly = what.EndsWith("hide", StringComparison.Ordinal);
            var key = hideOnly ? what[..^4] : what;
            var name = key switch { "plates" => "TargetingVisuals", "overhead" => "OverheadPresentation", _ => key };
            if (GetTree().Root.FindChild(name, true, false) is not Node node) continue;
            if (!hideOnly) node.ProcessMode = ProcessModeEnum.Disabled;
            if (node is Node3D n3) n3.Visible = false;
            else if (node is CanvasItem ci) ci.Visible = false;
            _profileDisabled.Add(what);
            GD.Print($"[x2 profile] disabled {name}");
        }
    }

    private void ProfileTick(double delta)
    {
        if (!Profile) return;
        if (ProfileOff.Length > _profileDisabled.Count) ApplyProfileOff();
        _profFrames++;
        _profClock += delta;
        if (_profClock < 5) return;
        // the whole frame for comparison: Godot's process/physics time, render load
        var process = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
        var physics = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000;
        var objects = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        var draws = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        var prims = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        var nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        var frameMs = _profClock * 1000 / Math.Max(1, _profFrames);
        GD.Print($"[x2 profile] {_profFrames / _profClock:F1} fps ({frameMs:F1} ms/frame): process {process:F1} ms (ui update {_profUpdate / _profFrames:F2}, " +
                 $"ui draw submit {_profDraw / _profFrames:F2}), physics {physics:F1} ms; render {draws:F0} draw calls, {objects:F0} objects, " +
                 $"{prims / 1000:F0}k primitives; {nodes:F0} nodes; {_session.Root?.AllWidgets().Count} widgets");
        if (_session.Root is { } profiled)
        {
            var top = profiled.ProfileByWidget.OrderByDescending(p => p.Value).Take(6)
                .Select(p => $"{p.Key} {p.Value / Math.Max(1, _profFrames):F2}");
            GD.Print($"[x2 profile] lua OnUpdate {profiled.ProfileOnUpdateMs / Math.Max(1, _profFrames):F2} ms/frame, " +
                     $"{profiled.ProfileOnUpdateCalls / Math.Max(1, _profFrames)} calls/frame; top: {string.Join("; ", top)}");
            profiled.ProfileOnUpdateMs = 0;
            profiled.ProfileOnUpdateCalls = 0;
            profiled.ProfileByWidget.Clear();
        }
        _profUpdate = _profDraw = _profClock = 0;
        _profFrames = 0;
    }

    private void DrawRoot(UiRoot root)
    {
        if (!InWorld) DrawBackdrop();
        var s = Math.Max(0.1f, UiScale);
        DrawSetTransform(Vector2.Zero, 0, new Vector2(s, s));
        DrawLoginModelView();
        if (_scenarioHideUi) return;
        var order = root.PaintOrder();
        foreach (var w in order) if (w is ListCtrlWidget list) list.UpdateHighlights();
        _clipCache.Clear();
        foreach (var widget in order)
        {
            var alpha = EffectiveAlpha(widget);
            if (alpha <= 0.004f) continue;
            _clip = ClipOf(widget.Parent);
            if (_clip is { Width: <= 0 } or { Height: <= 0 }) continue;
            var ancestors = new List<Widget>();
            for (var ancestor = widget; ancestor != null; ancestor = ancestor.Parent) ancestors.Add(ancestor);
            ancestors.Reverse();
            var localScale = 1f;
            var offset = Vector2.Zero;
            foreach (var ancestor in ancestors)
            {
                var r = ancestor.ScreenRect;
                if (ancestor.WidgetScale is > 0 and < 0.999f or > 1.001f)
                {
                    var factor = ancestor.WidgetScale;
                    offset = offset * factor + new Vector2(r.X, r.Y) * (1 - factor);
                    localScale *= factor;
                }
                if (ancestor is WindowWidget { StartScale: < 0.999f } window)
                {
                    var factor = window.StartScale;
                    offset = offset * factor + new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2) * (1 - factor);
                    localScale *= factor;
                }
            }
            if (Math.Abs(localScale - 1) > 0.001f)
                DrawSetTransform(offset * s, 0, new Vector2(s * localScale, s * localScale));
            DrawWidget(widget, alpha);
            if (Math.Abs(localScale - 1) > 0.001f) DrawSetTransform(Vector2.Zero, 0, new Vector2(s, s));
        }
        _clip = null;
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    // ------------------------------------------------------------------ clipping (scroll viewports)

    private UiRect? _clip;
    private readonly Dictionary<Widget, UiRect?> _clipCache = [];

    /// <summary>The intersection of every scroll viewport (EnableScroll) at or above <paramref name="w"/>; null = unclipped.</summary>
    private UiRect? ClipOf(Widget? w)
    {
        if (w == null) return null;
        if (_clipCache.TryGetValue(w, out var cached)) return cached;
        var outer = ClipOf(w.Parent);
        var clip = outer;
        if (w.ScrollEnabled) clip = outer is { } o ? Intersect(o, w.ScreenRect) : w.ScreenRect;
        _clipCache[w] = clip;
        return clip;
    }

    private static UiRect Intersect(UiRect a, UiRect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        return new UiRect(x, y, Math.Max(0, Math.Min(a.Right, b.Right) - x), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - y));
    }

    /// <summary>Clips a destination rectangle (and its texture source proportionally) to the current viewport; false = nothing left.</summary>
    private bool Clip(ref UiRect dst, ref UiRect src, bool flipX = false, bool flipY = false)
    {
        if (_clip is not { } c) return true;
        var d = Intersect(dst, c);
        if (d.Width <= 0 || d.Height <= 0) return false;
        if (dst.Width > 0 && dst.Height > 0)
        {
            var sx = src.Width / dst.Width;
            var sy = src.Height / dst.Height;
            var left = flipX ? dst.Right - d.Right : d.X - dst.X;
            var top = flipY ? dst.Bottom - d.Bottom : d.Y - dst.Y;
            src = new UiRect(src.X + left * sx, src.Y + top * sy, d.Width * sx, d.Height * sy);
        }
        dst = d;
        return true;
    }

    private void ClippedRect(UiRect dst, Color color)
    {
        var src = dst;
        if (Clip(ref dst, ref src)) DrawRect(R(dst), color);
    }

    /// <summary>Whether a text line (top y, height) is inside the viewport vertically; lines are skipped whole.</summary>
    private bool LineVisible(float y, float height)
        => _clip is not { } c || (y + height * 0.5f >= c.Y && y + height * 0.5f <= c.Bottom);

    private void DrawBackdrop()
    {
        if (string.IsNullOrEmpty(Backdrop)) return;
        var tex = _textures.Get(Backdrop);
        if (tex == null) { DrawRect(new Rect2(Vector2.Zero, Size), Colors.Black); return; }
        var ts = tex.GetSize();
        var scale = Math.Max(Size.X / ts.X, Size.Y / ts.Y);
        var drawSize = ts * scale;
        DrawTextureRect(tex, new Rect2((Size - drawSize) / 2, drawSize), false);
    }

    private static float EffectiveAlpha(Widget w)
    {
        var a = 1f;
        for (var p = w; p != null; p = p.Parent) a *= p.Alpha * p.FadeAlpha;
        return a;
    }

    private static int DrawableLayerRank(string layer) => layer?.ToLowerInvariant() switch
    {
        "background" => 0,
        "border" => 1,
        "artwork" => 2,
        "overlay" => 4,
        "overoverlay" => 5,
        "highlight" => 5,
        _ => 2,
    };

    private void DrawWidget(Widget widget, float alpha)
    {
        var drawables = widget.Drawables.Where(d => d.Visible).OrderBy(d => DrawableLayerRank(d.Layer)).ThenBy(d => d.Sequence).ToList();
        var button = widget as ButtonWidget;
        foreach (var d in drawables)
        {
            if (DrawableLayerRank(d.Layer) >= 4) break;
            if (button != null && !button.IsDrawableActive(d)) continue;
            DrawDrawable(d, alpha);
        }
        DrawNative(widget, alpha);
        DrawWidgetText(widget, alpha);
        foreach (var d in drawables)
        {
            if (DrawableLayerRank(d.Layer) < 4) continue;
            if (button != null && !button.IsDrawableActive(d)) continue;
            DrawDrawable(d, alpha);
        }
    }

    private static Rect2 R(UiRect r) => new(r.X, r.Y, r.Width, r.Height);
    private static Color C(UiColor c, float alpha) => new(c.R, c.G, c.B, c.A * alpha);

    private void DrawDrawable(Drawable d, float alpha)
    {
        var dst = d.ScreenRect;
        if (d is TextureDrawable { Snap: true })
            dst = new UiRect(MathF.Round(dst.X), MathF.Round(dst.Y), MathF.Round(dst.Width), MathF.Round(dst.Height));
        if (d is TextureDrawable { Effect: { } fx })
        {
            // effect drawables show only while their effect plays, with its colour and scale
            if (!fx.Running) return;
            alpha *= fx.Color.A;
            var (sx, sy) = fx.Scale;
            dst = new UiRect(dst.X + dst.Width * (1 - sx) / 2, dst.Y + dst.Height * (1 - sy) / 2, dst.Width * sx, dst.Height * sy);
        }
        if (dst.Width <= 0 || dst.Height <= 0) return;
        var tint = C(d.Tint, alpha);
        switch (d)
        {
            case ColorDrawable:
                ClippedRect(dst, tint);
                return;
            case TextDrawable text:
                DrawTextBlock(text.Text, text.style, dst, default, text.style.Color, alpha, false, 0);
                return;
            case TextureDrawable td:
            {
                var tex = td.TexturePath == null ? null : _textures.Get(td.TexturePath);
                if (tex == null) return;
                var src = td.AnimationRunning && td.AnimationFrames.Count > 0
                    ? td.AnimatedSourceRect : td.SourceRect ?? new UiRect(0, 0, tex.GetWidth(), tex.GetHeight());
                var kind = td.EffectiveKind;
                if ((kind == DrawableKind.NinePart || kind == DrawableKind.ThreePart) && td.SliceInset is { } inset)
                {
                    var flipX = src.Width < 0;
                    var flipY = src.Height < 0;
                    var abs = new UiRect(Math.Min(src.X, src.X + src.Width), Math.Min(src.Y, src.Y + src.Height), Math.Abs(src.Width), Math.Abs(src.Height));
                    var patches = kind == DrawableKind.NinePart ? NineSliceGeometry.Build(abs, dst, inset) : NineSliceGeometry.BuildThree(abs, dst, inset);
                    foreach (var p in patches)
                        DrawRegion(tex, p.Destination, p.Source, tint, flipX, flipY);
                    return;
                }
                var s = new UiRect(Math.Min(src.X, src.X + src.Width), Math.Min(src.Y, src.Y + src.Height), Math.Abs(src.Width), Math.Abs(src.Height));
                DrawRegion(tex, dst, s, tint, src.Width < 0, src.Height < 0);
                foreach (var extra in td.ExtraTextures)
                    if (_textures.Get(extra) is { } layer)
                        DrawRegion(layer, dst, new UiRect(0, 0, layer.GetWidth(), layer.GetHeight()), tint, false, false);
                return;
            }
        }
    }

    private void DrawRegion(Texture2D tex, UiRect dst, UiRect src, Color tint, bool flipX, bool flipY)
    {
        if (!Clip(ref dst, ref src, flipX, flipY)) return;
        var rect = R(dst);
        if (flipX) rect = new Rect2(rect.Position.X + rect.Size.X, rect.Position.Y, -rect.Size.X, rect.Size.Y);
        if (flipY) rect = new Rect2(rect.Position.X, rect.Position.Y + rect.Size.Y, rect.Size.X, -rect.Size.Y);
        DrawTextureRectRegion(tex, rect, R(src), tint);
    }

    private void DrawWidgetText(Widget widget, float alpha)
    {
        var rect = widget.ScreenRect;
        switch (widget)
        {
            case EditBoxWidget edit:
            {
                var focused = ReferenceEquals(Root.Focused, edit);
                var text = edit.DisplayText;
                if (text.Length == 0 && edit.GuideText.Length > 0)
                {
                    // the guide text stays until something is typed; the caret blinks over it when focused
                    var g = edit.GuideTextInset;
                    var inset = new UiInsets(Math.Max(g.Left, edit.TextInset.Left), g.Top + edit.TextInset.Top, Math.Max(g.Right, edit.TextInset.Right), g.Bottom + edit.TextInset.Bottom);
                    DrawTextBlock(edit.GuideText, edit.guideTextStyle, rect, inset, edit.guideTextStyle.Color, alpha, edit.Multiline, edit.EffectiveLineSpace);
                    if (!focused) return;
                }
                var caretX = DrawTextBlock(text, edit.style, rect, edit.TextInset, edit.style.Color, alpha, edit.Multiline, edit.EffectiveLineSpace, edit.Cursor, edit.AllSelected);
                if (focused && ((int)(_caretClock * 2) % 2 == 0) && caretX is { } caret)
                    DrawLine(new Vector2(caret.X, caret.Y + edit.CursorOffset),
                        new Vector2(caret.X, caret.Y + edit.CursorOffset + Math.Max(1, caret.Height + edit.CursorHeightAdjustment)),
                        C(edit.CursorColor, alpha), 1);
                return;
            }
            case TextBoxWidget textBox:
                if (textBox.GetText().Length > 0)
                {
                    DrawTextBlock(textBox.GetText(), textBox.style, rect, textBox.TextInset, textBox.style.Color,
                        alpha, textBox.WrapsText, textBox.EffectiveLineSpace);
                    if (textBox.StrikeThrough || textBox.UnderLine) DrawTextDecorations(textBox, rect, alpha);
                }
                return;
            case GameTooltipWidget tooltip:
            {
                // AddLine carries alignment and icon indentation for each row.  Drawing the
                // concatenated widget text with its default centered style discards both.
                var y = rect.Y + tooltip.TextInset.Top;
                for (var i = 0; i < tooltip.TooltipLines.Count; i++)
                {
                    var line = tooltip.TooltipLines[i];
                    y += line.UpperSpace;
                    var lineStyle = new WidgetStyle(Root);
                    lineStyle.CopyFrom(tooltip.style);
                    lineStyle.SetFontSize(line.Size);
                    lineStyle.SetAlign(line.Align);
                    var height = tooltip.TooltipLineHeight(i);
                    var leftIndent = (int)line.Align % 3 == 0 ? Math.Max(0, line.Indent) : 0;
                    if (line.Text.Length > 0)
                        DrawTextBlock(line.Text, lineStyle, new UiRect(rect.X, y, rect.Width, height),
                            new UiInsets(tooltip.TextInset.Left + leftIndent, 0, tooltip.TextInset.Right, 0),
                            tooltip.style.Color, alpha, tooltip.WrapsText, tooltip.EffectiveLineSpace);
                    if (tooltip is not UnitFrameTooltipWidget &&
                        tooltip.TooltipSideLines.TryGetValue(i + 1, out var side) && side.Text.Length > 0)
                    {
                        lineStyle.SetFontSize(side.Size);
                        lineStyle.SetAlign(UiAlign.Right);
                        DrawTextBlock(side.Text, lineStyle, new UiRect(rect.X, y, rect.Width, height),
                            new UiInsets(tooltip.TextInset.Left, 0, tooltip.TextInset.Right + Math.Max(0, side.Indent), 0),
                            tooltip.style.Color, alpha, false, 0);
                    }
                    y += height + line.LowerSpace;
                }
                return;
            }
            case WindowWidget window when window.TitleText.Length > 0:
                DrawTextBlock(window.TitleText, window.titleStyle, rect, window.TitleInset, window.titleStyle.Color, alpha, false, 0);
                if (widget.GetText().Length > 0)
                    DrawTextBlock(widget.GetText(), widget.style, rect, widget.TextInset, widget.style.Color, alpha, widget.WrapsText, widget.EffectiveLineSpace);
                return;
            case ButtonWidget button:
                if (button.GetText().Length > 0)
                    DrawTextBlock(button.GetText(), button.style, rect, button.TextInset, button.TextColor, alpha, button.AutoWordwrap, button.EffectiveLineSpace);
                return;
            default:
                if (widget.GetText().Length > 0)
                    DrawTextBlock(widget.GetText(), widget.style, rect, widget.TextInset, widget.style.Color, alpha, widget.WrapsText, widget.EffectiveLineSpace);
                return;
        }
    }

    private void DrawTextDecorations(TextBoxWidget box, UiRect rect, float alpha)
    {
        var style = box.style;
        var inset = box.TextInset;
        var inner = new UiRect(rect.X + inset.Left, rect.Y + inset.Top,
            Math.Max(0, rect.Width - inset.Left - inset.Right), Math.Max(0, rect.Height - inset.Top - inset.Bottom));
        var font = _fonts.Get(style.FontKey);
        var px = X2FontCache.PixelSize(style.FontSize);
        var lineHeight = font.GetHeight(px);
        var lines = TextLayout.Lines(Root, style.FontKey, style.FontSize, box.GetText(),
            box.WrapsText ? Math.Max(1, inner.Width) : float.MaxValue);
        var blockHeight = lines.Count * lineHeight + Math.Max(0, lines.Count - 1) * Math.Max(0, box.EffectiveLineSpace);
        var row = (int)style.Align / 3;
        var col = (int)style.Align % 3;
        var y = row == 0 ? inner.Y : row == 2 ? inner.Bottom - blockHeight : inner.Y + (inner.Height - blockHeight) / 2;
        var color = C(style.Color, alpha);
        foreach (var line in lines)
        {
            var x = col == 0 ? inner.X : col == 2 ? inner.Right - line.Width : inner.X + (inner.Width - line.Width) / 2;
            if (line.Width > 0 && LineVisible(y, lineHeight))
            {
                if (box.StrikeThrough) DrawLine(new Vector2(x, y + lineHeight * 0.52f),
                    new Vector2(x + line.Width, y + lineHeight * 0.52f), color, 1);
                if (box.UnderLine) DrawLine(new Vector2(x, y + lineHeight - 1),
                    new Vector2(x + line.Width, y + lineHeight - 1), color, 1);
            }
            y += lineHeight + Math.Max(0, box.EffectiveLineSpace);
        }
    }

    /// <summary>Draws aligned (and optionally wrapped) text; returns the caret rectangle when <paramref name="caret"/> is set.</summary>
    private (float X, float Y, float Height)? DrawTextBlock(string text, WidgetStyle style, UiRect rect, UiInsets inset, UiColor color,
        float alpha, bool wrap, float lineSpace, int caret = -1, bool selected = false)
    {
        var inner = new UiRect(rect.X + inset.Left, rect.Y + inset.Top,
            Math.Max(0, rect.Width - inset.Left - inset.Right), Math.Max(0, rect.Height - inset.Top - inset.Bottom));
        var font = _fonts.Get(style.FontKey);
        var px = X2FontCache.PixelSize(style.FontSize);
        var lineHeight = font.GetHeight(px);
        var ascent = font.GetAscent(px);
        var lines = TextLayout.Lines(Root, style.FontKey, style.FontSize, text, wrap ? Math.Max(1, inner.Width) : float.MaxValue);
        if (lines.Count == 0) lines.Add(new TextLine([], 0));
        var space = lines.Count > 1 ? Math.Max(0, lineSpace) : 0;
        var blockHeight = lines.Count * lineHeight + (lines.Count - 1) * space;
        var row = (int)style.Align / 3;
        var col = (int)style.Align % 3;
        var y = row switch
        {
            0 => inner.Y,
            2 => inner.Bottom - blockHeight,
            _ => inner.Y + (inner.Height - blockHeight) / 2,
        };
        (float, float, float)? caretRect = null;
        var consumed = 0;
        foreach (var line in lines)
        {
            var x = col switch
            {
                0 => inner.X,
                2 => inner.Right - line.Width,
                _ => inner.X + (inner.Width - line.Width) / 2,
            };
            x = MathF.Round(x);
            var baseline = MathF.Round(y + ascent);
            var lineText = line.Plain;
            var lineShown = LineVisible(y, lineHeight);
            if (selected && lineText.Length > 0)
                DrawRect(new Rect2(x, y, line.Width, lineHeight), new Color(0.35f, 0.5f, 0.8f, 0.6f * alpha));
            var cx = x;
            foreach (var run in line.Runs)
            {
                var runColor = run.Color is { } rc ? C(rc, alpha) : C(color, alpha);
                cx += DrawInlineRun(run, font, px, cx, y, lineHeight, baseline, runColor, style.Shadow, lineShown);
            }
            if (caret >= 0 && caretRect == null && caret <= consumed + lineText.Length)
            {
                var before = lineText[..Math.Clamp(caret - consumed, 0, lineText.Length)];
                caretRect = (x + font.GetStringSize(before, HorizontalAlignment.Left, -1, px).X, y, lineHeight);
            }
            consumed += lineText.Length;
            y += lineHeight + space;
        }
        if (caret >= 0 && caretRect == null)
            caretRect = (inner.X, inner.Y + (inner.Height - lineHeight) / 2, lineHeight);
        return caretRect;
    }

    private float DrawInlineRun(TextRun run, Font font, int px, float x, float y, float lineHeight,
        float baseline, Color color, bool shadow, bool visible)
    {
        if (run.Icon is { } key)
        {
            if (visible && Root.Settings.TextureRegion(TextLayout.CurrencyTexture, key)?.Coords is { } src
                && _textures.Get(TextLayout.CurrencyTexture) is { } texture)
                DrawRegion(texture,
                    new UiRect(x, MathF.Round(y + (lineHeight - TextLayout.CurrencyIconSize) / 2), TextLayout.CurrencyIconSize, TextLayout.CurrencyIconSize),
                    src, new Color(1, 1, 1, color.A), false, false);
            return TextLayout.CurrencyIconSize;
        }
        if (visible && shadow)
            DrawString(font, new Vector2(x + 1, baseline + 1), run.Text, HorizontalAlignment.Left, -1, px,
                new Color(0, 0, 0, color.A * 0.8f));
        if (visible) DrawString(font, new Vector2(x, baseline), run.Text, HorizontalAlignment.Left, -1, px, color);
        return font.GetStringSize(run.Text, HorizontalAlignment.Left, -1, px).X;
    }

    // ------------------------------------------------------------------ input

    private Vector2 ToUi(Vector2 p) => p / Math.Max(0.1f, UiScale);

    public override void _GuiInput(InputEvent e)
    {
        var root = _session.Root;
        if (root == null) return;
        root.ShiftDown = Input.IsKeyPressed(Key.Shift);
        switch (e)
        {
            case InputEventMouseMotion motion:
            {
                var p = ToUi(motion.Position);
                root.PointerMove(p.X, p.Y);
                break;
            }
            case InputEventMouseButton mb:
            {
                var p = ToUi(mb.Position);
                if (InWorld)
                {
                    // in the world only clicks on (or started on) a widget belong to the UI
                    var overUi = root.HitTest(p.X, p.Y) != null;
                    if (mb.Pressed && mb.ButtonIndex is MouseButton.Left or MouseButton.Right) _uiCapture = overUi;
                    if (!overUi && !(_uiCapture && !mb.Pressed))
                    {
                        if (mb.Pressed) root.PointerButton(-1, -1, "LeftButton", true);
                        return;
                    }
                    if (!mb.Pressed) _uiCapture = false;
                }
                switch (mb.ButtonIndex)
                {
                    case MouseButton.Left:
                    case MouseButton.Right:
                        if (mb.Pressed) { FocusMode = FocusModeEnum.All; GrabFocus(); }
                        root.PointerButton(p.X, p.Y, mb.ButtonIndex == MouseButton.Left ? "LeftButton" : "RightButton", mb.Pressed);
                        break;
                    case MouseButton.WheelUp when mb.Pressed:
                        root.Wheel(p.X, p.Y, true);
                        break;
                    case MouseButton.WheelDown when mb.Pressed:
                        root.Wheel(p.X, p.Y, false);
                        break;
                }
                AcceptEvent();
                break;
            }
            case InputEventKey { Pressed: true } key:
            {
                _caretClock = 0;
                var handled = key.Keycode switch
                {
                    Key.Enter or Key.KpEnter => root.KeyPress(UiKey.Enter),
                    Key.Tab => root.KeyPress(UiKey.Tab),
                    Key.Escape => root.KeyPress(UiKey.Escape),
                    Key.Backspace => root.KeyPress(UiKey.Backspace),
                    Key.Delete => root.KeyPress(UiKey.Delete),
                    Key.Left => root.KeyPress(UiKey.Left),
                    Key.Right => root.KeyPress(UiKey.Right),
                    Key.Home => root.KeyPress(UiKey.Home),
                    Key.End => root.KeyPress(UiKey.End),
                    Key.A when key.CtrlPressed => root.KeyPress(UiKey.SelectAll),
                    _ => false,
                };
                // in the world, typed characters belong to the UI only while an edit box has focus (else they are hotkeys/movement)
                var typing = !InWorld || root.Focused is EditBoxWidget;
                if (!handled && typing && key.Unicode >= 32 && !key.CtrlPressed && !key.AltPressed)
                {
                    root.TextInput(char.ConvertFromUtf32((int)key.Unicode));
                    handled = true;
                }
                if (handled || (!InWorld && key.Keycode == Key.Tab)) AcceptEvent();
                break;
            }
        }
    }
}

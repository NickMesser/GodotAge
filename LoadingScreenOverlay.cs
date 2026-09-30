#nullable enable

using Godot;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer;

/// <summary>World-entry and teleport cover using the client's world loading art and gauge atlas.</summary>
public partial class LoadingScreenOverlay : CanvasLayer
{
    private const string GaugePath = "game/ui/gauge.dds";
    private const string FontPath = "game/ui/font/yd_ygo540.ttf";

    private Control? _blocker;
    private TextureRect? _background;
    private TextureProgressBar? _gauge;
    private Label? _title;
    private Label? _percent;
    private Label? _tip;
    private Font? _font;
    private List<string> _tips = [];
    private float _progress;

    public override void _Ready()
    {
        Layer = 100;
        BuildControls();
        Visible = false;
    }

    /// <summary>Loads world background art, the loading-gauge atlas, the packaged font, and English tips of the day.</summary>
    public void LoadContent(string databasePath, string worldName)
    {
        if (_background == null)
            return;

        _background.Texture = LoadFirstWorldImage(worldName);
        if (PakFiles.Read(GaugePath) is { } gaugeBytes && LoadImage(gaugeBytes) is { } gaugeImage)
        {
            var atlas = ImageTexture.CreateFromImage(gaugeImage);
            if (_gauge != null)
            {
                _gauge.TextureUnder = Atlas(atlas, new Rect2(0, 21, 796, 20));
                _gauge.TextureProgress = Atlas(atlas, new Rect2(0, 15, 785, 6));
                _gauge.TextureOver = Atlas(atlas, new Rect2(0, 0, 793, 14));
            }
            // The original loading UI uses gauge.dds's 3x64 black_bg as a nine-part frame.
            if (_blocker?.GetNodeOrNull<NinePatchRect>("BottomFrame") is { } frame)
                frame.Texture = Atlas(atlas, new Rect2(796, 0, 3, 64));
        }

        if (PakFiles.Read(FontPath) is { } fontBytes)
        {
            _font = new FontFile
            {
                Data = fontBytes,
                Antialiasing = TextServer.FontAntialiasing.Gray,
                Hinting = TextServer.Hinting.Light,
                SubpixelPositioning = TextServer.SubpixelPositioning.Disabled,
            };
            foreach (var label in new[] { _title, _percent, _tip })
                label?.AddThemeFontOverride("font", _font);
        }

        _tips = ReadEnglishTips(databasePath);
        if (Visible)
            PickTip();
    }

    /// <summary>Shows the overlay while the initial world cells and their scene work load.</summary>
    public void BeginInitialLoad()
    {
        _progress = 0f;
        if (_gauge != null) _gauge.Value = 0;
        if (_percent != null) _percent.Text = "0%";
        if (_title != null) _title.Text = "Loading";
        Visible = true;
        PickTip();
    }

    /// <summary>Shows the overlay again while cells around a server teleport destination load.</summary>
    public void BeginTeleport()
    {
        BeginInitialLoad();
        if (_title != null) _title.Text = "Entering world";
    }

    public void SetProgress(float progress)
    {
        _progress = Mathf.Max(_progress, Mathf.Clamp(progress, 0f, 1f));
        if (_gauge != null) _gauge.Value = _progress * 100f;
        if (_percent != null) _percent.Text = $"{_progress * 100f:0}%";
    }

    public void Finish() => Visible = false;

    public void ShowFailure(string message)
    {
        if (_title != null) _title.Text = string.IsNullOrWhiteSpace(message) ? "World loading failed" : message;
        if (_tip != null) _tip.Text = "Check the viewer log for the loading error.";
        Visible = true;
    }

    private void BuildControls()
    {
        _blocker = new Control { Name = "LoadingBlocker", MouseFilter = Control.MouseFilterEnum.Stop };
        _blocker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_blocker);

        _background = new TextureRect
        {
            Name = "WorldArt",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _blocker.AddChild(_background);

        var fallback = new ColorRect { Color = new Color(0.025f, 0.03f, 0.04f, 1f), MouseFilter = Control.MouseFilterEnum.Ignore };
        fallback.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _blocker.AddChild(fallback);
        _background.ZIndex = 1;

        var frame = new NinePatchRect
        {
            Name = "BottomFrame",
            PatchMarginLeft = 1,
            PatchMarginTop = 58,
            PatchMarginRight = 1,
            PatchMarginBottom = 5,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        frame.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        frame.OffsetTop = -180;
        frame.OffsetBottom = 0;
        frame.ZIndex = 2;
        _blocker.AddChild(frame);

        _title = MakeLabel("Loading", 18, Colors.White);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        PlaceBottomCentered(_title, -405, -183, 405, -157);
        _blocker.AddChild(_title);

        _gauge = new TextureProgressBar
        {
            Name = "ProgressGauge",
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            FillMode = (int)TextureProgressBar.FillModeEnum.LeftToRight,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        PlaceBottomCentered(_gauge, -399, -151, 399, -129);
        _blocker.AddChild(_gauge);

        _percent = MakeLabel("0%", 18, new Color(179f / 255f, 103f / 255f, 19f / 255f));
        _percent.HorizontalAlignment = HorizontalAlignment.Left;
        PlaceBottomCentered(_percent, 405, -151, 500, -129);
        _blocker.AddChild(_percent);

        _tip = MakeLabel("", 13, new Color(205f / 255f, 154f / 255f, 97f / 255f));
        _tip.HorizontalAlignment = HorizontalAlignment.Center;
        _tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        PlaceBottomCentered(_tip, -620, -122, 620, -72);
        _blocker.AddChild(_tip);
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var label = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, size + 8),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.025f, 0.01f, 0.9f));
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeConstantOverride("outline_size", 2);
        return label;
    }

    private static void PlaceBottomCentered(Control control, float left, float top, float right, float bottom)
    {
        control.AnchorLeft = 0.5f;
        control.AnchorRight = 0.5f;
        control.AnchorTop = 1f;
        control.AnchorBottom = 1f;
        control.OffsetLeft = left;
        control.OffsetTop = top;
        control.OffsetRight = right;
        control.OffsetBottom = bottom;
        control.ZIndex = 2;
    }

    private Texture2D? LoadFirstWorldImage(string worldName)
    {
        var root = $"game/worlds/{worldName.Trim().Replace('\\', '/').Trim('/')}/loading";
        for (var i = 1; i <= 5; i++)
            if (PakFiles.Read($"{root}/worldloading_{i}.dds") is { } bytes && LoadImage(bytes) is { } image)
                return ImageTexture.CreateFromImage(image);
        if (PakFiles.Read($"game/worlds/{worldName.Trim().Replace('\\', '/').Trim('/')}/loading.dds") is { } fallback && LoadImage(fallback) is { } fallbackImage)
            return ImageTexture.CreateFromImage(fallbackImage);
        return null;
    }

    private static Image? LoadImage(byte[] bytes)
    {
        var image = new Image();
        return image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) == Error.Ok && !image.IsEmpty() ? image : null;
    }

    private static AtlasTexture Atlas(Texture2D atlas, Rect2 region) => new() { Atlas = atlas, Region = region };

    private static List<string> ReadEnglishTips(string databasePath)
    {
        if (!File.Exists(databasePath))
            return [];
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(NULLIF(l.en_us,''), t.text)
                  FROM tip_of_days t
                  LEFT JOIN localized_texts l
                    ON l.tbl_name='tip_of_days' AND l.tbl_column_name='text' AND l.idx=t.id
                 WHERE COALESCE(NULLIF(l.en_us,''), t.text) IS NOT NULL
                 ORDER BY t.id;
                """;
            using var reader = command.ExecuteReader();
            var tips = new List<string>();
            while (reader.Read())
            {
                var value = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
                if (value.Length > 0) tips.Add(value);
            }
            return tips;
        }
        catch (SqliteException e)
        {
            GD.PrintErr($"Loading tips unavailable: {e.Message}");
            return [];
        }
    }

    private void PickTip()
    {
        if (_tip == null)
            return;
        _tip.Text = _tips.Count > 0 ? _tips[Random.Shared.Next(_tips.Count)] : "Welcome to ArcheAge.";
    }
}

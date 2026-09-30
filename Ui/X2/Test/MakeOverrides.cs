using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// Regenerates the English texture overrides (Ui/X2/Overrides) for pak textures with baked-in Chinese text:
/// godot --path &lt;project&gt; res://Ui/X2/Test/MakeOverrides.tscn -- [pak=&lt;path to game_pak&gt;] [out=&lt;dir&gt;]
/// Each entry clears the text band of the original texture and renders the English text there with a pak font,
/// keeping the texture size so the .g coordinates still match. Output: &lt;out&gt;/game/&lt;pak path without .dds&gt;.png.
/// </summary>
public partial class MakeOverrides : Node
{
    private sealed record Entry(string Path, string Text, string Font, int Size, float CenterX, float CenterY, int Spacing);

    // 000_login.dds: region "logo" (0,0,206,202); the Chinese title "上古世纪 归来" sits under the emblem.
    private static readonly Entry[] Entries =
    [
        new("ui/login_stage/000_login.dds", "ARCHEAGE", "ui/font/librebaskerville-bold.ttf", 27, 103, 172, 3),
    ];

    private readonly Dictionary<string, string> _args = new(StringComparer.OrdinalIgnoreCase);
    private int _index = -1;
    private SubViewport _viewport;
    private int _frames;

    public override void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var kv = arg.TrimStart('-').Split('=', 2);
            _args[kv[0]] = kv.Length > 1 ? kv[1] : "1";
        }
        if (!PakFiles.Open(ClientPaths.Pak))
        {
            GD.PrintErr("MakeOverrides: cannot open the pak");
            GetTree().Quit(1);
            return;
        }
        NextEntry();
    }

    private void NextEntry()
    {
        _viewport?.QueueFree();
        _index++;
        if (_index >= Entries.Length)
        {
            GetTree().Quit();
            return;
        }
        var e = Entries[_index];
        var image = new Image();
        if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(PakFiles.Read("game/" + e.Path))) != Error.Ok)
        {
            GD.PrintErr($"MakeOverrides: cannot load {e.Path}");
            NextEntry();
            return;
        }
        if (image.IsCompressed()) image.Decompress();
        image.Convert(Image.Format.Rgba8);
        image.ClearMipmaps();
        var clearFrom = TextBandTop(image, (int)e.CenterY);
        for (var y = clearFrom; y < image.GetHeight(); y++)
            for (var x = 0; x < image.GetWidth(); x++)
                image.SetPixel(x, y, new Color(0, 0, 0, 0));

        var fontFile = new FontFile { Data = PakFiles.Read("game/" + e.Font) };
        var font = new FontVariation { BaseFont = fontFile, SpacingGlyph = e.Spacing };
        _viewport = new SubViewport
        {
            Size = new Vector2I(image.GetWidth(), image.GetHeight()),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        _viewport.AddChild(new TextureRect { Texture = ImageTexture.CreateFromImage(image), Size = _viewport.Size });
        var settings = new LabelSettings
        {
            Font = font,
            FontSize = e.Size,
            FontColor = new Color(1, 1, 1, 0.95f),
            OutlineSize = 3,
            OutlineColor = new Color(0.05f, 0.05f, 0.08f, 0.35f),
            ShadowSize = 4,
            ShadowColor = new Color(0, 0, 0, 0.35f),
            ShadowOffset = Vector2.Zero,
        };
        var textSize = font.GetStringSize(e.Text, HorizontalAlignment.Left, -1, e.Size);
        var label = new Label { Text = e.Text, LabelSettings = settings };
        label.Position = new Vector2(e.CenterX - textSize.X / 2, e.CenterY - textSize.Y / 2);
        _viewport.AddChild(label);
        AddChild(_viewport);
        _frames = 0;
        GD.Print($"MakeOverrides: {e.Path}: cleared rows {clearFrom}..{image.GetHeight() - 1}, drawing '{e.Text}'");
    }

    /// <summary>The emptiest row in the 30 rows above the text: the gap between the emblem and the text.</summary>
    private static int TextBandTop(Image image, int textCenterY)
    {
        var best = textCenterY - 20;
        var bestSum = float.MaxValue;
        for (var y = textCenterY - 40; y <= textCenterY - 12; y++)
        {
            var sum = 0f;
            for (var x = 0; x < image.GetWidth(); x++) sum += image.GetPixel(x, y).A;
            if (sum < bestSum) { bestSum = sum; best = y; }
        }
        return best;
    }

    public override void _Process(double delta)
    {
        if (_viewport == null || ++_frames < 4) return;
        var e = Entries[_index];
        var output = _args.GetValueOrDefault("out", ProjectSettings.GlobalizePath("res://Ui/X2/Overrides"));
        var file = System.IO.Path.Combine(output, "game", System.IO.Path.ChangeExtension(e.Path, ".png"));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        _viewport.GetTexture().GetImage().SavePng(file);
        GD.Print($"MakeOverrides: wrote {file}");
        NextEntry();
    }
}

using Godot;
namespace AAEmu.GodotViewer.Data;
public partial class DataTest : Control
{
    private int _frames;
    public override void _Ready()
    {
        PakFiles.Open(ClientPaths.Pak);
        var data = new GameData(ClientPaths.Database);
        var icons = new IconLibrary(data);
        var rnd = new System.Random(3);
        int shown = 0, english = 0, tried = 0;
        while (shown < 60 && tried < 2000)
        {
            tried++;
            var item = data.GetItem(rnd.Next(1, 40000));
            if (item == null) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(item.Name, "^[\x20-\x7E]*$")) english++;
            var tex = item.IconId is int iid ? icons.GetIcon(iid) : null;
            if (tex == null) continue;
            var x = shown % 10 * 150 + 10; var y = shown / 10 * 140 + 10;
            AddChild(new TextureRect { Texture = tex, Position = new Vector2(x + 40, y), Size = new Vector2(48, 48), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize });
            if (icons.GetGradeFrame(item.GradeId) is { } frame) AddChild(new TextureRect { Texture = frame, Position = new Vector2(x + 40, y), Size = new Vector2(48, 48), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize });
            AddChild(new Label { Text = item.Name, Position = new Vector2(x, y + 52), Size = new Vector2(145, 60), AutowrapMode = TextServer.AutowrapMode.Word, HorizontalAlignment = HorizontalAlignment.Center });
            shown++;
        }
        GD.Print($"DATATEST items tried with rows: english-ascii names {english}; shown {shown}");
        var sk = data.GetSkill(2); GD.Print($"DATATEST skill 2: {sk?.Name}");
    }
    public override void _Process(double d)
    {
        if (++_frames == 10) { GetViewport().GetTexture().GetImage().SavePng("res://datatest.png"); GetTree().Quit(); }
    }
}

#nullable enable

using AAEmu.GodotViewer.Net;
using Godot;

namespace AAEmu.GodotViewer.Client;

/// <summary>A fixed pool of billboard combat numbers using the original client's pak font.</summary>
internal sealed partial class CombatFloatingText : Node3D
{
    private const int PoolSize = 30; // COMBAT_TEXT_MAX_COUNT in game/scripts/x2ui/combattext/combat_text.lua.
    private const float Lifetime = 1.25f;
    private readonly List<Entry> _pool = [];
    private int _cursor;
    private Font? _font;

    public override void _Ready()
    {
        TopLevel = true;
        _font = LoadFont();
        for (var i = 0; i < PoolSize; i++)
        {
            var label = new Label3D
            {
                Name = $"CombatText{i:00}",
                Font = _font,
                FontSize = 36,
                PixelSize = 0.012f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                OutlineSize = 5,
                OutlineModulate = new Color(0.015f, 0.015f, 0.02f, 0.95f),
                Visible = false,
            };
            AddChild(label);
            _pool.Add(new Entry(label));
        }
    }

    public void Show(ITargetableRegistry registry, uint unitId, float height, string text, Color color)
    {
        var unit = registry.All.FirstOrDefault(candidate => candidate.Id == unitId);
        if (unit is null || !GodotObject.IsInstanceValid(unit.Node) || !unit.Node.IsInsideTree())
            return;
        ShowAt(unit.Node.GlobalPosition + Vector3.Up * Mathf.Max(0.8f, height + 0.25f), text, color);
    }

    public void ShowAt(Vector3 position, string text, Color color)
    {
        if (string.IsNullOrWhiteSpace(text) || _pool.Count == 0)
            return;
        var entry = _pool[_cursor++ % _pool.Count];
        entry.Label.Text = text;
        entry.Label.Modulate = color;
        entry.Label.GlobalPosition = position;
        entry.Label.Visible = true;
        entry.Elapsed = 0f;
        entry.Start = position;
    }

    public override void _Process(double delta)
    {
        var step = (float)delta;
        foreach (var entry in _pool)
        {
            if (!entry.Label.Visible)
                continue;
            entry.Elapsed += step;
            if (entry.Elapsed >= Lifetime)
            {
                entry.Label.Visible = false;
                continue;
            }
            var fraction = Mathf.Clamp(entry.Elapsed / Lifetime, 0f, 1f);
            entry.Label.GlobalPosition = entry.Start + Vector3.Up * (fraction * 0.85f);
            entry.Label.Modulate = entry.Label.Modulate with { A = 1f - fraction };
        }
    }

    private static Font? LoadFont()
    {
        var bytes = PakFiles.Read("game/ui/font/roboto.ttf")
                    ?? PakFiles.Read("game/ui/font/tahoma_regular_font.ttf");
        if (bytes == null)
            return ThemeDB.FallbackFont;
        var font = new FontFile
        {
            Data = bytes,
            Antialiasing = TextServer.FontAntialiasing.Gray,
        };
        return font;
    }

    private sealed class Entry(Label3D label)
    {
        public Label3D Label { get; } = label;
        public Vector3 Start { get; set; }
        public float Elapsed { get; set; }
    }
}

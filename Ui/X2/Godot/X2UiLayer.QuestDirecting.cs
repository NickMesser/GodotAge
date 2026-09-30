#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

// Quest directing mode (the NPC quest dialog cinema). While X2Quest:EnterQuestDirectingMode() is in effect the client
// switches the key bindings to default_binding.g's "quest_directing_mode_mode" (d 1 = previous, f 2 = next, g 3 = force)
// and reports the keys as QUEST_DIRECTING_MODE_HOT_KEY(n) to quest_context_directing.lua; Escape ends the mode
// (QUEST_DIRECTING_MODE_END -> CancelQuestDirectingMode). The world's nameplates, overhead markers and hover tooltip
// are hidden meanwhile, as in the original.
public partial class X2UiLayer
{
    private bool _questDirectingMode;
    private readonly List<(Node Node, bool Visible)> _directingHidden = [];

    private void SetQuestDirectingMode(bool enabled)
    {
        if (_questDirectingMode == enabled) return;
        _questDirectingMode = enabled;
        if (enabled)
        {
            _directingHidden.Clear();
            var root = GetTree().Root;
            foreach (var name in new[] { "TargetingVisuals", "OverheadPresentation" })
                if (root.FindChild(name, true, false) is { } node) HideForDirecting(node);
            foreach (var hover in FindAll<Client.Targeting.WorldHoverPresentation>(root)) HideForDirecting(hover);
        }
        else
        {
            foreach (var (node, visible) in _directingHidden)
                if (GodotObject.IsInstanceValid(node)) SetVisible(node, visible);
            _directingHidden.Clear();
        }
        QuestDirectingModeChanged?.Invoke(enabled);
    }

    private void HideForDirecting(Node node)
    {
        var visible = node switch { Node3D n => n.Visible, CanvasItem c => c.Visible, CanvasLayer l => l.Visible, _ => true };
        _directingHidden.Add((node, visible));
        SetVisible(node, false);
    }

    private static void SetVisible(Node node, bool visible)
    {
        switch (node)
        {
            case Node3D n: n.Visible = visible; break;
            case CanvasItem c: c.Visible = visible; break;
            case CanvasLayer l: l.Visible = visible; break;
        }
    }

    private static IEnumerable<T> FindAll<T>(Node node) where T : Node
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T t) yield return t;
            foreach (var x in FindAll<T>(child)) yield return x;
        }
    }

    /// <summary>Directing-mode keys, before the world sees them (F would otherwise interact, D walk).</summary>
    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) _loginSkipRequested = true;
        if (!_questDirectingMode || e is not InputEventKey { Pressed: true } key) return;
        if (Root.Focused is EditBoxWidget edit && edit.IsEffectivelyVisible()) return;
        var hotkey = key.Keycode switch { Key.D => 1, Key.F => 2, Key.G => 3, _ => 0 };
        if (hotkey != 0)
        {
            if (!key.Echo) _session.Root?.DispatchEvent("QUEST_DIRECTING_MODE_HOT_KEY", (double)hotkey);
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Escape && !key.Echo)
        {
            _session.Root?.DispatchEvent("QUEST_DIRECTING_MODE_END");
            GetViewport().SetInputAsHandled();
        }
    }
}

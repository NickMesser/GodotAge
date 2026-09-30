#nullable enable
using AAEmu.GodotViewer.Settings;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>Key binding text in the client's own format ("SHIFT-1", "CTRL-f"), as the x2ui scripts expect it.</summary>
public static class X2KeyBindingText
{
    public const int SlotAction = 254;     // ISLOT_ACTION
    public const int SlotModeAction = 246; // ISLOT_MODE_ACTION

    public static string? Get(KeyBindings bindings, string action, string? argument, int which = 1)
    {
        foreach (var b in bindings.All)
        {
            if (b.Action != action || (argument != null && b.Argument != argument)) continue;
            return Format(which == 2 ? b.Secondary : b.Primary);
        }
        return null;
    }

    public static string? ForSlot(KeyBindings bindings, int slotType, int index) => slotType switch
    {
        SlotAction => Get(bindings, "action_bar_button", index.ToString()),
        SlotModeAction => Get(bindings, "mode_action_bar_button", index.ToString()),
        _ => null,
    };

    public static string? Format(BindingChord? chord)
        => chord == null ? null : string.Concat(chord.Ctrl ? "CTRL-" : "", chord.Alt ? "ALT-" : "", chord.Shift ? "SHIFT-" : "", chord.Key);
}

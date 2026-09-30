#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

// In-game hotkeys of the client's UI actions (default_binding.g: i toggle_bag, k toggle_spellbook, escape open_config, ...).
// Keys the UI does not consume (no focused edit box) arrive here; the ones bound to a UI action open/close their window.
// Movement, targeting and skill keys are left to the world (they are not UI actions).
public partial class X2UiLayer
{
    /// <summary>The engine's window toggles: binding action -> UIC content id.</summary>
    private static readonly Dictionary<string, string> ContentActions = new()
    {
        ["toggle_bag"] = "UIC_BAG",
        ["toggle_character"] = "UIC_CHARACTER_INFO",
        ["toggle_spellbook"] = "UIC_SKILL",
        ["toggle_quest"] = "UIC_QUEST_LIST",
        ["toggle_worldmap"] = "UIC_WORLDMAP",
        ["toggle_craft_book"] = "UIC_CRAFT_BOOK",
        ["toggle_common_farm_info"] = "UIC_MY_FARM_INFO",
        ["toggle_achievement"] = "UIC_ACHIEVEMENT",
        ["toggle_community"] = "UIC_COMMUNITY",
        ["toggle_post"] = "UIC_MAIL",
        ["toggle_auction"] = "UIC_AUCTION",
        ["toggle_chronicle_book"] = "UIC_CHRONICLE_BOOK_WND",
        ["toggle_butler_info"] = "UIC_BUTLER_INFO",
        ["toggle_ingameshop"] = "UIC_INGAME_SHOP",
        ["toggle_raid_team_manager"] = "UIC_RAID_TEAM_MANAGER",
        ["toggle_battle_field"] = "UIC_REQUEST_BATTLEFIELD",
        // community tabs: the community trigger picks the tab from data.uic (community.lua tabMapping)
        ["toggle_community_expedition_tab"] = "UIC_COMMUNITY, {uic = UIC_EXPEDITION}",
        ["toggle_community_family_tab"] = "UIC_COMMUNITY, {uic = UIC_FAMILY}",
        ["toggle_community_faction_tab"] = "UIC_COMMUNITY, {uic = UIC_NATION}",
        // bindable in the options (main menu table.lua hotkeys) though not bound by default
        ["toggle_friend"] = "UIC_COMMUNITY",
        ["toggle_ranking"] = "UIC_RANK",
        ["toggle_hero"] = "UIC_HERO",
        ["toggle_specialty_info"] = "UIC_SPECIALTY_INFO",
    };

    /// <summary>Raised for bound actions the UI does not handle itself (the host may act on them).</summary>
    public event Action<string, string?>? HotkeyAction;

    /// <summary>
    /// In the world the layer holds keyboard focus only while an edit box (chat line, search field) has the UI's focus;
    /// otherwise keys such as Tab, WASD and the hotkeys must reach the world and the unhandled-input hotkeys.
    /// </summary>
    private void KeepWorldFocus()
    {
        if (!InWorld) return;
        var typing = Root.Focused is EditBoxWidget edit && edit.IsEffectivelyVisible();
        if (typing) return;
        if (HasFocus()) ReleaseFocus();
        FocusMode = FocusModeEnum.None;
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (!InWorld || e is not InputEventKey { Pressed: true, Echo: false } key || _session.Host is not { } host) return;
        if (Bindings is not { } bindings) return;
        var name = ClientKeyName(key.Keycode);
        if (name == null) return;
        foreach (var b in bindings.All)
        {
            if (!Matches(b.Primary, name, key) && !Matches(b.Secondary, name, key)) continue;
            if (RunUiAction(host, b.Action, b.Argument))
            {
                GetViewport().SetInputAsHandled();
                return;
            }
            HotkeyAction?.Invoke(b.Action, b.Argument);
            return;
        }
    }

    private bool RunUiAction(Scripting.X2LuaHost host, string action, string? argument)
    {
        if (ContentActions.TryGetValue(action, out var uic))
            return host.RunString($"ADDON:ToggleContent({uic})", "=hotkey");
        if (action == "action_bar_button" && int.TryParse(argument, out var slot))
            return UseActionSlot(slot); // the bar's contents decide (skill, item, ...), as in the client
        if (action == "open_chat")
        {
            // Enter opens the chat input line; the layer takes keyboard focus so the typed text reaches it
            var chat = Root.PaintOrder().OfType<ChatWindowWidget>().LastOrDefault(w => w.IsEffectivelyVisible());
            if (chat == null) return false;
            chat.ActivateInput();
            FocusMode = FocusModeEnum.All;
            GrabFocus();
            return true;
        }
        if (action == "open_config")
        {
            // Escape closes the frontmost open window (one with a title bar), else toggles the game menu
            var top = Root.TopLevel.Where(w => w.Visible && w is WindowWidget && w.GetChildByName("titleBar") != null)
                .OrderByDescending(w => w.RaiseOrder).FirstOrDefault();
            if (top != null) { top.Show(false); return true; }
            return host.RunString("ADDON:ToggleContent(UIC_SYSTEM_CONFIG_FRAME)", "=hotkey");
        }
        return false;
    }

    private static bool Matches(Settings.BindingChord? chord, string name, InputEventKey key)
        => chord != null && chord.Device == Settings.BindingDevice.Keyboard && chord.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
           && chord.Shift == key.ShiftPressed && chord.Ctrl == key.CtrlPressed && chord.Alt == key.AltPressed;

    /// <summary>The client's key name (default_binding.g spelling) of a Godot key.</summary>
    private static string? ClientKeyName(Key k) => k switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (k - Key.A))).ToString(),
        >= Key.Key0 and <= Key.Key9 => ((char)('0' + (k - Key.Key0))).ToString(),
        >= Key.F1 and <= Key.F12 => "f" + (k - Key.F1 + 1),
        Key.Escape => "escape",
        Key.Enter or Key.KpEnter => "enter",
        Key.Tab => "tab",
        Key.Space => "space",
        Key.Minus => "minus",
        Key.Equal => "equals",
        Key.Period => ".",
        Key.Comma => ",",
        Key.Slash => "/",
        Key.Insert => "insert",
        Key.Delete => "delete",
        Key.Home => "home",
        Key.End => "end",
        Key.Numlock => "numlock",
        _ => null,
    };
}

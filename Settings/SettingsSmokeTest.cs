using Godot;

namespace AAEmu.GodotViewer.Settings;

public partial class SettingsSmokeTest : Node
{
    public override void _Ready()
    {
        const string path = "user://settings_smoke.cfg";
        try
        {
            // the default key bindings come from the client's pak
            if (!PakFiles.Open(ClientPaths.Pak))
                throw new InvalidOperationException($"cannot open {ClientPaths.Pak}");
            var first = new OptionStore(path);
            first.Set("OIT_R_DESIREWIDTH", 1920);
            first.Set("OIT_S_MUSICVOLUME", 0.35f);
            Check(first.Save(), "option save");

            var bindings = new KeyBindings(path);
            bindings.LoadDefaults();
            // any action that has a single binding row
            var target = bindings.All.First(b => bindings.All.Count(other => other.Action == b.Action) == 1);
            bindings.Rebind(target.Action, new InputEventKey { PhysicalKeycode = Key.J });
            Check(bindings.Save(), "keybinding save");

            var second = new OptionStore(path);
            Check(second.Load(), "option load");
            if (second.Get<int>("OIT_R_DESIREWIDTH") != 1920 || Math.Abs(second.Get<float>("OIT_S_MUSICVOLUME") - 0.35f) > 0.001f)
                throw new InvalidOperationException("Option round-trip mismatch.");
            var reloaded = new KeyBindings(path);
            Check(reloaded.Load(), "keybinding load");
            var jump = InputMap.ActionGetEvents(target.InputAction).OfType<InputEventKey>().Single();
            if (jump.PhysicalKeycode != Key.J) throw new InvalidOperationException("Binding round-trip mismatch.");
            foreach (var (action, events) in reloaded.Snapshot())
                GD.Print($"INPUT {action} = {string.Join(" | ", events.Select(e => e.AsText()))}");
            GD.Print("SETTINGS_SMOKE_PASS options=2 actions=", reloaded.Snapshot().Count);
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError("SETTINGS_SMOKE_FAIL " + exception);
            GetTree().Quit(1);
        }
    }

    private static void Check(Error error, string operation)
    {
        if (error != Error.Ok) throw new InvalidOperationException($"{operation}: {error}");
    }
}

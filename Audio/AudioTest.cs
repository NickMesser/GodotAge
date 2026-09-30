using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// Manual integration smoke test.  Copy AudioTest.tscn and this file with the
/// Audio folder, then run the scene after the client data paths are available.
/// </summary>
public partial class AudioTest : Node3D
{
    [Export] public string GamePakPath { get; set; } = ClientPaths.Pak;
    /// <summary>Empty = <see cref="ClientPaths.Database"/> (resolved once the pak is open).</summary>
    [Export] public string DatabasePath { get; set; } = "";

    public override async void _Ready()
    {
        if (!PakFiles.Open(GamePakPath))
        {
            GD.PrintErr($"AudioTest: PakFiles.Open failed for '{GamePakPath}'. Configure the GamePakPath export.");
            return;
        }
        if (DatabasePath.Length == 0)
            DatabasePath = ClientPaths.Database;
        if (!File.Exists(DatabasePath))
        {
            GD.PrintErr($"AudioTest: client database missing at '{DatabasePath}'. Configure the DatabasePath export or the db setting.");
            return;
        }

        var director = new AudioDirector { Name = "AudioDirector" };
        director.Configure(DatabasePath, PakFiles.Read);
        AddChild(director);

        // zones.id=179 has zones.zone_key=258 and its zone_group=24 uses
        // sound_pack 122 (soundmood_e_tiger_spine_mountains).
        GD.Print($"AudioTest zone 179: {director.OnZoneChanged(258, 688)}");
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);

        // Pack items crime_records_show and ruling_status_show resolve to
        // hammer_2time and hammer_3time, plus the direct submenu_show event.
        foreach (var name in new[] { "crime_records_show", "ruling_status_show", "submenu_show" })
        {
            GD.Print($"AudioTest UI '{name}': {director.PlayUi(name) != null}");
            await ToSignal(GetTree().CreateTimer(0.7), SceneTreeTimer.SignalName.Timeout);
        }

        // Each is an fx_items FxSound row whose asset_name is an event path.
        foreach (var fxId in new uint[] { 1415, 1449, 1533 })
        {
            GD.Print($"AudioTest skill fx {fxId}: {director.PlaySkillFx(fxId, this) != null}");
            await ToSignal(GetTree().CreateTimer(0.7), SceneTreeTimer.SignalName.Timeout);
        }
        GD.Print("AudioTest complete. If a sound is false, inspect its preceding FSB/FEV warning for the unresolved bank or codec.");
    }
}

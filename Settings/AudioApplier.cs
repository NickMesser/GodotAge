using Godot;

namespace AAEmu.GodotViewer.Settings;

public sealed class AudioApplier
{
    public static IReadOnlyDictionary<string, string> BusOptions { get; } = new Dictionary<string, string>
    {
        ["Master"] = "OIT_S_GAMEMASTERVOLUME", ["Music"] = "OIT_S_MUSICVOLUME",
        ["Effects"] = "OIT_S_SFXVOLUME", ["Ambient"] = "OIT_S_MIDIVOLUME",
        ["Voice"] = "OIT_S_CINEMAVOLUME", ["UI"] = "VIEWER_AUDIO_UI_VOLUME"
    };

    public void Apply(OptionStore options)
    {
        var mute = options.Get<bool>("VIEWER_AUDIO_MUTE");
        foreach (var (bus, option) in BusOptions)
        {
            var index = EnsureBus(bus);
            var linear = options.Get<float>(option);
            AudioServer.SetBusVolumeDb(index, linear <= 0 ? -80 : Mathf.LinearToDb(linear));
            AudioServer.SetBusMute(index, mute || linear <= 0);
        }
    }

    private static int EnsureBus(string name)
    {
        var index = AudioServer.GetBusIndex(name);
        if (index >= 0) return index;
        AudioServer.AddBus();
        index = AudioServer.BusCount - 1;
        AudioServer.SetBusName(index, name);
        return index;
    }
}

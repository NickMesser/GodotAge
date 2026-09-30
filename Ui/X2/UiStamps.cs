#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// The client's saved UI stamps (UI:Get/SetAccountUITimeStamp and similar): day stamps that make once-a-day popups
/// (the event center) appear once per day instead of on every login. Kept in a small file next to the viewer's
/// local application data, as the original keeps them in the account's UI save data.
/// </summary>
internal static class UiStamps
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AAEmuGodotViewer", "ui_stamps.txt");
    private static Dictionary<string, double>? _stamps;

    public static double Get(string key) => Load().TryGetValue(key, out var value) ? value : 0;

    public static void Set(string key, double value)
    {
        Load()[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, _stamps!.Select(p => $"{p.Key}={p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, double> Load()
    {
        if (_stamps != null) return _stamps;
        _stamps = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(FilePath))
                foreach (var line in File.ReadAllLines(FilePath))
                    if (line.IndexOf('=') is > 0 and var eq &&
                        double.TryParse(line[(eq + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        _stamps[line[..eq]] = v;
        }
        catch (IOException) { }
        return _stamps;
    }
}

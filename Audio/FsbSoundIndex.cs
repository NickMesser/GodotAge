using Godot;

namespace AAEmu.GodotViewer;

using Audio;

/// <summary>Indexes the named samples in FEV-declared FSB banks without decoding them.</summary>
public sealed class FsbSoundIndex
{
    private readonly Func<string, byte[]?> _readPak;
    private readonly Dictionary<string, Fsb5Decoder?> _banks = new(StringComparer.OrdinalIgnoreCase);

    public FsbSoundIndex(Func<string, byte[]?> readPak) => _readPak = readPak;

    public IReadOnlyList<SoundEventReference> Resolve(string group, IEnumerable<string> banks, IEnumerable<string> aliases)
    {
        var keys = aliases.Select(Normalize).Where(key => key.Length > 0).Distinct().ToArray();
        var result = new List<SoundEventReference>();
        foreach (var bankName in banks.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = $"game/sounds/{group}/{bankName}.fsb";
            var bank = GetBank(path);
            if (bank == null) continue;
            foreach (var sample in bank.Samples)
            {
                var sampleKey = Normalize(sample.Name);
                if (!keys.Any(key => Matches(sampleKey, key))) continue;
                result.Add(new SoundEventReference(path, sample.Name, sample.Index));
            }
        }
        return result.Distinct().ToArray();
    }

    private Fsb5Decoder? GetBank(string path)
    {
        if (_banks.TryGetValue(path, out var bank)) return bank;
        try { bank = _readPak(path) is { } bytes ? Fsb5Decoder.Parse(bytes) : null; }
        catch (Exception error) { GD.PushWarning($"Could not index FSB '{path}': {error.Message}"); bank = null; }
        _banks[path] = bank;
        return bank;
    }

    private static bool Matches(string sample, string key) => sample == key ||
        sample.EndsWith('_' + key, StringComparison.Ordinal) || key.EndsWith('_' + sample, StringComparison.Ordinal) ||
        (key.Length >= 6 && sample.Contains(key, StringComparison.Ordinal));

    private static string Normalize(string name) => name.Trim().ToLowerInvariant()
        .Replace('/', '_').Replace('\\', '_').Replace('.', '_').Replace(':', '_').Replace('-', '_');
}

using System.Buffers.Binary;
using System.Text;

namespace AAEmu.GodotViewer;

/// <summary>
/// Reads the useful metadata from an FMOD Designer FEV file.  FEV is a RIFF
/// project file: audio lives in sibling FSB banks, while the project names its
/// banks and event/sample names in the LGCY/STRR chunks.
/// </summary>
public sealed class FmodFevProject
{
    private FmodFevProject(string name, IReadOnlyList<string> banks, IReadOnlyList<string> names)
    {
        Name = name;
        Banks = banks;
        Names = names;
    }

    public string Name { get; }
    public IReadOnlyList<string> Banks { get; }
    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<string> SourceWaves => Names.Where(name => name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).ToArray();

    public bool ContainsName(string name) => Names.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static FmodFevProject Parse(ReadOnlySpan<byte> bytes, string sourcePath = "")
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("FEV "u8))
            throw new InvalidDataException($"'{sourcePath}' is not an FMOD Designer FEV RIFF file.");

        var strings = new List<string>();
        ReadChunks(bytes, 12, bytes.Length, strings);
        var unique = strings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var name = unique.FirstOrDefault(value => value.StartsWith("x2", StringComparison.OrdinalIgnoreCase) &&
                                                   !value.Contains("_bank", StringComparison.OrdinalIgnoreCase))
                   ?? Path.GetFileNameWithoutExtension(sourcePath);
        // Designer projects commonly have named banks such as x2interface_global
        // in addition to x2interface_bank01.  Bank identifiers are simple root
        // strings, never source paths or event paths.
        var banks = unique.Where(value => value.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                                          !value.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                          !value.Contains('/') && !value.Contains('\\') &&
                                          value.All(c => char.IsLetterOrDigit(c) || c == '_'))
            .Select(value => Path.GetFileNameWithoutExtension(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new FmodFevProject(name, banks, unique);
    }

    private static void ReadChunks(ReadOnlySpan<byte> bytes, int start, int end, List<string> strings)
    {
        for (var offset = start; offset + 8 <= end;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (size < 0 || offset + 8L + size > end)
                return;
            var tag = bytes.Slice(offset, 4);
            var dataStart = offset + 8;
            if (tag.SequenceEqual("LIST"u8) && size >= 4)
                ReadChunks(bytes, dataStart + 4, dataStart + size, strings);
            else if (tag.SequenceEqual("LGCY"u8) || tag.SequenceEqual("STRR"u8) || tag.SequenceEqual("OBCT"u8))
                ExtractStrings(bytes.Slice(dataStart, size), strings);
            offset = dataStart + size + (size & 1);
        }
    }

    private static void ExtractStrings(ReadOnlySpan<byte> data, List<string> strings)
    {
        var start = -1;
        for (var i = 0; i <= data.Length; i++)
        {
            var printable = i < data.Length && data[i] >= 0x20 && data[i] <= 0x7e;
            if (printable && start < 0)
                start = i;
            if (printable || start < 0)
                continue;
            if (i - start >= 3)
            {
                var text = Encoding.ASCII.GetString(data.Slice(start, i - start));
                // FMOD's event/sample strings are path-like.  Dropping binary
                // noise here keeps the resolver deterministic across projects.
                if (text.Any(char.IsLetterOrDigit) && text.All(c => char.IsLetterOrDigit(c) || "_./:- \\".Contains(c)))
                    strings.Add(text.Trim());
            }
            start = -1;
        }
    }
}

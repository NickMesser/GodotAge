#nullable enable
using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Godot;

namespace AAEmu.GodotViewer.Client.Targeting;

/// <summary>
/// The client's mouse cursors are RT_CURSOR resources of archeage.exe (Bin64 or bin32 next to the pak). The viewer reads
/// them from there instead of shipping them: each one is converted to a PNG in <c>&lt;user data&gt;/cursors</c> the first
/// time it is asked for. Names follow the resource: <c>cursor-&lt;id&gt;-lang-&lt;language&gt;.png</c>.
/// Everything here is optional; when the executable or a resource is not there the caller uses the default cursor.
/// </summary>
internal static partial class ClientCursors
{
    private static readonly string[] ExecutableCandidates = ["Bin64/archeage.exe", "bin32/archeage.exe", "Bin32/archeage.exe"];
    private static Dictionary<(int Id, int Lang), byte[]>? _resources;

    public static string CacheDirectory => Path.Combine(OS.GetUserDataDir(), "cursors");

    /// <summary>The cursor for a <c>cursor-&lt;id&gt;-lang-&lt;language&gt;.png</c> style file name, or null.</summary>
    public static Texture2D? Load(string fileName)
    {
        var match = CursorName().Match(Path.GetFileName(fileName));
        if (!match.Success)
            return null;
        var id = int.Parse(match.Groups[1].Value);
        var lang = int.Parse(match.Groups[2].Value);
        var cached = Path.Combine(CacheDirectory, $"cursor-{id}-lang-{lang}.png");
        if (!File.Exists(cached) && !Extract(id, lang, cached))
            return null;
        var image = Image.LoadFromFile(cached);
        return image == null || image.IsEmpty() ? null : ImageTexture.CreateFromImage(image);
    }

    private static bool Extract(int id, int lang, string target)
    {
        try
        {
            _resources ??= ReadResources();
            if (!_resources.TryGetValue((id, lang), out var data) || ToImage(data) is not { } image)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            return image.SavePng(target) == Error.Ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            GD.PrintErr($"[GodotAge] could not extract cursor {id} from the client: {e.Message}");
            return false;
        }
    }

    /// <summary>RT_CURSOR data: hotspot (2 x uint16), then a DIB (BITMAPINFOHEADER, pixels bottom-up, height doubled for the AND mask).</summary>
    private static Image? ToImage(byte[] data)
    {
        if (data.Length < 44)
            return null;
        var header = data.AsSpan(4);
        var width = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[8..]) / 2;
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (bits != 32 || compression != 0 || width <= 0 || height <= 0 || width > 256 || height > 256)
            return null;
        var pixelStart = 4 + BinaryPrimitives.ReadInt32LittleEndian(header);
        var pixelBytes = width * height * 4;
        if (data.Length < pixelStart + pixelBytes)
            return null;
        var maskStride = (width + 31) / 32 * 4;
        var maskStart = pixelStart + pixelBytes;
        var hasMask = data.Length >= maskStart + maskStride * height;

        var rgba = new byte[pixelBytes];
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var source = pixelStart + (height - 1 - y) * width * 4; // stored bottom-up
            for (var x = 0; x < width; x++)
            {
                var s = source + x * 4;
                var d = (y * width + x) * 4;
                rgba[d] = data[s + 2];
                rgba[d + 1] = data[s + 1];
                rgba[d + 2] = data[s];
                rgba[d + 3] = data[s + 3];
                anyAlpha |= data[s + 3] != 0;
            }
        }
        if (!anyAlpha && hasMask)
        {
            // no alpha channel: the AND mask (1 = transparent) decides
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var bit = data[maskStart + (height - 1 - y) * maskStride + x / 8] >> (7 - x % 8) & 1;
                    rgba[(y * width + x) * 4 + 3] = bit == 0 ? (byte)255 : (byte)0;
                }
        }
        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
    }

    private static Dictionary<(int Id, int Lang), byte[]> ReadResources()
    {
        var result = new Dictionary<(int Id, int Lang), byte[]>();
        var folder = ClientPaths.ClientFolder;
        foreach (var candidate in ExecutableCandidates)
        {
            var path = Path.Combine(folder, candidate);
            if (!File.Exists(path))
                continue;
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            ReadCursorResources(stream, result);
            if (result.Count > 0)
                break;
        }
        return result;
    }

    private const int ResourceTypeCursor = 1;

    /// <summary>Walks the PE resource tree (type, id, language) and collects every RT_CURSOR entry.</summary>
    private static void ReadCursorResources(FileStream stream, Dictionary<(int Id, int Lang), byte[]> into)
    {
        using var reader = new BinaryReader(stream);
        stream.Position = 0x3c;
        var pe = reader.ReadInt32();
        stream.Position = pe;
        if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
            return;
        stream.Position = pe + 6;
        var sectionCount = reader.ReadUInt16();
        stream.Position = pe + 20;
        var optionalSize = reader.ReadUInt16();
        stream.Position = pe + 24;
        var magic = reader.ReadUInt16();
        stream.Position = pe + 24 + (magic == 0x20b ? 112 : 96) + 2 * 8; // data directory 2 = resources
        var resourceRva = reader.ReadUInt32();
        if (resourceRva == 0)
            return;

        var sections = new List<(uint Rva, uint Size, uint Raw)>();
        for (var i = 0; i < sectionCount; i++)
        {
            stream.Position = pe + 24 + optionalSize + i * 40 + 8;
            var virtualSize = reader.ReadUInt32();
            var virtualAddress = reader.ReadUInt32();
            var rawSize = reader.ReadUInt32();
            var rawPointer = reader.ReadUInt32();
            sections.Add((virtualAddress, Math.Max(virtualSize, rawSize), rawPointer));
        }
        long ToOffset(uint rva)
        {
            foreach (var (va, size, raw) in sections)
                if (rva >= va && rva < va + size)
                    return rva - va + raw;
            throw new InvalidDataException("resource address outside every section");
        }
        var root = ToOffset(resourceRva);

        List<(uint Id, uint Offset)> Entries(long directory)
        {
            stream.Position = directory + 12;
            var named = reader.ReadUInt16();
            var ids = reader.ReadUInt16();
            var list = new List<(uint, uint)>();
            for (var i = 0; i < named + ids; i++)
            {
                stream.Position = directory + 16 + i * 8;
                var name = reader.ReadUInt32();
                var offset = reader.ReadUInt32();
                if ((name & 0x80000000) == 0) // named entries are not the numeric ids we want
                    list.Add((name, offset));
            }
            return list;
        }

        foreach (var (type, typeOffset) in Entries(root))
        {
            if (type != ResourceTypeCursor || (typeOffset & 0x80000000) == 0)
                continue;
            foreach (var (id, idOffset) in Entries(root + (typeOffset & 0x7fffffff)))
            {
                if ((idOffset & 0x80000000) == 0)
                    continue;
                foreach (var (lang, langOffset) in Entries(root + (idOffset & 0x7fffffff)))
                {
                    stream.Position = root + (langOffset & 0x7fffffff);
                    var dataRva = reader.ReadUInt32();
                    var size = reader.ReadUInt32();
                    if (size is 0 or > 1 << 20)
                        continue;
                    stream.Position = ToOffset(dataRva);
                    into[((int)id, (int)lang)] = reader.ReadBytes((int)size);
                }
            }
        }
    }

    [GeneratedRegex(@"^cursor-(\d+)-lang-(\d+)\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex CursorName();
}

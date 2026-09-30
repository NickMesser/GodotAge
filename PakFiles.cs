namespace AAEmu.GodotViewer;

/// <summary>
/// The client's files, read straight from game_pak through <see cref="FastPak"/>: the file table is cached on disk after
/// the first open (about 0.3 s to open afterwards), and reads are positional, so any number of threads can read at once.
/// </summary>
internal static class PakFiles
{
    private static readonly Lock Gate = new();
    private static FastPak _pak;

    /// <summary>Where the pak's file-table cache lives (per user, outside the repository).</summary>
    public static string IndexCacheDirectory { get; } =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "AAEmuGodotViewer", "pakindex");

    public static bool Open(string gamePakPath)
    {
        lock (Gate)
        {
            if (_pak != null)
                return true;
            try
            {
                _pak = FastPak.Open(gamePakPath, IndexCacheDirectory);
                return true;
            }
            catch (Exception e)
            {
                Godot.GD.PrintErr($"Could not open {gamePakPath}: {e.Message}");
                return false;
            }
        }
    }

    public static bool Exists(string path) => _pak != null && _pak.Exists(path);

    /// <summary>Size of a file in the pak in bytes, or -1 when it isn't there.</summary>
    public static long SizeOf(string path) => _pak?.GetSize(path) ?? -1;

    /// <summary>Streams a file of the pak into <paramref name="destination"/>; false when it isn't there.</summary>
    public static bool CopyTo(string path, Stream destination) => _pak?.CopyTo(path, destination) ?? false;

    /// <summary>Returns the file's bytes, or null when it isn't in the pak.</summary>
    public static byte[] Read(string path) => _pak?.Read(path);

    public static string ReadText(string path)
    {
        var bytes = Read(path);
        return bytes == null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Pak paths are lower case with forward slashes.</summary>
    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}

#nullable enable
using System.Globalization;
using System.Text;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// The one place that says where the ArcheAge client data lives. Every setting resolves the same way, first hit wins:
/// <list type="number">
/// <item>a command line user argument, <c>--key=value</c> or <c>key=value</c> (everything after Godot's own <c>--</c>)</item>
/// <item>an environment variable, <c>GODOTAGE_&lt;KEY&gt;</c></item>
/// <item>a <c>godotage.cfg</c> file (<c>key=value</c> lines, <c>#</c> starts a comment), looked up in the working
/// directory (Godot makes that the project folder when it is started with <c>--path</c>), next to the executable and in the
/// project folder; when several exist the first that sets a key wins, and relative paths in it are relative to the file</item>
/// <item>the built-in default</item>
/// </list>
/// Keys: <c>pak</c> (the game_pak file), <c>db</c> (client database), <c>launcher</c> (launcher .bat read for
/// <c>--autologin</c>), <c>host</c> and <c>port</c> (login server, default 127.0.0.1:1237).
/// The database defaults to <c>game/db/compact.sqlite3</c> next to the pak when that file exists, otherwise it is
/// extracted from the pak once into the user data directory (see <see cref="Database"/>).
/// </summary>
public static class ClientPaths
{
    public const string DefaultPak = "C:/AA/game_pak";
    public const string ConfigFileName = "godotage.cfg";

    /// <summary>Path of the client database inside the pak.</summary>
    public const string DatabaseInPak = "game/db/compact.sqlite3";

    private static readonly Lock Gate = new();
    private static readonly Lock DatabaseGate = new();
    private static Dictionary<string, string>? _commandLine;
    private static Dictionary<string, (string Value, string File)>? _config;
    private static string? _pak, _pakSource;
    private static string? _database, _databaseSource;
    private static string? _launcher;
    private static string? _host;
    private static int _port;
    private static bool _pakChecked;

    /// <summary>The game_pak file (see the class summary for how it is chosen).</summary>
    public static string Pak
    {
        get
        {
            lock (Gate)
            {
                if (_pak != null)
                    return _pak;
                var setting = Lookup("pak");
                var value = setting?.Value ?? DefaultPak;
                // a client install folder is accepted too
                if (Directory.Exists(value))
                    value = Path.Combine(value, "game_pak");
                _pak = value;
                _pakSource = setting?.Source ?? "default";
                Log($"pak: {_pak} ({_pakSource})");
                if (!_pakChecked && !File.Exists(_pak))
                {
                    _pakChecked = true;
                    GD.PrintErr($"[GodotAge] game_pak not found at {_pak}. Point to your ArcheAge 10.x client's game_pak with pak=<path> " +
                        $"on the command line, the GODOTAGE_PAK environment variable, or a {ConfigFileName} line 'pak=<path>'.");
                }
                return _pak;
            }
        }
    }

    /// <summary>Folder that contains the pak (the client install folder).</summary>
    public static string ClientFolder => Path.GetDirectoryName(Path.GetFullPath(Pak)) ?? ".";

    /// <summary>
    /// The client database. Explicit setting (<c>db=</c>, <c>GODOTAGE_DB</c>, cfg) if it exists, else
    /// <c>&lt;pak folder&gt;/game/db/compact.sqlite3</c> if that file exists (set env <c>GODOTAGE_NO_SIBLING_DB=1</c> to ignore it),
    /// else the copy extracted from the pak into <c>&lt;user data dir&gt;/gamedb/compact.sqlite3</c>. The first access may extract
    /// (about 230 MB, a few seconds) and opens the pak. Always returns a path; if nothing worked the file does not exist
    /// and callers' existence checks fail as they do for any missing database.
    /// </summary>
    public static string Database
    {
        get
        {
            lock (DatabaseGate)
            {
                if (_database != null)
                    return _database;
                (_database, _databaseSource) = ResolveDatabase();
                Log($"db: {_database} ({_databaseSource})");
                return _database;
            }
        }
    }

    /// <summary>Launcher .bat that <c>--autologin</c> reads the account from; default <c>&lt;pak folder&gt;/launch_aaemu.bat</c>.</summary>
    public static string Launcher
    {
        get
        {
            lock (Gate)
            {
                if (_launcher != null)
                    return _launcher;
                var setting = Lookup("launcher");
                _launcher = setting?.Value ?? Path.Combine(ClientFolder, "launch_aaemu.bat");
                Log($"launcher: {_launcher} ({setting?.Source ?? "default"}, {(File.Exists(_launcher) ? "found" : "not found")})");
                return _launcher;
            }
        }
    }

    /// <summary>Login server host, default 127.0.0.1.</summary>
    public static string LoginHost
    {
        get
        {
            lock (Gate)
            {
                if (_host != null)
                    return _host;
                var setting = Lookup("host");
                _host = setting?.Value ?? "127.0.0.1";
                if (setting != null)
                    Log($"login host: {_host} ({setting.Value.Source})");
                return _host;
            }
        }
    }

    /// <summary>Login server port, default 1237.</summary>
    public static int LoginPort
    {
        get
        {
            lock (Gate)
            {
                if (_port != 0)
                    return _port;
                var setting = Lookup("port");
                _port = int.TryParse(setting?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : 1237;
                if (setting != null)
                    Log($"login port: {_port} ({setting.Value.Source})");
                return _port;
            }
        }
    }

    /// <summary>Where the pak's database is extracted to (whether or not it has been yet).</summary>
    public static string ExtractedDatabasePath => Path.Combine(OS.GetUserDataDir(), "gamedb", "compact.sqlite3");

    // ------------------------------------------------------------------ database

    private static (string Path, string Source) ResolveDatabase()
    {
        var setting = Lookup("db");
        if (setting != null)
        {
            if (File.Exists(setting.Value.Value))
                return (setting.Value.Value, setting.Value.Source);
            GD.PrintErr($"[GodotAge] database {setting.Value.Value} ({setting.Value.Source}) does not exist; falling back.");
        }

        var noSibling = System.Environment.GetEnvironmentVariable("GODOTAGE_NO_SIBLING_DB");
        var sibling = Path.Combine(ClientFolder, "game", "db", "compact.sqlite3");
        if (string.IsNullOrEmpty(noSibling) || noSibling == "0")
        {
            if (IsSqliteFile(sibling))
                return (sibling, "compact.sqlite3 next to the pak");
        }

        var (extracted, note) = ExtractFromPak();
        return (extracted ?? ExtractedDatabasePath, note);
    }

    private static bool IsSqliteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[15];
            return stream.Read(header) == 15 && header.SequenceEqual("SQLite format 3"u8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies game/db/compact.sqlite3 out of the pak (temp file, then rename). A marker file next to the copy records the
    /// size of the pak's file and the pak's own size and stamp, so a patched pak is extracted again.
    /// </summary>
    private static (string? Path, string Note) ExtractFromPak()
    {
        var target = ExtractedDatabasePath;
        string? temp = null;
        try
        {
            if (!PakFiles.Open(Pak))
                return (File.Exists(target) ? target : null, $"pak cannot be opened, no database ({target})");
            var size = PakFiles.SizeOf(DatabaseInPak);
            if (size < 0)
                return (File.Exists(target) ? target : null, $"{DatabaseInPak} is not in the pak");

            var pakInfo = new FileInfo(Path.GetFullPath(Pak));
            var stamp = string.Create(CultureInfo.InvariantCulture, $"v1|{pakInfo.FullName.ToUpperInvariant()}|{pakInfo.Length}|{pakInfo.LastWriteTimeUtc.Ticks}|{size}");
            var marker = target + ".marker";
            // the marker is "<stamp>|<length of the finished copy>": the copy is the pak's file plus one index, so it is larger
            if (File.Exists(target) && File.Exists(marker) && File.ReadAllText(marker).Trim() is var recorded
                && recorded.StartsWith(stamp + "|", StringComparison.Ordinal)
                && long.TryParse(recorded[(stamp.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var finished)
                && new FileInfo(target).Length == finished)
                return (target, "extracted from the pak earlier");

            var directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            GD.Print($"[GodotAge] extracting {DatabaseInPak} ({size / 1048576} MB) from the pak to {target} ...");
            var started = System.Diagnostics.Stopwatch.StartNew();
            temp = Path.Combine(directory, $"compact.{Guid.NewGuid():N}.tmp");
            using (var output = new FileStream(temp, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None))
                PakFiles.CopyTo(DatabaseInPak, output);
            AddIndexes(temp);
            var finishedLength = new FileInfo(temp).Length;
            try
            {
                File.Move(temp, target, overwrite: true);
                temp = null;
            }
            catch (IOException) when (File.Exists(target))
            {
                // another instance has the old copy open: keep using it (it is only replaced when nothing holds it)
                GD.PrintErr($"[GodotAge] {target} is in use, keeping the existing copy.");
                return (target, "existing extracted copy (in use, not refreshed)");
            }
            File.WriteAllText(marker, stamp + "|" + finishedLength.ToString(CultureInfo.InvariantCulture));
            GD.Print($"[GodotAge] extracted in {started.Elapsed.TotalSeconds:0.0} s");
            return (target, $"extracted from the pak, {size / 1048576} MB");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            GD.PrintErr($"[GodotAge] could not extract {DatabaseInPak} from the pak: {e.Message}");
            return (File.Exists(target) ? target : null, "extraction failed");
        }
        finally
        {
            if (temp != null)
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The client database has almost no indexes. Every text lookup (localized_texts, 660k rows) would scan the whole table,
    /// which made the in-game UI build about 3 times slower, so the extracted copy gets the index the server database has.
    /// Only this copy is touched, never the pak or a database the user pointed us to.
    /// </summary>
    private static void AddIndexes(string file)
    {
        try
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = file, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_localized_texts_tbl_column_idx ON localized_texts (tbl_name, tbl_column_name, idx)";
            command.ExecuteNonQuery();
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            GD.PrintErr($"[GodotAge] could not add the text index to the extracted database (it works, but slower): {e.Message}");
        }
    }

    // ------------------------------------------------------------------ settings

    private static (string Value, string Source)? Lookup(string key)
    {
        var cli = CommandLine();
        if (cli.TryGetValue(key, out var fromCli) && fromCli.Length > 0)
            return (fromCli, $"command line {key}=");
        var envName = "GODOTAGE_" + key.ToUpperInvariant();
        if (System.Environment.GetEnvironmentVariable(envName) is { } fromEnv && !string.IsNullOrWhiteSpace(fromEnv))
            return (fromEnv.Trim(), $"env {envName}");
        if (Config().TryGetValue(key, out var fromFile))
            return (fromFile.Value, $"{ConfigFileName} {fromFile.File}");
        return null;
    }

    private static Dictionary<string, string> CommandLine()
    {
        lock (Gate)
        {
            if (_commandLine != null)
                return _commandLine;
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var arg in OS.GetCmdlineUserArgs())
                {
                    var kv = arg.TrimStart('-').Split('=', 2);
                    if (kv.Length == 2 && kv[0].Length > 0)
                        args[kv[0]] = kv[1].Trim().Trim('"');
                }
            }
            catch (Exception e)
            {
                GD.PrintErr($"[GodotAge] could not read the command line: {e.Message}");
            }
            return _commandLine = args;
        }
    }

    private static Dictionary<string, (string Value, string File)> Config()
    {
        lock (Gate)
        {
            if (_config != null)
                return _config;
            var settings = new Dictionary<string, (string Value, string File)>(StringComparer.OrdinalIgnoreCase);
            var folders = new List<string> { Directory.GetCurrentDirectory() };
            try { folders.Add(Path.GetDirectoryName(OS.GetExecutablePath()) ?? ""); } catch (Exception) { /* not available */ }
            try { folders.Add(ProjectSettings.GlobalizePath("res://")); } catch (Exception) { /* not available */ }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders.Where(f => f.Length > 0))
            {
                string file;
                try { file = Path.GetFullPath(Path.Combine(folder, ConfigFileName)); }
                catch (Exception) { continue; }
                if (!seen.Add(file) || !File.Exists(file))
                    continue;
                try
                {
                    var directory = Path.GetDirectoryName(file)!;
                    foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#')
                            continue;
                        var kv = line.Split('=', 2);
                        if (kv.Length != 2 || kv[0].Trim().Length == 0)
                            continue;
                        var key = kv[0].Trim();
                        var value = kv[1].Trim().Trim('"');
                        if (value.Length == 0 || settings.ContainsKey(key))
                            continue; // the first file that sets a key wins
                        if (key.Equals("pak", StringComparison.OrdinalIgnoreCase) || key.Equals("db", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("launcher", StringComparison.OrdinalIgnoreCase))
                            value = Path.GetFullPath(value, directory);
                        settings[key] = (value, $"({file})");
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    GD.PrintErr($"[GodotAge] could not read {file}: {e.Message}");
                }
            }
            return _config = settings;
        }
    }

    private static void Log(string message) => GD.Print("[GodotAge] " + message);
}

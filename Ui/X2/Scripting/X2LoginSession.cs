#nullable enable
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting;

/// <summary>
/// Runs the original login stages end to end, engine independent: per stage a fresh <see cref="UiRoot"/> and Lua state
/// load the client's common packages plus the stage packages (connect, then character_select), exactly as the client
/// rebuilds its script environment on every stage transition. Call <see cref="Update"/> once per frame on the UI thread.
/// </summary>
public sealed class X2LoginSession : IDisposable
{
    private readonly IUiFileSource _files;
    private readonly ITextMeasurer _measurer;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Action> _queue = new();
    private float _width = 1920, _height = 1080;

    public X2LoginSession(IUiFileSource files, IUiTextSource texts, ITextMeasurer measurer, ILoginBackend backend,
        X2LoginOptions options, Action<string> log)
    {
        _files = files;
        _measurer = measurer;
        _log = log;
        Options = options;
        Settings = new UiSettings(files, options.Locale);
        Engine = new X2Engine(backend, options, texts, Post, log);
        Engine.StageChanged += _ => Post(BuildStage);
        Engine.EnteredWorld += c => EnteredWorld?.Invoke(c);
    }

    public X2LoginOptions Options { get; }
    public UiSettings Settings { get; }
    public X2Engine Engine { get; }
    public UiRoot? Root { get; private set; }
    public X2LuaHost? Host { get; private set; }
    public float UiScale { get; set; } = 1;
    /// <summary>Content registry and clock of the in-game UI.</summary>
    public World.X2WorldCore WorldCore { get; } = new();
    /// <summary>Installs the live X2 API families on the in-game (stage 6) Lua state, before its scripts load.</summary>
    public Action<X2LuaHost>? InstallWorldApis { get; set; }

    public event Action<CharacterInfo>? EnteredWorld;
    /// <summary>Raised after a stage's scripts are loaded (argument: stage id).</summary>
    public event Action<int>? StageBuilt;

    /// <summary>Queues work for the UI thread (backend completions arrive this way).</summary>
    public void Post(Action action) => _queue.Enqueue(action);

    public void Start() => BuildStage();

    /// <summary>Starts with the in-game UI (the character is already in the world).</summary>
    public void StartInWorld()
    {
        Engine.BeginInWorld();
        BuildStage();
    }

    public void SetScreenSize(float width, float height)
    {
        _width = width;
        _height = height;
        Root?.SetScreenSize(width, height);
    }

    public void Update(double deltaMilliseconds)
    {
        var budget = 64;
        while (budget-- > 0 && _queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { _log("[x2] " + e); }
        }
        Root?.Update(deltaMilliseconds);
    }

    private void BuildStage()
    {
        var stage = Engine.Stage;
        if (stage == X2Engine.StageWorld && !Options.HudInWorld) return;
        Host?.Dispose();
        var started = DateTime.UtcNow;
        var root = new UiRoot(Settings, _measurer, _width, _height) { UiScale = UiScale, Translator = Options.Translator };
        var host = new X2LuaHost(root, _files, _log);
        Engine.BeginStageBuild();
        try { BuildStageContents(stage, root, host, started); }
        finally { Engine.EndStageBuild(); }
    }

    private void BuildStageContents(int stage, UiRoot root, X2LuaHost host, DateTime started)
    {
        Engine.Install(host);
        Root = root;
        Host = host;

        host.LoadPackage("commonui", "baselib");
        host.EnsureLegacyFontColorHex();
        host.LoadPackage("commonui", "logic");
        foreach (var package in new[] { "logic", "baselib", "components", "option" })
            host.LoadPackage("x2ui", package);
        if (stage == X2Engine.StageWorld)
        {
            InstallWorldApis?.Invoke(host);
            Engine.InstallInput(host);
            // after the API families: the content registry and clock stay authoritative
            WorldCore.Install(host);
            // stage 6 (StartScreenWorld) scans the whole x2ui root after the common packages
            foreach (var package in WorldPackages())
                host.LoadPackage("x2ui", package);
        }
        else if (stage == X2Engine.StageSelect)
        {
            host.LoadPackage("x2ui", "loginstage_new/character_select");
            host.LoadPackage("x2ui", "secondpassword");
        }
        else if (stage is X2Engine.StageCreate or X2Engine.StageAbility or X2Engine.StageCustomize)
        {
            host.LoadPackage("x2ui", "loginstage_new/character_create");
        }
        else
        {
            host.LoadPackage("x2ui", "loginstage_new/connect");
        }
        _log($"[x2] stage {stage}: {root.AllWidgets().Count()} widgets, {host.Errors.Count} script error(s), {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
        Engine.StartStage(host);
        StageBuilt?.Invoke(stage);
    }

    private static readonly HashSet<string> CommonPackages = new(StringComparer.OrdinalIgnoreCase) { "logic", "baselib", "components", "option" };
    private static readonly HashSet<string> NotInWorld = new(StringComparer.OrdinalIgnoreCase) { "loginstage_new", "customizing_new", "secondpassword" };

    /// <summary>Every x2ui package with a toc.g except the common ones and the login stages, in name order.</summary>
    private IEnumerable<string> WorldPackages()
    {
        var names = Options.WorldPackages ?? KnownWorldPackages;
        return names.Where(n => !CommonPackages.Contains(n) && !NotInWorld.Contains(n)).OrderBy(n => n, StringComparer.Ordinal);
    }

    /// <summary>The x2ui package folders of the 10.0.2.13 pak (game/scripts/x2ui/*/toc.g).</summary>
    public static readonly string[] KnownWorldPackages =
    [
        // dialoghandler has only source scripts in this client extraction. LoadPackage falls back to
        // scripts/x2ui/dialoghandler/toc.g and its .lua files when compiled files are absent.
        "abilitychange", "assignment", "auction", "battlefield", "beautyshop_new", "bless_uthstin", "book", "butler",
        "centermessage", "changeitemlook", "characterinfo", "chat", "checkbot", "clientdrivenindun", "combat_resource",
        "combattext", "commonfarm", "community", "crafting", "dev", "dyeing", "esc_menu",
        "dialoghandler",
        "eventcenter", "hero", "housing", "hud", "indunchannelselect", "ingameshop", "interaction", "inventory", "item_guide",
        "loot", "mailbox", "map", "messageboximpl", "music", "nft", "portal", "premiumservice",
        "questcontext", "raid_recruit", "raidframe", "raidteammanager", "ranking", "repair", "resident", "roster",
        "sensitiveoperation", "siege_raid", "skill", "slave", "specialty", "store", "trade",
        "tutorial", "ucc", "unitframe", "usertrial", "webbrowser",
    ];

    public void Dispose()
    {
        Engine.CancelPending();
        Host?.Dispose();
        Host = null;
        Root = null;
    }
}

/// <summary>ui_texts from the client database (compact.sqlite3), in one locale.</summary>
public sealed class SqliteUiTextSource : IUiTextSource
{
    private readonly Dictionary<(int, string), string> _texts = [];
    private readonly string? _connection;
    private readonly string _column = "en_us";

    public SqliteUiTextSource(string databasePath, string locale = "en_us")
    {
        _connection = $"Data Source={databasePath};Mode=ReadOnly";
        if (!File.Exists(databasePath)) { _connection = null; return; }
        var column = locale.All(c => char.IsLetterOrDigit(c) || c == '_') ? locale : "en_us";
        _column = column;
        using var db = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT u.category_id, u.key, COALESCE(NULLIF(l.{column}, ''), u.text)
            FROM ui_texts u LEFT JOIN localized_texts l
              ON l.tbl_name = 'ui_texts' AND l.tbl_column_name = 'text' AND l.idx = u.id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            _texts[(reader.GetInt32(0), reader.GetString(1))] = reader.IsDBNull(2) ? "" : reader.GetString(2);
    }

    public int Count => _texts.Count;
    public string? Get(int category, string key) => _texts.GetValueOrDefault((category, key));

    /// <summary>Localised zones.display_text for a zone key (the lobby record's zone), or null.</summary>
    public string? ZoneName(uint zoneKey) => Lookup($"""
        SELECT COALESCE(NULLIF(l.{_column}, ''), z.display_text) FROM zones z LEFT JOIN localized_texts l
          ON l.tbl_name = 'zones' AND l.tbl_column_name = 'display_text' AND l.idx = z.id
        WHERE z.zone_key = {zoneKey} LIMIT 1
        """);

    /// <summary>Localised system_factions.name, or null.</summary>
    public string? FactionName(uint factionId) => Lookup($"""
        SELECT COALESCE(NULLIF(l.{_column}, ''), f.name) FROM system_factions f LEFT JOIN localized_texts l
          ON l.tbl_name = 'system_factions' AND l.tbl_column_name = 'name' AND l.idx = f.id
        WHERE f.id = {factionId} LIMIT 1
        """);

    private string? Lookup(string sql)
    {
        if (_connection == null) return null;
        try
        {
            using var db = new SqliteConnection(_connection);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar() as string is { Length: > 0 } s ? s : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}

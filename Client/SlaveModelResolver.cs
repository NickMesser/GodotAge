#nullable enable

using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Client;

/// <summary>One of the explicit visual-state columns in <c>vehicle_models</c> and <c>ship_models</c>.</summary>
public enum SlaveModelState
{
    Normal,
    Damaged25,
    Damaged50,
    Damaged75,
    Dying,
    Dead,
}

/// <summary>How a slave visual should be loaded by the caller.</summary>
public enum SlaveModelKind
{
    None,
    Prefab,
    Cgf,
    /// <summary>A valid client asset path, but unsupported by the current static-model renderer.</summary>
    Cga,
    Unsupported,
}

/// <summary>
/// The database-selected visual for a slave template. <see cref="PakPath"/> is normalized for <see cref="PakFiles"/>.
/// For <see cref="SlaveModelKind.Prefab"/>, <see cref="PrefabMember"/> identifies the member in the XML library.
/// </summary>
public sealed record SlaveModelResolution(
    long SlaveId,
    string SlaveName,
    long ModelId,
    string ModelSubType,
    long ModelSubId,
    SlaveModelState State,
    SlaveModelKind Kind,
    string? PakPath,
    string? PrefabMember,
    string? SourceUri,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>True when the selected row supplied a model that the existing static renderer can consume.</summary>
    public bool IsRenderable => (Kind is SlaveModelKind.Prefab or SlaveModelKind.Cgf) && PakPath is { Length: > 0 };
}

/// <summary>
/// Resolves a slave template's selected visual from <c>compact.sqlite3</c> without mutating the content database.
/// The verified content relationship is <c>slaves.model_id -&gt; models.(sub_type,sub_id) -&gt;
/// vehicle_models</c> or <c>ship_models</c>. No equipment, doodad, or attachment mapping is inferred here.
/// </summary>
public sealed class SlaveModelResolver
{
    private readonly string _connectionString;

    /// <param name="databasePath">The decrypted game content database. Connections are opened read-only.</param>
    public SlaveModelResolver(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    /// <summary>
    /// Returns the requested state visual. An empty state field is reported as a diagnostic; this method deliberately
    /// does not substitute the normal visual, because that would hide content-state differences.
    /// </summary>
    public SlaveModelResolution Resolve(long slaveId, SlaveModelState state = SlaveModelState.Normal)
    {
        var column = StateColumn(state);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        // column is selected exclusively by StateColumn, never from caller input.
        command.CommandText = $"""
            SELECT s.id, s.name, s.model_id, m.sub_type, m.sub_id,
                   CASE m.sub_type
                       WHEN 'VehicleModel' THEN vm.{column}
                       WHEN 'ShipModel' THEN sm.{column}
                   END AS model_uri
              FROM slaves AS s
              JOIN models AS m ON m.id = s.model_id
              LEFT JOIN vehicle_models AS vm
                ON m.sub_type = 'VehicleModel' AND vm.id = m.sub_id
              LEFT JOIN ship_models AS sm
                ON m.sub_type = 'ShipModel' AND sm.id = m.sub_id
             WHERE s.id = $slaveId;
            """;
        command.Parameters.AddWithValue("$slaveId", slaveId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Missing(slaveId, state, $"Slave template {slaveId} was not found.");

        var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
        var modelId = reader.GetInt64(2);
        var subType = reader.IsDBNull(3) ? "" : reader.GetString(3);
        var subId = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
        var uri = reader.IsDBNull(5) ? null : reader.GetString(5);
        if (subType is not ("VehicleModel" or "ShipModel"))
            return Unsupported(slaveId, name, modelId, subType, subId, state, uri ?? "",
                $"Slave {slaveId} resolves to unsupported model type '{subType}'.");
        return Parse(slaveId, name, modelId, subType, subId, state, uri);
    }

    /// <summary>Returns the localization key used by x2ui's SLAVE_KIND string table.</summary>
    public string ResolveKindLocalizationKey(long slaveId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT slave_kind_id FROM slaves WHERE id = $slaveId;";
        command.Parameters.AddWithValue("$slaveId", slaveId);
        var value = command.ExecuteScalar();
        if (value is null or DBNull) return "";
        return Convert.ToInt32(value) switch
        {
            1 => "big_sailing_ship",
            2 => "small_sailing_ship",
            3 => "speedboat",
            4 => "boat",
            5 => "tank",
            6 => "machine",
            7 => "siege_weapon",
            8 => "slave_equipment",
            9 => "fishboat",
            10 => "merchant_ship",
            11 => "leviathan",
            _ => "",
        };
    }

    /// <summary>
    /// Reads and flattens a resolved prefab using the supplied pak reader. The delegate is also used for nested prefab
    /// references. Returns <c>null</c> with a diagnostic when the resolution is not a readable prefab.
    /// </summary>
    public static PrefabResolution? ResolvePrefab(
        SlaveModelResolution resolution,
        Func<string, byte[]?> readPak,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(readPak);
        if (resolution.Kind != SlaveModelKind.Prefab || resolution.PakPath is null ||
            string.IsNullOrWhiteSpace(resolution.PrefabMember))
        {
            diagnostic = "The slave resolution does not name a complete prefab library and member.";
            return null;
        }

        var bytes = readPak(resolution.PakPath);
        if (bytes is null)
        {
            diagnostic = $"Prefab library '{resolution.PakPath}' is absent from the pak.";
            return null;
        }

        diagnostic = null;
        return PrefabReader.ResolveWithReferences(bytes, resolution.PrefabMember, readPak, resolution.PakPath);
    }

    private static SlaveModelResolution Missing(long slaveId, SlaveModelState state, string diagnostic) =>
        new(slaveId, "", 0, "", 0, state, SlaveModelKind.None, null, null, null, [diagnostic]);

    private static SlaveModelResolution Parse(
        long slaveId, string name, long modelId, string subType, long subId, SlaveModelState state, string? uri)
    {
        var diagnostics = new List<string>();
        var source = uri?.Trim();
        if (string.IsNullOrEmpty(source))
        {
            diagnostics.Add($"{state} visual is empty for slave {slaveId} ({subType} {subId}).");
            return new(slaveId, name, modelId, subType, subId, state, SlaveModelKind.None, null, null, source, diagnostics);
        }

        var separator = source.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            diagnostics.Add($"Unsupported slave visual URI '{source}'.");
            return new(slaveId, name, modelId, subType, subId, state, SlaveModelKind.Unsupported, null, null, source, diagnostics);
        }

        var scheme = source[..separator].Trim().ToLowerInvariant();
        var target = source[(separator + 3)..].Trim().Replace('\\', '/');
        return scheme switch
        {
            "prefab" or "prefabs" => ParsePrefab(slaveId, name, modelId, subType, subId, state, source, target, diagnostics),
            "cgf" => ParseStatic(slaveId, name, modelId, subType, subId, state, source, target, SlaveModelKind.Cgf, diagnostics),
            "cga" or "cga_loop" => ParseStatic(slaveId, name, modelId, subType, subId, state, source, target, SlaveModelKind.Cga, diagnostics),
            _ => Unsupported(slaveId, name, modelId, subType, subId, state, source, $"Unsupported slave model URI scheme '{scheme}'."),
        };
    }

    private static SlaveModelResolution ParsePrefab(
        long slaveId, string name, long modelId, string subType, long subId, SlaveModelState state,
        string source, string target, List<string> diagnostics)
    {
        var xmlEnd = target.IndexOf(".xml/", StringComparison.OrdinalIgnoreCase);
        if (xmlEnd < 0)
        {
            diagnostics.Add($"Prefab URI '{source}' has no XML member separator.");
            return new(slaveId, name, modelId, subType, subId, state, SlaveModelKind.Unsupported, null, null, source, diagnostics);
        }

        var library = target[..(xmlEnd + 4)];
        var member = target[(xmlEnd + 5)..].Trim();
        if (member.Length == 0)
        {
            diagnostics.Add($"Prefab URI '{source}' has no prefab member.");
            return new(slaveId, name, modelId, subType, subId, state, SlaveModelKind.Unsupported, null, null, source, diagnostics);
        }

        return new(slaveId, name, modelId, subType, subId, state, SlaveModelKind.Prefab,
            CdfReader.PakPath(library), member, source, diagnostics);
    }

    private static SlaveModelResolution ParseStatic(
        long slaveId, string name, long modelId, string subType, long subId, SlaveModelState state,
        string source, string target, SlaveModelKind kind, List<string> diagnostics)
    {
        var path = CdfReader.PakPath(target);
        if (path.Length == 0)
        {
            diagnostics.Add($"Model URI '{source}' has no asset path.");
            return new(slaveId, name, modelId, subType, subId, state, SlaveModelKind.Unsupported, null, null, source, diagnostics);
        }
        if (kind == SlaveModelKind.Cga)
            diagnostics.Add($"CGA slave model '{path}' is not supported by the current static-model renderer.");
        return new(slaveId, name, modelId, subType, subId, state, kind, path, null, source, diagnostics);
    }

    private static SlaveModelResolution Unsupported(
        long slaveId, string name, long modelId, string subType, long subId, SlaveModelState state,
        string source, string diagnostic) =>
        new(slaveId, name, modelId, subType, subId, state, SlaveModelKind.Unsupported, null, null, source, [diagnostic]);

    private static string StateColumn(SlaveModelState state) => state switch
    {
        SlaveModelState.Normal => "normal",
        SlaveModelState.Damaged25 => "damaged25",
        SlaveModelState.Damaged50 => "damaged50",
        SlaveModelState.Damaged75 => "damaged75",
        SlaveModelState.Dying => "dying",
        SlaveModelState.Dead => "dead",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported slave model state."),
    };
}

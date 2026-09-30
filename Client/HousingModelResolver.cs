#nullable enable
using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Client;

/// <summary>
/// Resolves the server-selected <c>models.id</c> of a housing unit into the static CryEngine parts that draw it.
/// All returned paths are lower-case, pak-relative paths and all transforms remain in CryEngine axes.
/// </summary>
public sealed class HousingModelResolver
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<uint, IReadOnlyList<PrefabPart>> _cache = new();

    /// <param name="databasePath">The decrypted game-content database, opened read-only.</param>
    public HousingModelResolver(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    /// <summary>
    /// Resolves a model id from <c>SCUnitState</c> or <c>SCHouseBuildProgress</c>.
    /// A <c>PrefabModel</c> may name several normal-state <c>prefab_elements</c>. Only state 1 is
    /// rendered: higher states are alternate damage/death appearances, not additive house parts. A model
    /// without state-1 rows, unsupported model kinds, and missing assets return an empty list.
    /// </summary>
    public IReadOnlyList<PrefabPart> Resolve(uint modelId) =>
        modelId == 0 ? Array.Empty<PrefabPart>() : _cache.GetOrAdd(modelId, Load);

    private IReadOnlyList<PrefabPart> Load(uint modelId)
    {
        using var database = new SqliteConnection(_connectionString);
        database.Open();
        using var model = database.CreateCommand();
        model.CommandText = """
            SELECT m.sub_type, m.sub_id, actor.model_file
              FROM models AS m
              LEFT JOIN actor_models AS actor
                ON actor.id = m.sub_id AND m.sub_type = 'ActorModel'
             WHERE m.id = $modelId;
            """;
        model.Parameters.AddWithValue("$modelId", (long)modelId);
        using var reader = model.ExecuteReader();
        if (!reader.Read())
            return Array.Empty<PrefabPart>();

        var subtype = reader.IsDBNull(0) ? "" : reader.GetString(0);
        var subId = reader.IsDBNull(1) ? 0u : checked((uint)reader.GetInt64(1));
        var actorFile = reader.IsDBNull(2) ? null : reader.GetString(2);

        if (subtype.Equals("PrefabModel", StringComparison.OrdinalIgnoreCase))
            return ResolvePrefabModel(database, subId);

        // Housing is normally a PrefabModel. Still accept a direct static URI from actor_models so a
        // content variant does not become an exception at the call site; CDF/CHR actors are deliberately
        // omitted because this resolver promises static render parts only.
        return ResolveUri(actorFile);
    }

    private static IReadOnlyList<PrefabPart> ResolvePrefabModel(SqliteConnection database, uint prefabModelId)
    {
        if (prefabModelId == 0)
            return Array.Empty<PrefabPart>();

        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT file_path
              FROM prefab_elements
             WHERE prefab_model_id = $prefabModelId
               AND state_id = 1
             ORDER BY state_id;
            """;
        command.Parameters.AddWithValue("$prefabModelId", (long)prefabModelId);
        using var reader = command.ExecuteReader();
        var result = new List<PrefabPart>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                continue;
            var uri = reader.GetString(0);
            if (seenUris.Add(uri))
                result.AddRange(ResolveUri(uri));
        }
        return result.Count == 0 ? Array.Empty<PrefabPart>() : result;
    }

    private static IReadOnlyList<PrefabPart> ResolveUri(string? sourceUri)
    {
        if (string.IsNullOrWhiteSpace(sourceUri))
            return Array.Empty<PrefabPart>();

        var source = sourceUri.Trim().Replace('\\', '/');
        var separator = source.IndexOf("://", StringComparison.Ordinal);
        var scheme = separator > 0 ? source[..separator].Trim() : "";
        var target = separator > 0 ? source[(separator + 3)..].Trim() : source;
        var xmlEnd = target.IndexOf(".xml/", StringComparison.OrdinalIgnoreCase);

        if (scheme.Equals("prefab", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("prefabs", StringComparison.OrdinalIgnoreCase) || xmlEnd >= 0 ||
            target.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            var member = xmlEnd >= 0 ? target[(xmlEnd + 5)..] : "";
            var libraryPath = ToPakPath(xmlEnd >= 0 ? target[..(xmlEnd + 4)] : target);
            var bytes = PakFiles.Read(libraryPath);
            if (bytes is null)
                return Array.Empty<PrefabPart>();
            var prefab = PrefabReader.ResolveWithReferences(bytes, member, PakFiles.Read, libraryPath);
            return prefab.Parts;
        }

        var path = ToPakPath(target);
        if (!path.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".cga", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<PrefabPart>();
        return [new PrefabPart(path, null, Matrix4x4.Identity, scheme, null)];
    }

    private static string ToPakPath(string path)
    {
        path = path.TrimStart('/');
        if (!path.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
            path = "game/" + path;
        return path.ToLowerInvariant();
    }
}

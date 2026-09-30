#nullable enable
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer;

public enum DoodadInteractionKind
{
    None,
    Use,
    Gather,
    Mine,
    Log,
    Fish,
    Read,
    Loot,
}

public enum DoodadUiInteractionKind
{
    None,
    Mailbox,
    Crafting,
    CraftOrderBoard,
}

/// <summary>The client-visible action authored on a doodad's current function group.</summary>
public sealed record DoodadInteractionResolution(DoodadUiInteractionKind UiKind, uint SkillId);

/// <summary>Classifies the asset addressed by a doodad model URI.</summary>
public enum DoodadModelKind
{
    None,
    Cgf,
    Cga,
    Chr,
    Cdf,
    Prefab,
    Vegetation,
    Entity,
    NpcType,
    Unknown,
}

/// <summary>
/// The visual selected for a doodad template and one of its server phase IDs.
/// <paramref name="ModelPath"/> is a lower-case, pak-relative asset path when the URI names a file.
/// For prefab URIs, <paramref name="Member"/> identifies the named object inside that XML file.
/// </summary>
public sealed record DoodadModelResolution(
    bool TemplateExists,
    bool PhaseExists,
    string? ModelPath,
    DoodadModelKind Kind,
    float Scale,
    bool HasPhaseStartScale,
    float? PhaseEndScale,
    string? Member,
    string? SourceUri);

/// <summary>
/// Resolves visual assets directly from the read-only game content database.
/// The server's current <c>FuncGroupId</c> is the <c>phaseId</c> argument.
/// </summary>
public sealed class DoodadModelResolver
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<(uint TemplateId, uint PhaseId), DoodadInteractionKind> _interactionCache = new();
    private readonly ConcurrentDictionary<(uint TemplateId, uint PhaseId), DoodadInteractionResolution> _interactionResolutionCache = new();
    private readonly ConcurrentDictionary<(uint TemplateId, uint PhaseId), bool> _parentInfoCache = new();

    /// <param name="databasePath">Path to <c>compact.sqlite3</c>; it is opened read-only.</param>
    public DoodadModelResolver(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    /// <summary>
    /// Returns the non-empty phase model when <paramref name="phaseId"/> belongs to the template; otherwise
    /// returns <c>doodad_almighties.model</c>. This follows the server's <c>Doodad.GetZoneModelId</c> rule.
    /// <see cref="DoodadModelResolution.Scale"/> is the phase's unambiguous growth start scale when one is
    /// declared, otherwise 1. The packet's instance scale is authoritative, especially while a growth task is
    /// interpolating toward <see cref="DoodadModelResolution.PhaseEndScale"/>.
    /// </summary>
    public DoodadModelResolution Resolve(uint templateId, uint phaseId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.model AS template_model,
                   g.id AS phase_id,
                   g.model AS phase_model,
                   growth.start_scale,
                   growth.end_scale
              FROM doodad_almighties AS t
              LEFT JOIN doodad_func_groups AS g
                ON g.id = $phaseId AND g.doodad_almighty_id = t.id
              LEFT JOIN (
                   SELECT p.doodad_func_group_id,
                          CASE WHEN COUNT(*) = 1 THEN MAX(growth_func.start_scale) END AS start_scale,
                          CASE WHEN COUNT(*) = 1 THEN MAX(growth_func.end_scale) END AS end_scale
                     FROM doodad_phase_funcs AS p
                     JOIN doodad_func_growths AS growth_func
                       ON growth_func.id = p.actual_func_id
                    WHERE p.actual_func_type = 'DoodadFuncGrowth'
                 GROUP BY p.doodad_func_group_id
              ) AS growth ON growth.doodad_func_group_id = g.id
             WHERE t.id = $templateId;
            """;
        command.Parameters.AddWithValue("$templateId", (long)templateId);
        command.Parameters.AddWithValue("$phaseId", (long)phaseId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new(false, false, null, DoodadModelKind.None, 1f, false, null, null, null);

        var phaseExists = !reader.IsDBNull(1);
        var phaseUri = phaseExists && !reader.IsDBNull(2) ? reader.GetString(2) : null;
        var uri = string.IsNullOrWhiteSpace(phaseUri) ? reader.GetString(0) : phaseUri;
        var hasPhaseStartScale = !reader.IsDBNull(3);
        var scale = hasPhaseStartScale ? reader.GetFloat(3) / 1000f : 1f;
        float? endScale = reader.IsDBNull(4) ? null : reader.GetFloat(4) / 1000f;
        return ParseUri(uri, phaseExists, scale, hasPhaseStartScale, endScale);
    }

    /// <summary>Returns a conservative hover hint from direct server data attached to this doodad phase.</summary>
    public DoodadInteractionKind GetInteractionKind(uint templateId, uint phaseId) =>
        phaseId == 0 ? DoodadInteractionKind.None : _interactionCache.GetOrAdd((templateId, phaseId), LoadInteractionKind);

    /// <summary>Resolves client UI navigation and its authored skill from the live function group.</summary>
    public DoodadInteractionResolution ResolveInteraction(uint templateId, uint phaseId) =>
        phaseId == 0
            ? new(DoodadUiInteractionKind.None, 0)
            : _interactionResolutionCache.GetOrAdd((templateId, phaseId), LoadInteractionResolution);

    /// <summary>
    /// Whether the doodad's live function group has a DoodadFuncParentInfo (a house's "building management
    /// nameplate", func skill 15212): using it opens the parent house's window.
    /// </summary>
    public bool HasParentInfoFunc(uint templateId, uint phaseId) =>
        phaseId != 0 && _parentInfoCache.GetOrAdd((templateId, phaseId), key =>
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM doodad_funcs AS f
                      JOIN doodad_func_groups AS g ON g.id = f.doodad_func_group_id
                     WHERE g.doodad_almighty_id = $templateId AND g.id = $phaseId
                       AND f.actual_func_type = 'DoodadFuncParentInfo');
                """;
            command.Parameters.AddWithValue("$templateId", (long)key.TemplateId);
            command.Parameters.AddWithValue("$phaseId", (long)key.PhaseId);
            return Convert.ToInt64(command.ExecuteScalar()) != 0;
        });

    private DoodadInteractionResolution LoadInteractionResolution((uint TemplateId, uint PhaseId) key)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.actual_func_type, COALESCE(f.func_skill_id, 0)
              FROM doodad_funcs AS f
              JOIN doodad_func_groups AS g ON g.id = f.doodad_func_group_id
             WHERE g.doodad_almighty_id = $templateId AND g.id = $phaseId
               AND f.actual_func_type IN (
                   'DoodadFuncNaviOpenMailbox',
                   'DoodadFuncCraftStart',
                   'DoodadFuncCraftPack',
                   'DoodadFuncCraftOrderBoardUiOpen',
                   'DoodadFuncAttachment')
             ORDER BY CASE f.actual_func_type
                 WHEN 'DoodadFuncNaviOpenMailbox' THEN 0
                 WHEN 'DoodadFuncCraftOrderBoardUiOpen' THEN 1
                 WHEN 'DoodadFuncCraftStart' THEN 2
                 WHEN 'DoodadFuncCraftPack' THEN 3
                 ELSE 4 END,
                 f.id
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$templateId", (long)key.TemplateId);
        command.Parameters.AddWithValue("$phaseId", (long)key.PhaseId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new(DoodadUiInteractionKind.None, 0);
        var kind = reader.GetString(0) switch
        {
            "DoodadFuncNaviOpenMailbox" => DoodadUiInteractionKind.Mailbox,
            "DoodadFuncCraftOrderBoardUiOpen" => DoodadUiInteractionKind.CraftOrderBoard,
            "DoodadFuncCraftStart" => DoodadUiInteractionKind.Crafting,
            "DoodadFuncCraftPack" => DoodadUiInteractionKind.Crafting,
            _ => DoodadUiInteractionKind.None,
        };
        return new(kind, checked((uint)reader.GetInt64(1)));
    }

    private DoodadInteractionKind LoadInteractionKind((uint TemplateId, uint PhaseId) key)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT f.actual_func_type, f.is_primary
              FROM (
                   SELECT doodad_func_group_id, actual_func_type, 1 AS is_primary FROM doodad_funcs
                   UNION ALL
                   SELECT doodad_func_group_id, actual_func_type, 0 AS is_primary FROM doodad_phase_funcs
              ) AS f
              JOIN doodad_func_groups AS g ON g.id = f.doodad_func_group_id
             WHERE g.doodad_almighty_id = $templateId AND g.id = $phaseId;
            """;
        command.Parameters.AddWithValue("$templateId", (long)key.TemplateId);
        command.Parameters.AddWithValue("$phaseId", (long)key.PhaseId);
        using var reader = command.ExecuteReader();
        var interaction = DoodadInteractionKind.None;
        while (reader.Read())
        {
            var type = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var isPrimary = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
            var candidate = ClassifyInteraction(type, isPrimary);
            if (candidate == DoodadInteractionKind.Loot)
                return DoodadInteractionKind.Loot;
            if (InteractionPriority(candidate) > InteractionPriority(interaction))
                interaction = candidate;
        }
        return interaction;
    }

    private static DoodadInteractionKind ClassifyInteraction(string type, bool isPrimary)
    {
        if (type.Contains("Loot", StringComparison.OrdinalIgnoreCase)) return DoodadInteractionKind.Loot;
        if (type.Contains("OpenPaper", StringComparison.OrdinalIgnoreCase)) return DoodadInteractionKind.Read;
        if (type.Contains("DigTerrain", StringComparison.OrdinalIgnoreCase)) return DoodadInteractionKind.Mine;
        if (type.Contains("Cutdown", StringComparison.OrdinalIgnoreCase)) return DoodadInteractionKind.Log;
        if (type.Contains("Fish", StringComparison.OrdinalIgnoreCase)) return DoodadInteractionKind.Fish;
        if (type.Contains("Growth", StringComparison.OrdinalIgnoreCase)) return DoodadInteractionKind.Gather;
        return isPrimary && !string.IsNullOrEmpty(type) ? DoodadInteractionKind.Use : DoodadInteractionKind.None;
    }

    private static int InteractionPriority(DoodadInteractionKind kind) => kind switch
    {
        DoodadInteractionKind.Loot => 7,
        DoodadInteractionKind.Read => 6,
        DoodadInteractionKind.Mine => 5,
        DoodadInteractionKind.Log => 4,
        DoodadInteractionKind.Fish => 3,
        DoodadInteractionKind.Gather => 2,
        DoodadInteractionKind.Use => 1,
        _ => 0,
    };

    private static DoodadModelResolution ParseUri(
        string? uri, bool phaseExists, float scale, bool hasPhaseStartScale, float? phaseEndScale)
    {
        var sourceUri = uri?.Trim();
        if (string.IsNullOrEmpty(sourceUri) ||
            sourceUri.Equals("a://invalid", StringComparison.OrdinalIgnoreCase))
            return new(true, phaseExists, null, DoodadModelKind.None, scale, hasPhaseStartScale, phaseEndScale, null, sourceUri);

        var separator = sourceUri.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
            return new(true, phaseExists, null, DoodadModelKind.Unknown, scale, hasPhaseStartScale, phaseEndScale, null, sourceUri);

        var scheme = sourceUri[..separator].Trim().ToLowerInvariant();
        var target = sourceUri[(separator + 3)..].Trim().Replace('\\', '/');
        var schemeKind = scheme switch
        {
            "cgf" => DoodadModelKind.Cgf,
            "cga" or "cga_loop" => DoodadModelKind.Cga,
            "chr" => DoodadModelKind.Chr,
            "cdf" => DoodadModelKind.Cdf,
            "prefab" or "prefabs" => DoodadModelKind.Prefab,
            "vegetation" => DoodadModelKind.Vegetation,
            "entity" => DoodadModelKind.Entity,
            "npctype" => DoodadModelKind.NpcType,
            _ => DoodadModelKind.Unknown,
        };

        if (schemeKind is DoodadModelKind.Unknown or DoodadModelKind.Entity or DoodadModelKind.NpcType)
            return new(true, phaseExists, null, schemeKind, scale, hasPhaseStartScale, phaseEndScale, target, sourceUri);

        string? member = null;
        if (schemeKind == DoodadModelKind.Prefab)
        {
            var xmlEnd = target.IndexOf(".xml/", StringComparison.OrdinalIgnoreCase);
            if (xmlEnd >= 0)
            {
                member = target[(xmlEnd + 5)..];
                target = target[..(xmlEnd + 4)];
            }
        }

        var modelPath = ToPakPath(target);
        var kind = ClassifyFileKind(modelPath) ?? schemeKind;
        return new(true, phaseExists, modelPath, kind, scale, hasPhaseStartScale, phaseEndScale, member, sourceUri);
    }

    private static DoodadModelKind? ClassifyFileKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cgf" => DoodadModelKind.Cgf,
        ".cga" => DoodadModelKind.Cga,
        ".chr" => DoodadModelKind.Chr,
        ".cdf" => DoodadModelKind.Cdf,
        _ => null,
    };

    private static string ToPakPath(string path)
    {
        path = path.TrimStart('/');
        if (!path.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
            path = "game/" + path;
        return path.ToLowerInvariant();
    }
}

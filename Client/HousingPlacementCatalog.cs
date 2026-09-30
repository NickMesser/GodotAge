#nullable enable

using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Client;

/// <summary>A housing design (housings row) as the builder and the house UI need it.</summary>
/// <param name="ItemTemplateId">The design item that placed it, or 0 when resolved from a placed house.</param>
/// <param name="GardenRadius">housing_sizes.garden_radius: half the side of the square plot, in metres.</param>
/// <param name="Completion">item_housings.completion: a complete kit places a finished house.</param>
public sealed record HousingDesignData(uint ItemTemplateId, uint DesignId, uint CategoryId, uint MainModelId,
    float GardenRadius = 0, bool Completion = false, uint TaxationId = 0, bool HeavyTax = false,
    bool AlwaysPublic = false, bool IsSellable = false, uint RotateItemId = 0, int RotateItemCount = 0,
    int DecoLimit = 0, int AbsoluteDecoLimit = 0, string Name = "", int ViewSize = 0, bool AutoZ = false);

/// <summary>One housing_build_steps row plus the material its construction skill consumes.</summary>
public sealed record HousingBuildStepData(int Step, uint ModelId, uint SkillId, int NumActions,
    uint ConsumeItemId, int ConsumeItemCount);

/// <summary>Read-only item/design, build-step and area-group lookups used by housing placement and the house UI.</summary>
public sealed class HousingPlacementCatalog
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<uint, Lazy<HousingDesignData?>> _designs = new();
    private readonly ConcurrentDictionary<uint, Lazy<HousingDesignData?>> _designsById = new();
    private readonly ConcurrentDictionary<uint, IReadOnlyList<HousingBuildStepData>> _steps = new();
    private readonly ConcurrentDictionary<(int ZoneKey, uint CategoryId), bool> _areaRules = new();
    private readonly ConcurrentDictionary<(int AreaId, uint CategoryId), bool> _areaCategoryRules = new();

    public HousingPlacementCatalog(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    /// <summary>Resolves an item template through item_housings.design_id to the housings row.</summary>
    public HousingDesignData? ResolveDesign(uint itemTemplateId) =>
        _designs.GetOrAdd(itemTemplateId,
            id => new Lazy<HousingDesignData?>(() => LoadDesign(id, byItem: true),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Resolves a housings.id (the template id a placed house reports).</summary>
    public HousingDesignData? DesignById(uint designId) =>
        _designsById.GetOrAdd(designId,
            id => new Lazy<HousingDesignData?>(() => LoadDesign(id, byItem: false),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>housing_build_steps of a design in step order, with each step skill's consumed material.</summary>
    public IReadOnlyList<HousingBuildStepData> BuildSteps(uint designId) => _steps.GetOrAdd(designId, LoadSteps);

    /// <summary>
    /// Mirrors the server's housing_areas to housing_group_categories eligibility lookup.
    /// This is client feedback only; territory, collision, ownership, and other placement checks remain server-owned.
    /// </summary>
    public bool IsCategoryAllowedInZone(int zoneKey, uint categoryId)
    {
        if (zoneKey < 0 || categoryId == 0)
            return false;
        return _areaRules.GetOrAdd((zoneKey, categoryId), IsAllowed);
    }

    /// <summary>
    /// Whether the activated housing_areas row (the client housing_area.xml shape's value1) belongs to a housing
    /// group that admits the design category.
    /// </summary>
    public bool IsCategoryAllowedInArea(int areaId, uint categoryId)
    {
        if (areaId <= 0 || categoryId == 0)
            return false;
        return _areaCategoryRules.GetOrAdd((areaId, categoryId), key =>
        {
            using var database = Open();
            using var command = database.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM housing_areas AS area
                      JOIN housing_group_categories AS category ON category.housing_group_id = area.housing_group_id
                     WHERE area.id = $area AND area.activated = 't' AND category.category_id = $category);
                """;
            command.Parameters.AddWithValue("$area", key.AreaId);
            command.Parameters.AddWithValue("$category", (long)key.CategoryId);
            return Convert.ToInt64(command.ExecuteScalar()) != 0;
        });
    }

    private SqliteConnection Open()
    {
        var database = new SqliteConnection(_connectionString);
        database.Open();
        return database;
    }

    private HousingDesignData? LoadDesign(uint id, bool byItem)
    {
        using var database = Open();
        using var command = database.CreateCommand();
        var source = byItem
            ? "item_housings AS item JOIN housings AS design ON design.id = item.design_id"
            : "housings AS design";
        command.CommandText =
            "SELECT design.id, design.category_id, design.main_model_id, COALESCE(size.garden_radius, 0), " +
            (byItem ? "item.completion, " : "'f', ") +
            "COALESCE(design.taxation_id, 0), design.heavy_tax, design.always_public, design.is_sellable, " +
            "COALESCE(design.rotate_item_id, 0), COALESCE(design.rotate_item_count, 0), " +
            "COALESCE(design.deco_limit, 0), COALESCE(design.absolute_deco_limit, 0), " +
            "COALESCE(NULLIF(name.en_us, ''), design.name, ''), COALESCE(view.value, 0), design.auto_z " +
            "FROM " + source + " " +
            "LEFT JOIN housing_sizes AS size ON size.id = design.housing_size_id " +
            "LEFT JOIN housing_view_sizes AS view ON view.id = size.housing_view_size_id " +
            "LEFT JOIN localized_texts AS name " +
            "  ON name.tbl_name = 'housings' AND name.tbl_column_name = 'name' AND name.idx = design.id " +
            "WHERE " + (byItem ? "item.item_id" : "design.id") + " = $id " +
            (byItem ? "ORDER BY item.id " : "") + "LIMIT 1;";
        command.Parameters.AddWithValue("$id", (long)id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new HousingDesignData(
            byItem ? id : 0,
            U32(reader, 0), U32(reader, 1), U32(reader, 2),
            reader.IsDBNull(3) ? 0f : (float)reader.GetDouble(3),
            Flag(reader, 4), U32(reader, 5), Flag(reader, 6), Flag(reader, 7), Flag(reader, 8),
            U32(reader, 9), checked((int)U32(reader, 10)), checked((int)U32(reader, 11)),
            checked((int)U32(reader, 12)), reader.IsDBNull(13) ? "" : reader.GetString(13),
            checked((int)U32(reader, 14)), Flag(reader, 15));
    }

    private IReadOnlyList<HousingBuildStepData> LoadSteps(uint designId)
    {
        using var database = Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT step.step, COALESCE(step.model_id, 0), COALESCE(step.skill_id, 0), COALESCE(step.num_actions, 0),
                   COALESCE((SELECT effect.consume_item_id FROM skill_effects AS effect
                              WHERE effect.skill_id = step.skill_id AND effect.consume_item_id > 0
                              ORDER BY effect.id LIMIT 1), 0),
                   COALESCE((SELECT effect.consume_item_count FROM skill_effects AS effect
                              WHERE effect.skill_id = step.skill_id AND effect.consume_item_id > 0
                              ORDER BY effect.id LIMIT 1), 0)
              FROM housing_build_steps AS step
             WHERE step.housing_id = $design
             ORDER BY step.step;
            """;
        command.Parameters.AddWithValue("$design", (long)designId);
        var steps = new List<HousingBuildStepData>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            steps.Add(new HousingBuildStepData(checked((int)reader.GetInt64(0)), U32(reader, 1), U32(reader, 2),
                checked((int)reader.GetInt64(3)), U32(reader, 4), checked((int)reader.GetInt64(5))));
        return steps;
    }

    private static uint U32(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? 0u : checked((uint)reader.GetInt64(column));

    private static bool Flag(SqliteDataReader reader, int column)
    {
        if (reader.IsDBNull(column))
            return false;
        var value = reader.GetValue(column);
        return value is string text ? text is "t" or "true" or "1" : Convert.ToInt64(value) != 0;
    }

    private bool IsAllowed((int ZoneKey, uint CategoryId) key)
    {
        using var database = Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                  FROM zones AS zone
                  JOIN zone_groups AS zone_group ON zone_group.id = zone.group_id
                  JOIN housing_areas AS area
                    ON area.name IN (zone.name, zone_group.name) AND area.activated = 't'
                  JOIN housing_group_categories AS category
                    ON category.housing_group_id = area.housing_group_id
                 WHERE zone.zone_key = $zoneKey
                   AND category.category_id = $categoryId
            );
            """;
        command.Parameters.AddWithValue("$zoneKey", key.ZoneKey);
        command.Parameters.AddWithValue("$categoryId", (long)key.CategoryId);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
}

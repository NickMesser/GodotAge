#nullable enable

using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Client;

/// <summary>A housing design item and the static house model selected for its placement preview.</summary>
public sealed record HousingDesignData(uint ItemTemplateId, uint DesignId, uint CategoryId, uint MainModelId);

/// <summary>Read-only item/design and zone/category lookups needed for housing placement feedback.</summary>
public sealed class HousingPlacementCatalog
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<uint, Lazy<HousingDesignData?>> _designs = new();
    private readonly ConcurrentDictionary<(int ZoneKey, uint CategoryId), bool> _areaRules = new();

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
            id => new Lazy<HousingDesignData?>(() => LoadDesign(id), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

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

    private HousingDesignData? LoadDesign(uint itemTemplateId)
    {
        using var database = new SqliteConnection(_connectionString);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT design.id, design.category_id, design.main_model_id
              FROM item_housings AS item
              JOIN housings AS design ON design.id = item.design_id
             WHERE item.item_id = $itemId
             ORDER BY item.id
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$itemId", (long)itemTemplateId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new HousingDesignData(
            itemTemplateId,
            reader.IsDBNull(0) ? 0u : checked((uint)reader.GetInt64(0)),
            reader.IsDBNull(1) ? 0u : checked((uint)reader.GetInt64(1)),
            reader.IsDBNull(2) ? 0u : checked((uint)reader.GetInt64(2)));
    }

    private bool IsAllowed((int ZoneKey, uint CategoryId) key)
    {
        using var database = new SqliteConnection(_connectionString);
        database.Open();
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

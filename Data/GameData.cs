#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using AAEmu.GodotViewer.Ui.X2;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Data;

public sealed record ItemStats(
    string? EquipmentKind,
    int? ArmorBasePoints,
    int? MagicResistanceBasePoints,
    int? AttackSpeed,
    int? MinRange,
    int? MaxRange,
    string? DpsFormula,
    string? MagicDpsFormula,
    string? HealDpsFormula,
    string? ArmorFormula,
    string? MagicResistanceFormula);

public sealed record ItemData(
    long Id,
    string Name,
    string Description,
    int Level,
    int LevelRequirement,
    int CategoryId,
    string Category,
    int? IconId,
    string? IconPath,
    int GradeId,
    ItemGradeData? Grade,
    int? EquipSlotTypeId,
    string? EquipSlot,
    ItemStats Stats,
    uint UseSkillId);

public sealed record ItemGradeData(
    int Id,
    string Name,
    int GradeOrder,
    string ColorArgb,
    int? IconId,
    string? IconPath,
    double DurabilityValue,
    int StatMultiplier);

public sealed record SkillData(
    long Id,
    string Name,
    string Description,
    int? IconId,
    string? IconPath,
    int AbilityId,
    int AbilityLevel,
    int LevelStep,
    int ManaCost,
    int CooldownMilliseconds,
    int CastingMilliseconds,
    int ChannelingMilliseconds,
    int MinRange,
    int MaxRange,
    int TargetAreaRadius,
    int TargetTypeId,
    int TargetSelectionId,
    int TargetRelationId,
    int? FxGroupId,
    int? StartAnimationId,
    int? FireAnimationId,
    int? ChannelingAnimationId,
    int? TwohandFireAnimationId,
    int? DualWieldFireAnimationId);

public sealed record BuffData(
    long Id,
    string Name,
    string Description,
    int? IconId,
    string? IconPath,
    int DurationMilliseconds,
    int MaxStack,
    int KindId,
    int? FxGroupId);

public sealed record NpcData(
    long Id,
    string Name,
    int Level,
    int HeirLevel,
    int GradeId,
    string Grade,
    int KindId,
    string Kind,
    int FactionId,
    int Aggression,
    bool HasDialogue,
    bool HasLootPack);

/// <summary>Native quest conversation camera values, in Cry local axes (X right, Y forward, Z up).</summary>
public sealed record QuestCameraData(
    float FieldOfView,
    float NpcOffsetX, float NpcOffsetY, float NpcOffsetZ,
    float CameraOffsetX, float CameraOffsetY, float CameraOffsetZ,
    bool HidePlayer, bool Interpolate, bool DepthOfField,
    float BokehSize, float Intensity, float Luminance);

public sealed record AnimationData(int Id, string Name, bool Loop);

/// <summary>One particle effect item referenced by an FX group. Offsets are in Cry axes.</summary>
public sealed record FxGroupItemData(
    int Id,
    string AssetName,
    int StartEventId,
    int EndEventId,
    int LocationId,
    string Bone,
    float OffsetX,
    float OffsetY,
    float OffsetZ);

public sealed record QuestData(
    long Id,
    string Name,
    string Summary,
    string Body,
    string? SubTitle,
    string? ProgressTitle,
    int Level,
    int MinLevel,
    int MaxLevel,
    int CategoryId,
    int ZoneId,
    int GradeId,
    bool Repeatable,
    bool Selective,
    bool Successive,
    int ChapterIndex,
    int QuestIndex);

public sealed record SubZoneData(long Id, int Index, string Name, int ParentSubZoneId);

public sealed record ZoneData(
    long Id,
    int ZoneKey,
    string Name,
    string DisplayText,
    int? GroupId,
    int? FactionId,
    IReadOnlyList<SubZoneData> SubZones);

/// <summary>
/// Thread-safe, lazily cached access to the client's immutable game-content database.
/// Each calling thread receives its own read-only SQLite connection.
/// </summary>
public sealed class GameData : IDisposable
{
    private sealed record ItemEquipment(int? SlotId, ItemStats Stats);

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private readonly ThreadLocal<SqliteConnection> _connections;
    private readonly ConcurrentDictionary<long, Lazy<ItemData?>> _items = new();
    private readonly ConcurrentDictionary<int, Lazy<ItemGradeData?>> _grades = new();
    private readonly ConcurrentDictionary<long, Lazy<SkillData?>> _skills = new();
    private readonly ConcurrentDictionary<long, Lazy<BuffData?>> _buffs = new();
    private readonly ConcurrentDictionary<long, Lazy<NpcData?>> _npcs = new();
    private readonly ConcurrentDictionary<long, Lazy<QuestCameraData?>> _questCameras = new();
    private readonly ConcurrentDictionary<int, Lazy<AnimationData?>> _animations = new();
    private readonly ConcurrentDictionary<int, Lazy<IReadOnlyList<FxGroupItemData>>> _fxGroups = new();
    private readonly ConcurrentDictionary<(int From, int To), Lazy<int?>> _factionRelations = new();
    private readonly ConcurrentDictionary<long, Lazy<QuestData?>> _quests = new();
    private readonly ConcurrentDictionary<int, Lazy<ZoneData?>> _zones = new();
    private readonly ConcurrentDictionary<int, long> _levelExperience = new();
    private readonly ConcurrentDictionary<int, Lazy<string?>> _icons = new();
    private readonly ConcurrentDictionary<int, string> _equipSlotNames = new();
    private readonly ConcurrentDictionary<(string Table, string Column, long Id), Lazy<string>> _texts = new();
    private readonly ConcurrentDictionary<string, Lazy<string>> _uiTexts = new(StringComparer.Ordinal);
    private readonly Lazy<Dictionary<long, ItemEquipment>> _itemEquipment;
    private bool _disposed;

    public GameData(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A database path is required.", nameof(databasePath));
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Game database was not found.", fullPath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        _connections = new ThreadLocal<SqliteConnection>(() =>
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            ClientEnums.Ensure(connection);
            return connection;
        }, trackAllValues: true);
        _itemEquipment = new Lazy<Dictionary<long, ItemEquipment>>(LoadItemEquipment,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ItemData? GetItem(long id) => Cached(_items, id, LoadItem);
    public ItemGradeData? GetItemGrade(int id) => Cached(_grades, id, LoadGrade);
    public SkillData? GetSkill(long id) => Cached(_skills, id, LoadSkill);
    public BuffData? GetBuff(long id) => Cached(_buffs, id, LoadBuff);
    public NpcData? GetNpc(long id) => Cached(_npcs, id, LoadNpc);
    public QuestCameraData? GetQuestCamera(long npcId) => Cached(_questCameras, npcId, LoadQuestCamera);

    public string? GetQuestMailSender(long questMailId)
    {
        using var command = Command(@"SELECT COALESCE(NULLIF(qname.en_us,''), NULLIF(nname.en_us,''))
FROM quest_mails q
LEFT JOIN localized_texts qname ON qname.tbl_name='quest_mails'
 AND qname.tbl_column_name='sender_name' AND qname.idx=q.id
LEFT JOIN localized_texts nname ON nname.tbl_name='npcs'
 AND nname.tbl_column_name='name' AND nname.idx=q.npc_id
WHERE q.id=@id LIMIT 1", ("@id", questMailId));
        return command.ExecuteScalar() is { } sender and not DBNull
            ? Convert.ToString(sender, CultureInfo.InvariantCulture) : null;
    }
    public AnimationData? GetAnimation(int id) => Cached(_animations, id, LoadAnimation);
    public IReadOnlyList<FxGroupItemData> GetFxGroupItems(int groupId) =>
        groupId <= 0 ? [] : _fxGroups.GetOrAdd(groupId,
            id => new Lazy<IReadOnlyList<FxGroupItemData>>(() => LoadFxGroupItems(id), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Returns system_faction_relations.state_id, checking both stored directions and mother factions.</summary>
    public int? GetFactionRelation(int fromFactionId, int toFactionId)
    {
        if (fromFactionId <= 0 || toFactionId <= 0) return null;
        return _factionRelations.GetOrAdd((fromFactionId, toFactionId),
            pair => new Lazy<int?>(() => LoadFactionRelation(pair), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
    public QuestData? GetQuest(long id) => Cached(_quests, id, LoadQuest);
    public ZoneData? GetZone(int key) => Cached(_zones, key, LoadZone);

    /// <summary>Total experience required to reach this level in the client's levels table.</summary>
    public long GetLevelExperience(int level) => level < 1 ? 0 : _levelExperience.GetOrAdd(level, id =>
    {
        using var command = Command("SELECT total_exp FROM levels WHERE id=@id LIMIT 1", ("@id", id));
        return command.ExecuteScalar() is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;
    });

    /// <summary>Resolves an icons.id to its canonical path inside game_pak.</summary>
    public string? IconPath(int iconId) => Cached(_icons, iconId, LoadIconPath);

    /// <summary>Returns localized_texts.en_us when present and non-empty, otherwise the source cell.</summary>
    public string GetText(string table, string column, long id)
    {
        ValidateIdentifier(table, nameof(table));
        ValidateIdentifier(column, nameof(column));
        var key = (table.ToLowerInvariant(), column.ToLowerInvariant(), id);
        return Cached(_texts, key, k => LoadText(k.Table, k.Column, k.Id));
    }

    /// <summary>Returns the English UI string by its stable ui_texts.key, falling back to ui_texts.text.</summary>
    public string GetUiText(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _uiTexts.GetOrAdd(key, value => new Lazy<string>(() => LoadUiText(value),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private QuestCameraData? LoadQuestCamera(long npcId)
    {
        static bool Boolean(SqliteDataReader reader, int ordinal)
        {
            var value = reader.GetValue(ordinal);
            return value switch
            {
                bool boolean => boolean,
                string text => text.Equals("t", StringComparison.OrdinalIgnoreCase) ||
                               text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1",
                _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
            };
        }

        using var command = Command(@"
SELECT qc.fov, qc.npc_offset_x, qc.npc_offset_y, qc.npc_offset_z,
       qc.camera_offset_x, qc.camera_offset_y, qc.camera_offset_z,
       qc.invisible, qc.interpolate, qc.dof,
       qc.nv_bokeh_size, qc.nv_intensity, qc.nv_luminance
FROM npcs n
JOIN model_quest_cameras mqc ON mqc.model_id=n.model_id
JOIN quest_cameras qc ON qc.id=mqc.quest_camera_id
WHERE n.id=@id LIMIT 1", ("@id", npcId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new QuestCameraData(
            reader.GetFloat(0),
            reader.GetFloat(1), reader.GetFloat(2), reader.GetFloat(3),
            reader.GetFloat(4), reader.GetFloat(5), reader.GetFloat(6),
            Boolean(reader, 7), Boolean(reader, 8), Boolean(reader, 9),
            reader.GetFloat(10), reader.GetFloat(11), reader.GetFloat(12));
    }

    private SqliteConnection Connection => !_disposed
        ? _connections.Value!
        : throw new ObjectDisposedException(nameof(GameData));

    private static TValue? Cached<TKey, TValue>(ConcurrentDictionary<TKey, Lazy<TValue?>> cache, TKey key, Func<TKey, TValue?> load)
        where TKey : notnull where TValue : class =>
        cache.GetOrAdd(key, k => new Lazy<TValue?>(() => load(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static string Cached(ConcurrentDictionary<(string Table, string Column, long Id), Lazy<string>> cache,
        (string Table, string Column, long Id) key, Func<(string Table, string Column, long Id), string> load) =>
        cache.GetOrAdd(key, k => new Lazy<string>(() => load(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private ItemData? LoadItem(long id)
    {
        using var command = Command(@"
SELECT COALESCE(NULLIF(il.en_us,''),i.name), COALESCE(NULLIF(idl.en_us,''),i.description),
       i.level, i.level_requirement, i.category_id,
       COALESCE(NULLIF(icl.en_us,''),ic.name,''), i.icon_id, i.fixed_grade, icon.filename, i.use_skill_id
FROM items i
LEFT JOIN localized_texts il ON il.tbl_name='items' AND il.tbl_column_name='name' AND il.idx=i.id
  AND il.en_us<>'' AND instr(upper(il.en_us),'DO NOT TRANSLATE')=0 AND upper(ltrim(il.en_us)) NOT LIKE 'TEST:%'
LEFT JOIN localized_texts idl ON idl.tbl_name='items' AND idl.tbl_column_name='description' AND idl.idx=i.id
  AND idl.en_us<>'' AND instr(upper(idl.en_us),'DO NOT TRANSLATE')=0 AND upper(ltrim(idl.en_us)) NOT LIKE 'TEST:%'
LEFT JOIN item_categories ic ON ic.id=i.category_id
LEFT JOIN localized_texts icl ON icl.tbl_name='item_categories' AND icl.tbl_column_name='name' AND icl.idx=ic.id
  AND icl.en_us<>'' AND instr(upper(icl.en_us),'DO NOT TRANSLATE')=0 AND upper(ltrim(icl.en_us)) NOT LIKE 'TEST:%'
LEFT JOIN icons icon ON icon.id=i.icon_id
WHERE i.id=@id LIMIT 1", ("@id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        var v = Snapshot(r); r.Dispose();
        var gradeId = I32(v, 7); var categoryId = I32(v, 4); var iconId = NI32(v, 6);
        _itemEquipment.Value.TryGetValue(id, out var equipment);
        return new ItemData(id, UiTranslator.Shared.TranslateDatabaseText(S(v, 0)), UiTranslator.Shared.TranslateDatabaseText(S(v, 1)),
            I32(v, 2), I32(v, 3), categoryId, S(v, 5),
            iconId, NormalizeIconPath(NS(v, 8)), gradeId, gradeId >= 0 ? GetItemGrade(gradeId) : null,
            equipment?.SlotId, equipment?.SlotId is { } slotId ? EquipSlotName(slotId) : null,
            equipment?.Stats ?? new ItemStats(null, null, null, null, null, null, null, null, null, null, null),
            unchecked((uint)I32(v, 9)));
    }

    private Dictionary<long, ItemEquipment> LoadItemEquipment()
    {
        var result = new Dictionary<long, ItemEquipment>();
        const string sql = @"SELECT a.item_id,a.slot_type_id,'armor',wr.armor_bp,wr.magic_resistance_bp,
NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL FROM item_armors a
LEFT JOIN wearables wr ON wr.armor_type_id=a.type_id AND wr.slot_type_id=a.slot_type_id
UNION ALL
SELECT w.item_id,h.slot_type_id,'weapon',NULL,NULL,h.speed,h.min_range,h.max_range,h.formula_dps,
h.formula_mdps,h.formula_hdps,h.formula_armor,h.formula_magic_resist FROM item_weapons w
LEFT JOIN holdables h ON h.id=w.holdable_id
UNION ALL
SELECT x.item_id,x.slot_type_id,'accessory',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL FROM item_accessories x";
        using var command = Command(sql);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stats = new ItemStats(EmptyToNull(S(reader, 2)), NI32(reader, 3), NI32(reader, 4),
                NI32(reader, 5), NI32(reader, 6), NI32(reader, 7), NS(reader, 8), NS(reader, 9),
                NS(reader, 10), NS(reader, 11), NS(reader, 12));
            result.TryAdd(reader.GetInt64(0), new ItemEquipment(NI32(reader, 1), stats));
        }
        return result;
    }

    private string? EquipSlotName(int slotId)
    {
        var name = _equipSlotNames.GetOrAdd(slotId, value =>
        {
            using var command = Command("SELECT name FROM enum_equip_slot_types WHERE id=@id", ("@id", value));
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        });
        return EmptyToNull(name);
    }

    private ItemGradeData? LoadGrade(int id)
    {
        using var command = Command("SELECT name,grade_order,color_argb,icon_id,durability_value,stat_multiplier FROM item_grades WHERE id=@id", ("@id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        var v = Snapshot(r); r.Dispose(); var icon = NI32(v, 3);
        return new ItemGradeData(id, Localized("item_grades", "name", id, S(v, 0)), I32(v, 1), S(v, 2), icon,
            icon is { } iconId ? IconPath(iconId) : GradeFramePath(id), D(v, 4), I32(v, 5));
    }

    private SkillData? LoadSkill(long id)
    {
        using var command = Command(@"SELECT COALESCE(NULLIF(n.en_us,''),s.name),COALESCE(NULLIF(d.en_us,''),s.desc),
s.icon_id,icon.filename,s.ability_id,s.ability_level,s.level_step,s.mana_cost,s.cooldown_time,
s.casting_time,s.channeling_time,s.min_range,s.max_range,s.target_area_radius,
s.target_type_id,s.target_selection_id,s.target_relation_id,s.fx_group_id,s.start_anim_id,s.fire_anim_id,
s.channeling_anim_id,s.twohand_fire_anim_id,s.dual_wield_fire_anim_id
FROM skills s LEFT JOIN localized_texts n ON n.tbl_name='skills' AND n.tbl_column_name='name' AND n.idx=s.id
 AND n.en_us<>'' AND instr(upper(n.en_us),'DO NOT TRANSLATE')=0
LEFT JOIN localized_texts d ON d.tbl_name='skills' AND d.tbl_column_name='desc' AND d.idx=s.id
 AND d.en_us<>'' AND instr(upper(d.en_us),'DO NOT TRANSLATE')=0
LEFT JOIN icons icon ON icon.id=s.icon_id WHERE s.id=@id", ("@id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        var v = Snapshot(r); r.Dispose(); var icon = NI32(v, 2);
        return new SkillData(id, S(v, 0), S(v, 1), icon,
            NormalizeIconPath(NS(v, 3)), I32(v, 4), I32(v, 5), I32(v, 6), I32(v, 7), I32(v, 8),
            I32(v, 9), I32(v, 10), I32(v, 11), I32(v, 12), I32(v, 13),
            I32(v, 14), I32(v, 15), I32(v, 16), NI32(v, 17), NI32(v, 18), NI32(v, 19),
            NI32(v, 20), NI32(v, 21), NI32(v, 22));
    }

    private BuffData? LoadBuff(long id)
    {
        using var command = Command(@"SELECT COALESCE(NULLIF(n.en_us,''),b.name),COALESCE(NULLIF(d.en_us,''),b.desc),
 b.icon_id,icon.filename,b.duration,b.max_stack,b.kind_id,b.fx_group_id FROM buffs b
LEFT JOIN localized_texts n ON n.tbl_name='buffs' AND n.tbl_column_name='name' AND n.idx=b.id
 AND n.en_us<>'' AND instr(upper(n.en_us),'DO NOT TRANSLATE')=0
LEFT JOIN localized_texts d ON d.tbl_name='buffs' AND d.tbl_column_name='desc' AND d.idx=b.id
 AND d.en_us<>'' AND instr(upper(d.en_us),'DO NOT TRANSLATE')=0
LEFT JOIN icons icon ON icon.id=b.icon_id WHERE b.id=@id", ("@id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        var v = Snapshot(r); r.Dispose(); var icon = NI32(v, 2);
        return new BuffData(id, S(v, 0), S(v, 1), icon,
            NormalizeIconPath(NS(v, 3)), I32(v, 4), I32(v, 5), I32(v, 6), NI32(v, 7));
    }

    private NpcData? LoadNpc(long id)
    {
        using var command = Command(@"SELECT n.name,n.level,n.heir_level,n.npc_grade_id,COALESCE(g.name,''),
n.npc_kind_id,COALESCE(k.name,''),n.faction_id,n.aggression,
EXISTS(SELECT 1 FROM npc_interactions i WHERE i.npc_interaction_set_id=n.npc_interaction_set_id),
EXISTS(SELECT 1 FROM loot_pack_dropping_npcs l WHERE l.npc_id=n.id) FROM npcs n
LEFT JOIN enum_npc_grade g ON g.id=n.npc_grade_id LEFT JOIN enum_npc_kind k ON k.id=n.npc_kind_id WHERE n.id=@id", ("@id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        var v = Snapshot(r); r.Dispose();
        return new NpcData(id, Localized("npcs", "name", id, S(v, 0)), I32(v, 1), I32(v, 2), I32(v, 3), S(v, 4),
            I32(v, 5), S(v, 6), I32(v, 7), I32(v, 8), I32(v, 9) != 0, I32(v, 10) != 0);
    }

    private AnimationData? LoadAnimation(int id)
    {
        using var command = Command("SELECT name,loop FROM anims WHERE id=@id", ("@id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        return new AnimationData(id, S(r, 0), B(S(r, 1)));
    }

    private IReadOnlyList<FxGroupItemData> LoadFxGroupItems(int groupId)
    {
        using var command = Command(@"SELECT f.id,f.asset_name,f.fx_event_start_id,f.fx_event_end_id,
f.fx_location_id,COALESCE(b.name,''),f.offset_x,f.offset_y,f.offset_z
FROM fx_group_fx_items g JOIN fx_items f ON f.id=g.fx_item_id
LEFT JOIN enum_effect_bone b ON b.id=f.bone_id
WHERE g.fx_group_id=@id ORDER BY g.id", ("@id", groupId));
        using var r = command.ExecuteReader();
        var items = new List<FxGroupItemData>();
        while (r.Read())
            items.Add(new FxGroupItemData(I32(r, 0), S(r, 1), I32(r, 2), I32(r, 3), I32(r, 4), S(r, 5),
                Convert.ToSingle(r.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToSingle(r.GetValue(7), CultureInfo.InvariantCulture),
                Convert.ToSingle(r.GetValue(8), CultureInfo.InvariantCulture)));
        return items;
    }

    private int? LoadFactionRelation((int From, int To) pair)
    {
        if (pair.From == pair.To) return 3; // RelationState.Friendly in AAEmu's RelationState enum.
        using var command = Command(@"SELECT state_id FROM system_faction_relations
WHERE (faction1_id=@from AND faction2_id=@to) OR (faction1_id=@to AND faction2_id=@from)
LIMIT 1", ("@from", pair.From), ("@to", pair.To));
        var value = command.ExecuteScalar();
        if (value is not null and not DBNull)
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);

        var fromMother = 0;
        var toMother = 0;
        {
            using var mothers = Command("SELECT id,mother_id FROM system_factions WHERE id IN (@from,@to)",
                ("@from", pair.From), ("@to", pair.To));
            using var r = mothers.ExecuteReader();
            while (r.Read())
            {
                if (I32(r, 0) == pair.From) fromMother = I32(r, 1);
                if (I32(r, 0) == pair.To) toMother = I32(r, 1);
            }
        }
        if (fromMother != 0 && fromMother != pair.From)
        {
            var relation = LoadFactionRelation((fromMother, pair.To));
            if (relation is not null) return relation;
        }
        if (toMother != 0 && toMother != pair.To)
            return LoadFactionRelation((pair.From, toMother));
        return null;
    }

    private QuestData? LoadQuest(long id)
    {
        using var command = Command(@"SELECT name,level,min_level,max_level,category_id,zone_id,grade_id,repeatable,selective,
successive,chapter_idx,quest_idx FROM quest_contexts WHERE id=@id", ("@id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        var v = Snapshot(r); r.Dispose();
        return new QuestData(id, Localized("quest_contexts", "name", id, S(v, 0)), QuestText(id, 1), QuestText(id, 2),
            QuestName(id, 1), QuestName(id, 2), I32(v, 1), I32(v, 2), I32(v, 3), I32(v, 4), I32(v, 5), I32(v, 6),
            B(v, 7), B(v, 8), B(v, 9), I32(v, 10), I32(v, 11));
    }

    private ZoneData? LoadZone(int key)
    {
        using var command = Command(@"SELECT id,zone_key,name,display_text,group_id,faction_id FROM zones
WHERE zone_key=@key OR id=@key ORDER BY CASE WHEN zone_key=@key THEN 0 ELSE 1 END LIMIT 1", ("@key", key));
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        var v = Snapshot(r); r.Dispose(); var id = Convert.ToInt64(v[0], CultureInfo.InvariantCulture); var group = NI32(v, 4);
        var rawSubZones = new List<(long Id, int Index, string Name, int Parent)>();
        using (var sub = Command("SELECT id,idx,name,parent_sub_zone_id FROM sub_zones WHERE linked_zone_group_id=@g ORDER BY idx", ("@g", group ?? -1)))
        using (var sr = sub.ExecuteReader())
            while (sr.Read()) rawSubZones.Add((sr.GetInt64(0), I32(sr, 1), S(sr, 2), I32(sr, 3)));
        var subZones = new List<SubZoneData>(rawSubZones.Count);
        foreach (var z in rawSubZones) subZones.Add(new SubZoneData(z.Id, z.Index, Localized("sub_zones", "name", z.Id, z.Name), z.Parent));
        return new ZoneData(id, I32(v, 1), Localized("zones", "name", id, S(v, 2)), Localized("zones", "display_text", id, S(v, 3)),
            group, NI32(v, 5), new ReadOnlyCollection<SubZoneData>(subZones));
    }

    private string? LoadIconPath(int id)
    {
        using var command = Command("SELECT filename FROM icons WHERE id=@id", ("@id", id));
        var value = command.ExecuteScalar();
        if (value is null or DBNull) return null;
        return NormalizeIconPath(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static string? NormalizeIconPath(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        var path = filename.Replace('\\', '/').TrimStart('/');
        if (path.StartsWith("game/", StringComparison.OrdinalIgnoreCase)) return path.ToLowerInvariant();
        if (path.StartsWith("ui/", StringComparison.OrdinalIgnoreCase)) return ("game/" + path).ToLowerInvariant();
        return ("game/ui/icon/" + path).ToLowerInvariant();
    }

    private static string? GradeFramePath(int id) => id switch
    {
        0 => "game/ui/icon/item_grade_1common.dds", 1 => "game/ui/icon/item_grade_0poor.dds",
        2 => "game/ui/icon/item_grade_2uncommon.dds", 3 => "game/ui/icon/item_grade_3rare.dds",
        4 => "game/ui/icon/item_grade_4ancient.dds", 5 => "game/ui/icon/item_grade_5heroic.dds",
        6 => "game/ui/icon/item_grade_6unique.dds", 7 => "game/ui/icon/item_grade_7artifact.dds",
        8 => "game/ui/icon/item_grade_8wonder.dds", 9 => "game/ui/icon/item_grade_9epic.dds",
        10 => "game/ui/icon/item_grade_10legendary.dds", 11 => "game/ui/icon/item_grade_11mythic.dds",
        12 => "game/ui/icon/item_grade_12arche.dds", _ => null
    };

    private string QuestText(long questId, int kind)
    {
        using var command = Command("SELECT id,text FROM quest_context_texts WHERE quest_context_id=@id AND quest_context_text_kind_id=@kind ORDER BY id LIMIT 1", ("@id", questId), ("@kind", kind));
        using var r = command.ExecuteReader();
        if (!r.Read()) return "";
        var rowId = r.GetInt64(0); var fallback = S(r, 1); r.Dispose();
        return Localized("quest_context_texts", "text", rowId, fallback);
    }

    private string? QuestName(long questId, int kind)
    {
        using var command = Command("SELECT id,name FROM quest_names WHERE quest_context_id=@id AND quest_name_kind_id=@kind ORDER BY id LIMIT 1", ("@id", questId), ("@kind", kind));
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        var rowId = r.GetInt64(0); var fallback = S(r, 1); r.Dispose();
        return EmptyToNull(Localized("quest_names", "name", rowId, fallback));
    }

    private string Localized(string table, string column, long id, string fallback)
    {
        using var command = Command(@"SELECT en_us FROM localized_texts
WHERE tbl_name=@table AND tbl_column_name=@column AND idx=@id AND en_us<>''
  AND instr(upper(en_us),'DO NOT TRANSLATE')=0 ORDER BY id LIMIT 1",
            ("@table", table), ("@column", column), ("@id", id));
        var localized = command.ExecuteScalar();
        return UiTranslator.Shared.TranslateDatabaseText(
            localized is not null and not DBNull ? Convert.ToString(localized, CultureInfo.InvariantCulture) : null, fallback);
    }

    private string LoadText(string table, string column, long id)
    {
        string? localizedValue = null;
        using (var localized = Command(@"SELECT en_us FROM localized_texts
WHERE tbl_name=@table AND tbl_column_name=@column AND idx=@id AND en_us<>''
  ORDER BY id LIMIT 1",
                   ("@table", table), ("@column", column), ("@id", id)))
        {
            var value = localized.ExecuteScalar();
            if (value is not null and not DBNull) localizedValue = Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        using var source = Command($"SELECT \"{column}\" FROM \"{table}\" WHERE id=@id LIMIT 1", ("@id", id));
        var fallback = source.ExecuteScalar();
        return UiTranslator.Shared.TranslateDatabaseText(localizedValue,
            fallback is null or DBNull ? null : Convert.ToString(fallback, CultureInfo.InvariantCulture));
    }

    private string LoadUiText(string key)
    {
        using var command = Command(@"SELECT COALESCE(NULLIF(l.en_us,''),t.text)
FROM ui_texts t LEFT JOIN localized_texts l
  ON l.tbl_name='ui_texts' AND l.tbl_column_name='text' AND l.idx=t.id
 AND l.en_us<>'' AND instr(upper(l.en_us),'DO NOT TRANSLATE')=0
WHERE t.key=@key LIMIT 1", ("@key", key));
        var value = command.ExecuteScalar();
        return value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = Connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void ValidateIdentifier(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || !Identifier.IsMatch(value))
            throw new ArgumentException("Only SQLite identifiers made of ASCII letters, digits, and underscores are accepted.", parameter);
    }

    private static string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
    private static string? NS(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : S(r, i);
    private static object?[] Snapshot(SqliteDataReader r)
    {
        var values = new object?[r.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = r.IsDBNull(i) ? null : r.GetValue(i);
        return values;
    }
    private static string S(object?[] v, int i) => v[i] is null ? "" : Convert.ToString(v[i], CultureInfo.InvariantCulture) ?? "";
    private static string? NS(object?[] v, int i) => v[i] is null ? null : S(v, i);
    private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static int I32(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : ToInt(r.GetValue(i));
    private static int? NI32(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : I32(r, i);
    private static int I32(object?[] v, int i) => v[i] is null ? 0 : ToInt(v[i]);

    /// <summary>Integer columns; the DB stores some flags (e.g. npcs.aggression) as the text 't' / 'f'.</summary>
    private static int ToInt(object? value) => value switch
    {
        "t" or "T" or "true" => 1,
        "f" or "F" or "false" or "" => 0,
        _ => Convert.ToInt32(value, CultureInfo.InvariantCulture),
    };
    private static int? NI32(object?[] v, int i) => v[i] is null ? null : I32(v, i);
    private static double D(object?[] v, int i) => v[i] is null ? 0 : Convert.ToDouble(v[i], CultureInfo.InvariantCulture);
    private static bool B(object?[] v, int i) => v[i] is not null && (v[i] switch { bool b => b, string s => s is "t" or "true" or "1", _ => Convert.ToInt64(v[i], CultureInfo.InvariantCulture) != 0 });
    private static bool B(string value) => value is "1" or "t" or "true";

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var connection in _connections.Values) connection.Dispose();
        _connections.Dispose();
    }
}

#nullable enable
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Data;

/// <summary>
/// The <c>enum_*</c> lookup tables (id to name) exist in the full server database but not in the client database that
/// ships in the pak (<c>game/db/compact.sqlite3</c>). Call <see cref="Ensure"/> right after opening a connection: for every
/// enum table the code queries that the database lacks, it creates a temporary view of the same name and columns
/// (the temp schema is per connection and never touches the file), so the queries keep working unchanged.
/// Each of them has a small built-in copy of its rows (equipment slots, item kinds, NPC kinds, hotkey actions, map symbols ...);
/// only the server error names (1244 rows) are left empty, and the code then treats the name as unknown.
/// </summary>
public static class ClientEnums
{
    private static readonly ConcurrentDictionary<string, string[]> MissingByDatabase = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the temporary views for the enum tables that <paramref name="db"/> (already open) lacks.</summary>
    public static void Ensure(SqliteConnection db)
    {
        var missing = MissingByDatabase.GetOrAdd(db.DataSource ?? "", _ => FindMissing(db));
        foreach (var table in missing)
        {
            using var command = db.CreateCommand();
            command.CommandText = $"CREATE TEMP VIEW IF NOT EXISTS {table} AS {Fallbacks[table].Select()}";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>True when <paramref name="table"/> is one of the enum tables that <paramref name="db"/> lacks (so it is served by the built-in fallback).</summary>
    public static bool IsFallback(SqliteConnection db, string table)
    {
        Ensure(db);
        return MissingByDatabase.TryGetValue(db.DataSource ?? "", out var missing) && missing.Contains(table, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] FindMissing(SqliteConnection db)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','view') AND name LIKE 'enum!_%' ESCAPE '!'";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                present.Add(reader.GetString(0));
        return Fallbacks.Keys.Where(name => !present.Contains(name)).ToArray();
    }

    private sealed record Fallback(string[] Columns, object?[][] Rows)
    {
        public string Select()
        {
            if (Rows.Length == 0)
                return "SELECT " + string.Join(", ", Columns.Select(c => "NULL AS " + c)) + " WHERE 0";
            var sql = new StringBuilder();
            for (var i = 0; i < Rows.Length; i++)
            {
                sql.Append(i == 0 ? "SELECT " : " UNION ALL SELECT ");
                for (var c = 0; c < Columns.Length; c++)
                {
                    if (c > 0)
                        sql.Append(", ");
                    sql.Append(Literal(Rows[i][c]));
                    if (i == 0)
                        sql.Append(" AS ").Append(Columns[c]);
                }
            }
            return sql.ToString();
        }

        private static string Literal(object? value) => value switch
        {
            null => "NULL",
            string s => "'" + s.Replace("'", "''") + "'",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
        };
    }

    private static Fallback Names(params (int Id, string Name)[] rows) =>
        new(["id", "name"], rows.Select(r => new object?[] { r.Id, r.Name }).ToArray());

    private static readonly Dictionary<string, Fallback> Fallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        // essential: equipment slot types (which character slot an item goes to, and which asset code its model uses)
        ["enum_equip_slot_types"] = new(
            ["id", "name", "default_equip_slot_id", "aux_equip_slot_id", "category", "asset_code"],
            [
                [1, "head", 0, null, "wearable", "_hm_"],
                [2, "neck", 1, null, "wearable", null],
                [3, "chest", 2, null, "wearable", "_ar_"],
                [4, "waist", 3, null, "wearable", "_saddle_"],
                [5, "legs", 4, null, "wearable", "_pt_"],
                [6, "hands", 5, null, "wearable", "_gv_"],
                [7, "feet", 6, null, "wearable", "_bo_"],
                [8, "arms", 7, null, "wearable", null],
                [9, "back", 8, null, "wearable", "_cp_"],
                [10, "ear", 9, 10, "wearable", null],
                [11, "finger", 11, 12, "wearable", null],
                [12, "undershirt", 13, null, "wearable", null],
                [13, "underpants", 14, null, "wearable", "_in_"],
                [14, "mainhand", 15, null, "holdable", null],
                [15, "offhand", 16, null, "holdable", null],
                [16, "2handed", 15, null, "holdable", null],
                [17, "1handed", 15, 16, "holdable", null],
                [18, "ranged", 17, null, "holdable", null],
                [19, "ammunition", -1, null, "", null],
                [20, "shield", 16, null, "holdable", null],
                [21, "instrument", 18, null, "holdable", null],
                [22, "bag", -1, null, null, null],
                [23, "face", 19, null, "body", null],
                [24, "hair", 20, null, "body", null],
                [25, "glasses", 21, null, "body", "_gl_"],
                [26, "horns", 22, null, "body", null],
                [27, "tail", 23, null, "body", null],
                [28, "body", 24, null, "body", null],
                [29, "beard", 25, null, "body", null],
                [30, "backpack", 26, null, "backpack", null],
                [31, "cosplay", 27, 31, "wearable", "_sk_"],
                [32, "stabilizer", 28, null, "wearable", "_stabilizer_"],
                [33, "race_cosplay", 32, 33, "wearable", "_sk_"],
            ]),

        // essential: item implementation kind (weapon, armor, backpack ...) and its default item category
        ["enum_item_impls"] = new(
            ["id", "name", "default_category_id"],
            [
                [0, "misc", 0], [1, "weapon", 2], [2, "armor", 3], [3, "body", 0], [4, "bag", 5], [5, "housing", 6],
                [6, "housing_decoration", 7], [7, "tool", 0], [8, "summon_slave", 4], [9, "spawn_doodad", 8],
                [10, "accept_quest", 0], [11, "summon_mate", 4], [12, "recipe", 1], [13, "crafting", 1], [14, "portal", 0],
                [15, "enchanting_gem", 20], [16, "report_crime", 0], [17, "logic_doodad", 0], [18, "has_ucc", 0],
                [19, "open_emblem_ui", 0], [20, "shipyard", 0], [21, "socket", 0], [22, "backpack", 0], [23, "open_paper", 0],
                [24, "accessory", 0], [25, "treasure", 0], [26, "music_sheet", 0], [27, "dyeing", 0], [28, "slave_equipment", 0],
                [29, "grade_enchanting_support", 0], [30, "mate_armor", 108], [31, "location", 0], [32, "rename_character", 0],
                [33, "evolving_material", 199], [34, "butler_armor", 204], [35, "bless_uthstin", 1],
            ]),

        ["enum_item_bind_types"] = Names((1, "normal"), (2, "soulbound_pickup"), (3, "soulbound_equip"), (4, "soulbound_unpack"),
            (5, "soulbound_pickup_pack"), (6, "soulbound_pickup_auction_win")),

        ["enum_npc_grade"] = Names((1, "normal"), (2, "elite"), (3, "boss_a"), (4, "boss_b"), (5, "boss_c"), (6, "weak"), (7, "strong"),
            (8, "boss_s")),

        ["enum_npc_kind"] = Names((1, "human"), (2, "beast"), (3, "undead"), (4, "devil"), (5, "spirit"), (8, "dragon"),
            (9, "siege_weapon"), (10, "ship"), (11, "horse"), (12, "carriage"), (13, "fantastic"), (14, "machine"), (15, "unknown"),
            (16, "mannequin")),

        // effect attach points on a model (skill effects)
        ["enum_effect_bone"] = Names((0, "none"), (1, "spine"), (2, "head"), (3, "neck"), (4, "hand_l"), (5, "hand_r"), (6, "foot_l"),
            (7, "foot_r"), (8, "item_hand_l"), (9, "item_hand_r"), (10, "effect_1"), (11, "effect_2"), (12, "effect_3"),
            (13, "effect_4"), (14, "effect_5"), (15, "effect_6"), (16, "effect_7"), (17, "effect_8"), (18, "effect_9"),
            (19, "Bip01_Hit"), (20, "attach_form"), (21, "attach_launch"), (22, "spine1"), (23, "spine2"), (24, "spine3"),
            (25, "mainhand_effect1"), (26, "mainhand_effect2"), (27, "offhand_effect1"), (28, "offhand_effect2"),
            (29, "item_side_l"), (30, "item_side_r"), (31, "item_back_c"), (32, "weapon_effect1"), (33, "weapon_effect2")),

        ["enum_abilities"] = Names((0, "general"), (1, "fight"), (2, "illusion"), (3, "adamant"), (4, "will"), (5, "death"),
            (6, "wild"), (7, "magic"), (8, "vocation"), (9, "romance"), (10, "love"), (11, "hatred"), (12, "assassin"),
            (13, "madness"), (14, "pleasure"), (15, "space4"), (16, "space5"), (17, "space6"), (18, "space7"), (19, "space8"),
            (20, "space9"), (21, "space10"), (22, "space11"), (23, "space12"), (24, "space13"), (25, "space14"), (26, "space15"),
            (27, "space16"), (28, "predator"), (29, "trooper")),

        ["enum_unit_appellation_routes"] = Names((1, "quest_contexts"), (2, "achievements"), (3, "merchant_packs"), (4, "hidden"), (5, "etc")),

        // the two kinds the particle preview follows
        ["enum_skill_effect_special_type"] = Names((33, "skill"), (35, "fx_group")),

        // key binding option rows are identified by these names
        ["enum_hotkey_actions"] = Names((0, "moveforward"), (1, "moveback"), (2, "moveleft"), (3, "moveright"), (4, "turnleft"), (5, "turnright"), (6,
            "jump"), (7, "down"), (8, "rotateyaw"), (9, "rotatepitch"), (10, "zoom_in"), (11, "zoom_out"), (12,
            "open_chat"), (13, "open_config"), (14, "toggle_bag"), (15, "toggle_spellbook"), (16, "toggle_character"),
            (17, "toggle_quest"), (18, "toggle_craft_book"), (20, "toggle_common_farm_info"), (21, "autorun"), (22,
            "cycle_hostile_forward"), (23, "cycle_hostile_backward"), (24, "cycle_friendly_forward"), (25,
            "cycle_friendly_backward"), (26, "screenshotmode"), (27, "screenshotcamera"), (28, "toggle_nametag"), (29,
            "activate_weapon"), (30, "toggle_worldmap"), (31, "do_interaction_1"), (32, "do_interaction_2"), (33,
            "do_interaction_3"), (34, "do_interaction_4"), (35, "toggle_faction"), (36, "toggle_post"), (37,
            "toggle_walk"), (38, "toggle_pet_manage"), (39, "toggle_force_attack"), (40, "self_target"), (41,
            "round_target"), (42, "team_target"), (43, "over_head_marker"), (44, "pet_target"), (45,
            "toggle_web_messenger"), (46, "toggle_web_wiki"), (47, "toggle_web_play_diary"), (48,
            "toggle_web_play_diary_instant"), (49, "front_camera"), (50, "left_camera"), (51, "right_camera"), (52,
            "back_camera"), (53, "screenshot_zoom_in"), (54, "screenshot_zoom_out"), (55, "dof_toggle"), (56,
            "dof_auto_focus"), (57, "dof_add_dist"), (58, "dof_sub_dist"), (59, "dof_add_range"), (60, "dof_sub_range"),
            (61, "dof_bokeh_toggle"), (62, "dof_bokeh_circle"), (63, "dof_bokeh_hexagon"), (64, "dof_bokeh_heart"), (65,
            "dof_bokeh_star"), (66, "dof_bokeh_add_size"), (67, "dof_bokeh_sub_size"), (68, "dof_bokeh_add_intensity"),
            (69, "dof_bokeh_sub_intensity"), (70, "cycle_camera_clockwise"), (71, "cycle_camera_counter_clockwise"), (72,
            "quick_interaction"), (73, "toggle_gm_console"), (74, "toggle_raid_frame"), (75, "toggle_raid_team_manager"),
            (77, "toggle_community"), (78, "reply_last_whisper"), (79, "reply_last_whispered"), (80,
            "toggle_show_guide_decal"), (82, "action_bar_button"), (83, "toggle_commercial_mail"), (84,
            "pet_action_bar_button"), (85, "mode_action_bar_button"), (86, "action_bar_page_prev"), (87,
            "action_bar_page_next"), (88, "action_bar_page"), (89, "set_watch_target"), (90,
            "builder_rotate_left_normal"), (91, "builder_rotate_right_normal"), (92, "builder_rotate_left_small"), (93,
            "builder_rotate_right_small"), (94, "builder_rotate_left_large"), (95, "builder_rotate_right_large"), (96,
            "builder_zoom_in"), (97, "builder_zoom_out"), (98, "toggle_ingameshop"), (100, "toggle_ranking"), (101,
            "toggle_achievement"), (102, "quest_directing_interaction"), (103, "godmode"), (104, "flymode"), (105,
            "slash_open_chat"), (106, "toggle_auction"), (107, "toggle_mail"), (108, "toggle_battle_field"), (109,
            "open_target_equipment"), (110, "toggle_specialty_info"), (111, "change_roadmap_size"), (112, "toggle_hero"),
            (114, "toggle_megaphone_chat"), (116, "cycle_hostile_head_marker_forward"), (117,
            "cycle_hostile_head_marker_backward"), (118, "toggle_chronicle_book"), (119, "toggle_butler_info"), (120,
            "toggle_random_shop"), (121, "toggle_community_expedition_tab"), (122, "toggle_community_family_tab"), (123,
            "toggle_community_faction_tab"), (124, "swap_preliminary_equipment"), (125, "toggle_optimization")),

        // map filter: the key and the ui text id (M_<NAME>) of every map icon kind
        ["enum_map_symbol_types"] = Names((0, "quest_talk_or_employment"), (1, "quest_main_gives"), (2, "quest_zone_gives"), (3, "quest_repeat"), (4,
            "quest_main_completes"), (5, "quest_main_progress"), (6, "quest_zone_completes"), (7, "quest_zone_progress"),
            (8, "quest_livelihood_gives"), (9, "quest_livelihood_complete"), (10, "quest_hunt_gives"), (11,
            "quest_notifier"), (12, "party"), (13, "raidteam"), (14, "offline_party"), (15, "raidteam_owner"), (16,
            "over_head_mark1"), (17, "over_head_mark2"), (18, "over_head_mark3"), (19, "over_head_mark4"), (20,
            "over_head_mark5"), (21, "over_head_mark6"), (22, "over_head_mark7"), (23, "over_head_mark8"), (24,
            "over_head_mark9"), (25, "over_head_mark10"), (26, "over_head_mark11"), (27, "over_head_mark12"), (28,
            "quest_letit"), (29, "quest_over"), (30, "npc_station"), (31, "npc_graveyard"), (32, "npc_ability_changer"),
            (33, "npc_stabler"), (34, "friendly"), (35, "hostile"), (36, "npc_store_potion"), (37, "npc_store_clothes"),
            (38, "npc_store_food"), (39, "npc_store_weapon"), (40, "npc_store_defense"), (41, "npc_store_tree"), (42,
            "npc_store_plants"), (43, "npc_store_material"), (44, "npc_store_goods"), (45, "npc_store_enlisting"), (46,
            "npc_store_ship"), (47, "npc_store_siege_weapon"), (48, "npc_store_building"), (49, "npc_store_furniture"),
            (50, "npc_store_livestock"), (51, "npc_store_bank"), (52, "npc_store_auction_house"), (53,
            "npc_store_expedition"), (54, "npc_store_pirate_expedition"), (55, "npc_store_territory"), (56,
            "npc_store_trade"), (57, "npc_store_delphinad"), (58, "npc_store_roamer"), (59, "npc_store_blacksmith"), (60,
            "npc_store_gladiator"), (61, "npc_instance_target_corps1"), (62, "npc_instance_target_corps2"), (63,
            "housing_house"), (64, "housing_farm"), (65, "housing_fishfarm"), (66, "my_crops"), (67, "corpse_pos"), (68,
            "my_ping"), (69, "portal"), (70, "ship"), (71, "shipyard"), (72, "ship_enemy"), (73, "shipyard_enemy"), (74,
            "common_farm"), (75, "transfer_airship"), (76, "transfer_carriage"), (77, "transfer_landship"), (78,
            "force_released_ship"), (79, "force_released_vehicle"), (80, "fish_school"), (81, "doodad_mail"), (82,
            "doodad_inn"), (83, "doodad_leather"), (84, "doodad_craft"), (85, "doodad_metal"), (86, "doodad_machinery"),
            (87, "doodad_timber"), (88, "doodad_woodwork"), (89, "doodad_weapon"), (90, "doodad_stone"), (91,
            "doodad_archium"), (92, "doodad_alchemy"), (93, "doodad_cook"), (94, "doodad_coatofarms"), (95,
            "doodad_fabric"), (96, "doodad_special_product"), (97, "doodad_light_armor"), (98, "doodad_leather_armor"),
            (99, "doodad_heavy_armor"), (100, "doodad_paper"), (101, "doodad_print"), (102, "doodad_fish_stand"), (103,
            "doodad_art_work"), (104, "doodad_portal"), (105, "doodad_portal_archemall"), (106, "doodad_portal_dungeon"),
            (107, "house_normal_house"), (108, "house_sea_farm_house"), (109, "house_high_house"), (110,
            "house_pumpkin_house"), (111, "house_farm_house"), (112, "house_benny_house"), (113, "house_bungalow_house"),
            (114, "my_slave"), (115, "player"), (116, "unused_116"), (117, "trade_route"), (118, "light_house"), (119,
            "npc_ocean_trader"), (120, "boss"), (121, "doodad_hammer"), (122, "faction_hq"), (123,
            "doodad_mate_equipment"), (124, "doodad_slave_equipment"), (125, "ping_enemy"), (126, "ping_attack"), (127,
            "ping_line"), (128, "doodad_raid_purity"), (129, "resident_hall"), (130, "housing_family_house"), (131,
            "housing_family_farm"), (132, "housing_family_fish_farm"), (133, "housing_exped_house"), (134,
            "housing_exped_farm"), (135, "housing_exped_fish_farm"), (137, "territory_a"), (138, "territory_b"), (139,
            "territory_c"), (140, "npc_specialty_goods_trader"), (141, "npc_specialty_tradegoods_trader"), (142,
            "npc_specialty_tradegoods_seller"), (143, "npc_specialty_tradegoods_buyer"), (144, "transfer_cruiser"), (145,
            "sea_gimic"), (146, "joint_raid_team"), (147, "joint_raid_leader"), (148, "joint_raid_officer"), (149,
            "npc_store_honor_point_collector"), (150, "doodad_craft_order_board"), (151, "doodad_guard_tower"), (152,
            "quest_saga_gives"), (153, "quest_saga_completes"), (154, "faction_entry_restriction"), (155,
            "special_rez_district_friendly"), (156, "special_rez_district_hostile"), (157,
            "special_rez_district_neutral"), (158, "housing_exped_own")),

        // not derivable without the full database (1244 names, and the ids do not line up with the ui_texts rows): server error
        // names. A server error then shows no system message.
        ["enum_error_messages"] = Names(),
    };
}

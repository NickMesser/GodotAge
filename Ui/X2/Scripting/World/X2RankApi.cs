#nullable enable
using AAEmu.GodotViewer.Lua;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.World;

/// <summary>
/// Installs the ranking metadata API used by x2ui.  Ranking tabs and board descriptions are client
/// content, so they remain available without a world connection; snapshots are empty until a live
/// protocol adapter supplies them.
/// </summary>
public static class X2RankApi
{
    private sealed record RankRow(int Id, int Kind, string Name, string Guide, string Tab,
        int Order, string Icon, bool Queriable, bool RatingOnly, int DetailKind, int DetailMethod,
        int ResetInterval, int ResetDay);
    private sealed record RewardRow(int RankId, int Begin, int End, int ItemId, int Count,
        int ItemGrade, int CurrencyId, int CurrencyAmount, int Grade);

    public static void Install(X2LuaHost host, string? database = null, X2RankRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var rows = Load(database ?? X2DbLists.DefaultDatabase);
        var rewards = LoadRewards(database ?? X2DbLists.DefaultDatabase);

        host.Define("X2Rank", "GetRankTabCodes", _ => Tabs(rows));
        host.Define("X2Rank", "BuildRankTabInfo", a => RankInfos(rows, a.Str(0) ?? ""));
        host.Define("X2Rank", "GetAllRanks", _ => RankInfos(rows, null));
        host.Define("X2Rank", "GetRankKind", a => (double)(Find(rows, a.Int(0))?.Kind ?? 0));
        host.Define("X2Rank", "GetRankDivisions", _ => OneDivision());
        host.Define("X2Rank", "GetRankingRewardDivisions", _ => OneDivision());
        host.Define("X2Rank", "GetRankRewardDivisions", a => RewardDivisions(rewards, a.Int(0)));
        host.Define("X2Rank", "GetRankRewards", a => RewardRows(rows, rewards, a.Int(0)));
        host.Define("X2Rank", "HasRankReward", a => rewards.Any(x => x.RankId == a.Int(0)));
        host.Define("X2Rank", "GetSnapshot", a => runtime?.Snapshot(a.Int(0), a.Int(1), false) ?? new LuaTable());
        host.Define("X2Rank", "GetRewardSnapshot", a => runtime?.Snapshot(a.Int(0), a.Int(1), true) ?? new LuaTable());
        host.Define("X2Rank", "GetPersonalData", a => runtime?.Personal(a.Int(0)) ?? EmptyPersonal());
        host.Define("X2Rank", "GetMetaInfo", a => Meta(rows, a.Int(0)));
        host.Define("X2Rank", "GetRankSeasonInformation", _ => new LuaTable { ["isAlways"] = true });
        host.Define("X2Rank", "GetRankSeasonOffDate", a => DateTable(SeasonEnd(Find(rows, a.Int(0)), DateTime.UtcNow)));
        host.Define("X2Rank", "IsRankerQueriable", a => Find(rows, a.Int(0))?.Queriable ?? false);
        host.Define("X2Rank", "IsRankRatingOnly", a => Find(rows, a.Int(0))?.RatingOnly ?? false);
        host.Define("X2Rank", "GetGamePointDetail", a =>
        {
            var row = Find(rows, a.Int(0));
            return new LuaMulti((double)(row?.DetailKind ?? 0), (double)(row?.DetailMethod ?? 0));
        });

        // The offline data source intentionally answers requests with the already installed empty state.
        // A live adapter can override these methods while retaining the DB-backed metadata above.
        host.Define("X2Rank", "RequestPersonalData", a => { runtime?.RequestPersonal(); return null; });
        host.Define("X2Rank", "RequestSnapshot", a => { runtime?.RequestSnapshot(a.Int(0), a.Int(1), false); return null; });
        host.Define("X2Rank", "RequestRewardSnapshot", a => { runtime?.RequestSnapshot(a.Int(0), a.Int(1), true); return null; });
        foreach (var method in new[] { "RequestRankData", "RequestItemRank", "RequestPlayerRecords" })
            host.Define("X2Rank", method, _ => null);
        host.Define("X2Rank", "GetRankerInformation", _ => new LuaTable());
        host.Define("X2Rank", "BuildRankingTabInfo", _ => new LuaTable());
    }

    private static LuaTable Tabs(IReadOnlyList<RankRow> rows)
    {
        var result = new LuaTable();
        var index = 1;
        foreach (var group in rows.GroupBy(x => x.Tab).OrderBy(x => x.Min(r => r.Order)))
        {
            var first = group.OrderBy(x => x.Order).First();
            result[(double)index++] = new LuaTable
            {
                ["tabCode"] = first.Tab,
                ["tabIconKey"] = first.Icon,
            };
        }
        return result;
    }

    private static LuaTable RankInfos(IReadOnlyList<RankRow> rows, string? tab)
    {
        var result = new LuaTable();
        var selected = string.IsNullOrEmpty(tab) ? rows : rows.Where(x => x.Tab == tab);
        var index = 1;
        foreach (var row in selected.OrderBy(x => x.Order))
        {
            result[(double)index++] = new LuaTable
            {
                ["rankType"] = (double)row.Id,
                ["rankKind"] = (double)row.Kind,
                ["rankName"] = row.Name,
                ["guideDesc"] = row.Guide,
                ["tabCode"] = row.Tab,
                ["tabIconKey"] = row.Icon,
            };
        }
        return result;
    }

    private static LuaTable OneDivision() => Array(new LuaTable
    {
        ["worldId"] = 0d,
        ["worldName"] = "All Worlds",
        ["name"] = "All Worlds",
    });

    private static LuaTable RewardDivisions(IReadOnlyList<RewardRow> rewards, int rankId) =>
        rewards.Any(x => x.RankId == rankId) ? Array(new LuaTable { ["division"] = 0d }) : new LuaTable();

    private static LuaTable RewardRows(IReadOnlyList<RankRow> ranks, IReadOnlyList<RewardRow> rewards, int rankId)
    {
        var result = new LuaTable();
        var rankKind = Find(ranks, rankId)?.Kind ?? 0;
        var index = 1;
        foreach (var reward in rewards.Where(x => x.RankId == rankId).OrderBy(x => x.Begin))
            result[(double)index++] = new LuaTable
            {
                ["grade"] = (double)reward.Grade, ["_begin"] = (double)reward.Begin,
                ["_end"] = (double)reward.End, ["rewardItem"] = (double)reward.ItemId,
                ["count"] = (double)reward.Count, ["itemGrade"] = (double)reward.ItemGrade,
                ["rewardCurrency"] = (double)reward.CurrencyId,
                ["rewardCurrencyAmount"] = (double)reward.CurrencyAmount,
                ["rankKind"] = (double)rankKind,
            };
        return result;
    }

    private static LuaTable EmptyPersonal() => new()
    {
        ["v1"] = 0d,
        ["v2"] = 0d,
        ["ranking"] = 0d,
        ["expBonusPercent"] = 0d,
    };

    private static LuaTable Meta(IReadOnlyList<RankRow> rows, int id)
    {
        var row = Find(rows, id);
        return new LuaTable
        {
            ["rankName"] = row?.Name ?? "",
            ["guideDesc"] = row?.Guide ?? "ranking_tip",
        };
    }

    private static RankRow? Find(IReadOnlyList<RankRow> rows, int id) => rows.FirstOrDefault(x => x.Id == id);

    private static List<RankRow> Load(string database)
    {
        try
        {
            if (!File.Exists(database)) return Fallback();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                select r.id, r.rank_kind_id,
                       coalesce((select l.en_us from localized_texts l
                                  where l.tbl_name='ranks' and l.tbl_column_name='name' and l.idx=r.id), r.name),
                       r.guide_desc, r.tab_name, r.display_order,
                       r.tab_icon_key, r.ranker_queriable,
                       coalesce(i.rating_only, 'f'), coalesce(g.game_point_kind, 0),
                       coalesce(g.game_point_method, 0), coalesce(rs.reset_interval_id, 0),
                       coalesce(rs.day_of_week_id, 8)
                  from ranks r
                  left join instance_rank_details i on i.id = r.rank_detail_id
                  left join game_point_rank_details g on g.id = r.rank_detail_id
                  left join rank_resets rs on rs.id = r.rank_reset_id
                 where r.tab_display = 't'
                 order by r.display_order, r.id
                """;
            using var reader = cmd.ExecuteReader();
            var rows = new List<RankRow>();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                rows.Add(new RankRow(id, reader.GetInt32(1),
                    UiTranslator.Shared.Translate(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                    reader.GetInt32(5), reader.GetString(6), True(reader.GetValue(7)), True(reader.GetValue(8)),
                    reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12)));
            }
            return rows.Count == 0 ? Fallback() : rows;
        }
        catch (Exception)
        {
            return Fallback();
        }
    }

    private static List<RewardRow> LoadRewards(string database)
    {
        try
        {
            if (!File.Exists(database)) return [];
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            // Global tiers are usable offline. Local tiers describe a particular live world and are duplicates
            // of the same bands in this data set, so the live world selector can add those later if needed.
            cmd.CommandText = """
                select rank_id, scope_from, scope_to, coalesce(reward_item_id,0), reward_item_count,
                       reward_item_grade_id, coalesce(currency_id,0), coalesce(currency_amount,0), id
                  from rank_tiers
                 where is_local='f' and (coalesce(reward_item_id,0)>0 or coalesce(currency_amount,0)>0)
                 order by rank_id, scope_from, id
                """;
            using var reader = cmd.ExecuteReader();
            var raw = new List<(int Rank, int Begin, int End, int Item, int Count, int ItemGrade,
                int Currency, int CurrencyAmount)>();
            while (reader.Read()) raw.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7)));
            return raw.GroupBy(x => x.Rank).SelectMany(group => group.Select((x, index) =>
                new RewardRow(x.Rank, x.Begin, x.End, x.Item, x.Count, x.ItemGrade, x.Currency,
                    x.CurrencyAmount, index + 1))).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool True(object value) => value switch
    {
        bool b => b,
        string s => s.Equals("t", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase),
        long n => n != 0,
        _ => false,
    };

    private static List<RankRow> Fallback() =>
    [
        new(23, 9, "Equipment Gear Score", "ranking_tip_gear_score", "rank_tab_achievement", 1,
            "grades", true, false, 0, 0, 0, 8),
    ];

    private static LuaTable Array(params object?[] values)
    {
        var table = new LuaTable();
        for (var i = 0; i < values.Length; i++) table[(double)(i + 1)] = values[i];
        return table;
    }

    private static LuaTable DateTable(DateTime utc) => new()
    {
        ["year"] = (double)utc.Year, ["month"] = (double)utc.Month, ["day"] = (double)utc.Day,
        ["hour"] = (double)utc.Hour, ["minute"] = (double)utc.Minute, ["second"] = (double)utc.Second,
    };

    private static DateTime SeasonEnd(RankRow? row, DateTime utc)
    {
        if (row is null || row.ResetInterval == 0) return utc;
        if (row.ResetInterval != 1)
            return new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        var start = utc.Date;
        var wanted = (DayOfWeek)(((row.ResetDay % 7) + 7) % 7);
        while (start.DayOfWeek != wanted) start = start.AddDays(-1);
        return start.AddDays(7);
    }
}

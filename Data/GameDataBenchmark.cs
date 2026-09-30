using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Data;

public sealed record GameDataBenchmarkResult(int SamplesPerType, TimeSpan ItemsCold, TimeSpan ItemsWarm,
    TimeSpan SkillsCold, TimeSpan SkillsWarm);

/// <summary>Small repeatable lookup benchmark. “Cold” means an empty GameData object cache.</summary>
public static class GameDataBenchmark
{
    public static GameDataBenchmarkResult Run(string databasePath, int samplesPerType = 10_000, int seed = 1337)
    {
        if (samplesPerType <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerType));
        var itemIds = SampleIds(databasePath, "items", samplesPerType, seed);
        var skillIds = SampleIds(databasePath, "skills", samplesPerType, seed + 1);

        using var data = new GameData(databasePath);
        var itemsCold = Time(() => { foreach (var id in itemIds) _ = data.GetItem(id); });
        var skillsCold = Time(() => { foreach (var id in skillIds) _ = data.GetSkill(id); });
        var itemsWarm = Time(() => { foreach (var id in itemIds) _ = data.GetItem(id); });
        var skillsWarm = Time(() => { foreach (var id in skillIds) _ = data.GetSkill(id); });
        return new GameDataBenchmarkResult(samplesPerType, itemsCold, itemsWarm, skillsCold, skillsWarm);
    }

    private static long[] SampleIds(string databasePath, string table, int count, int seed)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadOnly };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM {table} ORDER BY id";
        var all = new List<long>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) all.Add(reader.GetInt64(0));
        var random = new Random(seed);
        for (var i = all.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (all[i], all[j]) = (all[j], all[i]);
        }
        return all.Take(Math.Min(count, all.Count)).ToArray();
    }

    private static TimeSpan Time(Action action)
    {
        var timer = Stopwatch.StartNew();
        action();
        timer.Stop();
        return timer.Elapsed;
    }
}

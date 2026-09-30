#nullable enable

using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Client;

/// <summary>First enabled speech row for each component, from the configured read-only quest_chat_bubbles table.</summary>
public sealed class QuestChatBubbleCatalog : IDisposable
{
    public sealed record Entry(uint Id, uint ComponentId, uint NpcTemplateId, uint NextBubbleId);

    private readonly Dictionary<uint, Entry> _startByComponent = [];
    private bool _disposed;

    public QuestChatBubbleCatalog(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT id, quest_component_id, npc_id, next_bubble
FROM quest_chat_bubbles
WHERE lower(cast(is_start as text)) IN ('t','true','1')
  AND lower(cast(enable as text)) IN ('t','true','1')
  AND npc_id>0 AND COALESCE(npc_group_id,0)=0 AND COALESCE(npc_spawner_id,0)=0
ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Entry(checked((uint)reader.GetInt64(0)), checked((uint)reader.GetInt64(1)),
                checked((uint)reader.GetInt64(2)), reader.IsDBNull(3) ? 0u : checked((uint)reader.GetInt64(3)));
            _startByComponent.TryAdd(row.ComponentId, row);
        }
    }

    public bool TryGetStart(uint componentId, out Entry entry)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(QuestChatBubbleCatalog));
        return _startByComponent.TryGetValue(componentId, out entry!);
    }

    public void Dispose() => _disposed = true;
}

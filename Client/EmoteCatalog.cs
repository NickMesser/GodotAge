#nullable enable

using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Client;

/// <summary>Read-only express_texts/slash_commands lookup built from the configured game-content database.</summary>
public sealed class EmoteCatalog : IDisposable
{
    public sealed record Entry(uint Id, int AnimationId, int NpcAnimationId);

    private readonly SqliteConnection _connection;
    private readonly Dictionary<uint, Entry> _entries = [];
    private readonly Dictionary<string, uint> _commands = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public EmoteCatalog(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, anim_id, npc_anim_id FROM express_texts";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = checked((uint)reader.GetInt64(0));
                _entries[id] = new Entry(id, reader.GetInt32(1), reader.GetInt32(2));
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = @"SELECT action_id, command_list
FROM slash_commands
WHERE action_type='ExpressText' AND action_id IS NOT NULL AND command_list IS NOT NULL
ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = checked((uint)reader.GetInt64(0));
                if (!_entries.ContainsKey(id)) continue;
                foreach (var alias in reader.GetString(1).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    _commands.TryAdd(alias, id);
            }
        }
    }

    public bool TryGet(uint id, out Entry entry)
    {
        ThrowIfDisposed();
        return _entries.TryGetValue(id, out entry!);
    }

    /// <summary>Resolves an original slash alias such as <c>/bow</c> or <c>/emote bow</c>.</summary>
    public bool TryResolveCommand(string? command, out uint emotionId)
    {
        ThrowIfDisposed();
        emotionId = 0;
        if (string.IsNullOrWhiteSpace(command)) return false;
        var token = command.Trim();
        if (token.StartsWith("/emote ", StringComparison.OrdinalIgnoreCase)) token = token[7..].TrimStart();
        if (token.StartsWith('/')) token = token[1..];
        var end = token.IndexOfAny([' ', '\t', '\r', '\n']);
        if (end >= 0) token = token[..end];
        return token.Length > 0 && _commands.TryGetValue(token, out emotionId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EmoteCatalog));
    }
}

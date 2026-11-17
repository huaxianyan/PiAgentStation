using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace MAStation.Storage;

public sealed record Conversation(
    string Id,
    string Title,
    string Cwd,
    string? PiSessionFile,
    long CreatedAt,
    long UpdatedAt,
    int MessageCount,
    string? Stats,
    string? Model,
    string? ThinkingLevel);

/// <summary>
/// MAStation 自己的对话存储。客户端只读这里，不去碰 Pi 的会话目录。
/// Pi 那边的会话文件只在这个类里当绑定字段用：续聊时传给 Pi，界面看不到。
/// </summary>
public sealed class ConversationStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public ConversationStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");
        Execute("""
            CREATE TABLE IF NOT EXISTS conversations (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL DEFAULT '',
                cwd TEXT NOT NULL,
                pi_session_file TEXT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                message_count INTEGER NOT NULL DEFAULT 0,
                stats TEXT NULL,
                model TEXT NULL,
                thinking_level TEXT NULL
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS messages (
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                seq INTEGER NOT NULL,
                payload TEXT NOT NULL,
                PRIMARY KEY (conversation_id, seq)
            );
            """);
        Execute(@"
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            ");
        Execute("CREATE INDEX IF NOT EXISTS ix_conversations_updated ON conversations(updated_at DESC);");
    }

    public string DatabasePath => _connection.DataSource;

    public Conversation Create(string cwd, string? title = null)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var id = "c-" + Guid.NewGuid().ToString("N")[..12];
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO conversations (id, title, cwd, created_at, updated_at)
                VALUES ($id, $title, $cwd, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title ?? string.Empty);
            command.Parameters.AddWithValue("$cwd", cwd);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        return Get(id)!;
    }

    public IReadOnlyList<Conversation> List()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, cwd, pi_session_file, created_at, updated_at, message_count, stats, model, thinking_level
                FROM conversations ORDER BY updated_at DESC;
                """;
            using var reader = command.ExecuteReader();
            var items = new List<Conversation>();
            while (reader.Read())
            {
                items.Add(Read(reader));
            }

            return items;
        }
    }

    public Conversation? Get(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, cwd, pi_session_file, created_at, updated_at, message_count, stats, model, thinking_level
                FROM conversations WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    public JsonArray Messages(string id)
    {
        var messages = new JsonArray();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM messages WHERE conversation_id = $id ORDER BY seq;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (JsonNode.Parse(reader.GetString(0)) is { } node)
                {
                    messages.Add(node);
                }
            }
        }

        return messages;
    }

    /// <summary>一轮结束时整体替换消息，顺带更新统计、模型与会话绑定。</summary>
    public void SaveTurn(
        string id,
        JsonArray? messages,
        JsonObject? stats,
        JsonObject? state,
        string? title = null)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            if (messages is not null)
            {
                using (var clear = _connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM messages WHERE conversation_id = $id;";
                    clear.Parameters.AddWithValue("$id", id);
                    clear.ExecuteNonQuery();
                }

                for (var index = 0; index < messages.Count; index++)
                {
                    if (messages[index] is null)
                    {
                        continue;
                    }

                    using var insert = _connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO messages (conversation_id, seq, payload) VALUES ($id, $seq, $payload);
                        """;
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$seq", index);
                    insert.Parameters.AddWithValue("$payload", messages[index]!.ToJsonString());
                    insert.ExecuteNonQuery();
                }
            }

            var setParts = new List<string> { "updated_at = $now" };
            if (messages is not null)
            {
                setParts.Add("message_count = $count");
            }

            if (stats is not null)
            {
                setParts.Add("stats = $stats");
            }

            if (state is not null)
            {
                setParts.Add("model = $model");
                setParts.Add("thinking_level = $thinking");
                var file = state["sessionFile"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(file))
                {
                    setParts.Add("pi_session_file = $file");
                }
            }

            if (title is not null)
            {
                setParts.Add("title = $title");
            }

            using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = $"UPDATE conversations SET {string.Join(", ", setParts)} WHERE id = $id;";
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$count", messages?.Count ?? 0);
                update.Parameters.AddWithValue("$stats", stats?.ToJsonString() ?? (object)DBNull.Value);
                update.Parameters.AddWithValue("$model", state?["model"]?.ToJsonString() ?? (object)DBNull.Value);
                update.Parameters.AddWithValue(
                    "$thinking",
                    state?["thinkingLevel"]?.GetValue<string>() ?? (object)DBNull.Value);
                update.Parameters.AddWithValue(
                    "$file",
                    state?["sessionFile"]?.GetValue<string>() ?? (object)DBNull.Value);
                update.Parameters.AddWithValue("$title", title ?? string.Empty);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void Rename(string id, string title)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE conversations SET title = $title, updated_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM conversations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    private static Conversation Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetInt64(4),
        reader.GetInt64(5),
        reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}

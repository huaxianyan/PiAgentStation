using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SevenStation.Core.Storage;

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
/// SevenStation.Core 自己的对话存储。客户端只读这里，不去碰 Pi 的会话目录。
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
                text TEXT NULL,
                PRIMARY KEY (conversation_id, seq)
            );
            """);

        EnsureMessageTextColumn();
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

    /// <summary>
    /// 标题与正文一起搜。正文命中时给出命中位置附近的一小段，界面拿来当摘要。
    /// </summary>
    public IReadOnlyList<(Conversation Conversation, string? Snippet)> Search(string keyword, int limit = 40)
    {
        var needle = keyword.Trim();
        if (needle.Length == 0)
        {
            return Array.Empty<(Conversation, string?)>();
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT c.id, c.title, c.cwd, c.pi_session_file, c.created_at, c.updated_at, c.message_count,
                       c.stats, c.model, c.thinking_level,
                       (SELECT m.text FROM messages m
                          WHERE m.conversation_id = c.id AND m.text LIKE $like
                          ORDER BY m.seq DESC LIMIT 1) AS hit
                FROM conversations c
                WHERE c.title LIKE $like
                   OR EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = c.id AND m.text LIKE $like)
                ORDER BY c.updated_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$like", "%" + needle + "%");
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            var items = new List<(Conversation, string?)>();
            while (reader.Read())
            {
                var conversation = Read(reader);
                var hit = reader.IsDBNull(10) ? null : reader.GetString(10);
                items.Add((conversation, Snippet(hit, needle)));
            }

            return items;
        }
    }

    /// <summary>消息是按原始 JSON 存的，搜出来要还原成人读的文字再截。</summary>
    private static string PayloadText(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return string.Empty;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("content", out var content))
            {
                return payload;
            }

            if (content.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (content.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return payload;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    builder.Append(part.GetString());
                    continue;
                }

                if (part.TryGetProperty("text", out var text) &&
                    text.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    builder.Append(text.GetString());
                }
            }

            return builder.ToString();
        }
        catch
        {
            return payload;
        }
    }

    private static string? Snippet(string? content, string needle)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        var index = content.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = Math.Max(0, index - 30);
        var length = Math.Min(content.Length - start, needle.Length + 80);
        var text = content.Substring(start, length).ReplaceLineEndings(" ");
        return (start > 0 ? "…" : string.Empty) + text + (start + length < content.Length ? "…" : string.Empty);
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
                        INSERT INTO messages (conversation_id, seq, payload, text)
                        VALUES ($id, $seq, $payload, $text);
                        """;
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$seq", index);
                    insert.Parameters.AddWithValue("$payload", messages[index]!.ToJsonString());
                    insert.Parameters.AddWithValue("$text", PayloadText(messages[index]!.ToJsonString()));
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

    /// <summary>
    /// payload 里的中文按 \uXXXX 转义，直接 LIKE 搜不到；另存一份解好的正文，
    /// 顺便让搜索不必每次解析 JSON。老库在启动时补这一列并回填。
    /// </summary>
    private void EnsureMessageTextColumn()
    {
        var hasColumn = false;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(messages);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "text", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                }
            }
        }

        if (!hasColumn)
        {
            Execute("ALTER TABLE messages ADD COLUMN text TEXT NULL;");
        }

        var pending = new List<(string Id, long Seq, string Text)>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT conversation_id, seq, payload FROM messages WHERE text IS NULL;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                pending.Add((reader.GetString(0), reader.GetInt64(1), PayloadText(reader.GetString(2))));
            }
        }

        foreach (var (id, seq, text) in pending)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE messages SET text = $text WHERE conversation_id = $id AND seq = $seq;";
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$seq", seq);
            command.ExecuteNonQuery();
        }
    }

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

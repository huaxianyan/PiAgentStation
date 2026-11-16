using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace PiAgentStation.Sessions;

public sealed record SessionSummary(
    string Path,
    string SessionId,
    string Cwd,
    string? Name,
    string? Title,
    DateTimeOffset UpdatedAt,
    long SizeBytes);

/// <summary>
/// 直接读磁盘上的会话文件。Pi 的会话是只追加 JSONL，TUI 与这个应用读写同一份，
/// 所以列表来自文件系统、不经过子进程。
/// 目录结构：~/.pi/agent/sessions/--&lt;编码后的工作目录&gt;--/&lt;时间戳&gt;_&lt;ID&gt;.jsonl
/// </summary>
public static class SessionStore
{
    private const int MaxScanLines = 120;
    private const int MaxScanBytes = 128 * 1024;

    // 文件没变就不重复解析；每轮结束刷新列表时不至于把几百个会话全量重读。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, DateTime Stamp, SessionSummary Value)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string RootDirectory
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }

            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent", "sessions");
        }
    }

    public static IReadOnlyList<SessionSummary> List(int limit = 300)
    {
        var root = RootDirectory;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var found = new List<SessionSummary>();

        // 默认布局是 root/<按工作目录编码的目录>/<会话>.jsonl，
        // 但用 --session-dir 或 PI_CODING_AGENT_SESSION_DIR 覆盖时，文件直接落在 root 下。
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl"))
        {
            AddIfReadable(file, found);
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                AddIfReadable(file, found);
            }
        }

        return found
            .OrderByDescending(s => s.UpdatedAt)
            .Take(limit)
            .ToList();
    }

    private static void AddIfReadable(string file, List<SessionSummary> found)
    {
        var summary = TryRead(file);
        if (summary is not null)
        {
            found.Add(summary);
        }
    }

    public static SessionSummary? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (Cache.TryGetValue(path, out var cached) && cached.Size == info.Length && cached.Stamp == info.LastWriteTimeUtc)
            {
                return cached.Value;
            }

            string? cwd = null;
            string? id = null;
            string? name = null;
            string? title = null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var bytes = 0;
            var lines = 0;
            while (lines < MaxScanLines && bytes < MaxScanBytes &&
                   reader.ReadLine() is { } line)
            {
                lines++;
                bytes += line.Length + 1;
                if (line.Length == 0 || !line.Contains('"'))
                {
                    continue;
                }

                JsonObject? entry;
                try
                {
                    entry = JsonNode.Parse(line) as JsonObject;
                }
                catch
                {
                    continue;
                }

                if (entry is null)
                {
                    continue;
                }

                switch (entry["type"]?.GetValue<string>())
                {
                    case "session":
                        cwd ??= entry["cwd"]?.GetValue<string>();
                        id ??= entry["id"]?.GetValue<string>();
                        break;

                    case "session_info":
                        var candidate = entry["name"]?.GetValue<string>();
                        name = string.IsNullOrWhiteSpace(candidate) ? null : candidate;
                        break;

                    case "message" when title is null:
                        var message = entry["message"] as JsonObject;
                        if (message?["role"]?.GetValue<string>() == "user")
                        {
                            title = FirstText(message["content"]);
                        }

                        break;
                }

                if (cwd is not null && title is not null)
                {
                    break;
                }
            }

            var sessionId = id ?? IdFromFileName(System.IO.Path.GetFileName(path));

            // 目录名把盘符冒号和路径分隔符都编码成 '-'，无法反解，所以只信会话头里的 cwd。
            var summary = new SessionSummary(
                path,
                sessionId,
                cwd ?? string.Empty,
                name,
                title ?? name,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Length);
            Cache[path] = (info.Length, info.LastWriteTimeUtc, summary);
            return summary;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstText(JsonNode? content)
    {
        switch (content)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return Shorten(text);
            case JsonArray blocks:
                foreach (var block in blocks)
                {
                    if (block is JsonObject obj &&
                        obj["type"]?.GetValue<string>() == "text" &&
                        obj["text"]?.GetValue<string>() is { } blockText)
                    {
                        return Shorten(blockText);
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var flat = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        return flat.Length <= 100 ? flat : flat[..100] + "…";
    }

    private static string IdFromFileName(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var index = stem.IndexOf('_');
        return index >= 0 && index + 1 < stem.Length ? stem[(index + 1)..] : stem;
    }

}

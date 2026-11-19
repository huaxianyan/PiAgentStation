using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SevenStation.Core.Agents;
using SevenStation.Core.Rpc;

namespace SevenStation.Core.Sessions;

/// <summary>Pi 会话目录里扫出来的一段，够界面列一行、够判断恢复没恢复过。</summary>
public sealed record RestoreCandidate(
    string Id,
    string Cwd,
    string Title,
    long StartedAt,
    int MessageCount,
    long SizeBytes,
    string? Model,
    string Path,
    string LastMessage);

/// <summary>
/// Pi 自己的会话文件（`&lt;agent 目录&gt;\sessions\&lt;工作区&gt;\&lt;时间&gt;_&lt;id&gt;.jsonl`）。
/// 只读不写：恢复是把这段会话收进 SevenStation 的对话库，原来那份文件一直归 Pi 管。
/// </summary>
public static class PiSessionLibrary
{
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private sealed record CacheEntry(long Ticks, long Length, RestoreCandidate? Summary);

    public static string Root => PiLocator.SessionsDirectory();

    /// <summary>扫一遍会话目录。按开始时间倒序，最近聊过的在最上面。</summary>
    public static IReadOnlyList<RestoreCandidate> Scan()
    {
        var root = Root;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var items = new List<RestoreCandidate>();
        string[] files;
        try
        {
            files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        lock (Gate)
        {
            foreach (var file in files)
            {
                var summary = Summarize(file);
                if (summary is not null)
                {
                    items.Add(summary);
                }
            }

            // 目录里被删掉的老文件不必留在缓存里。
            var alive = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in Cache.Keys.Where(key => !alive.Contains(key)).ToList())
            {
                Cache.Remove(stale);
            }
        }

        return items.OrderByDescending(item => item.StartedAt).ToList();
    }

    /// <summary>按会话 id、文件名或完整路径找回那一段。</summary>
    public static RestoreCandidate? Resolve(string idOrPath)
    {
        var text = (idOrPath ?? string.Empty).Trim().Trim('"', '\'');
        if (text.Length == 0)
        {
            return null;
        }

        if (text.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && File.Exists(text))
        {
            return Summarize(text);
        }

        if (text.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Root, text)))
        {
            return Summarize(Path.Combine(Root, text));
        }

        return Scan().FirstOrDefault(item =>
            item.Id.StartsWith(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// magpie 那边给的是一整条命令，形如
    /// <c>Set-Location -LiteralPath 'E:\Dev\Android'; pi --session 01a1ed4e-...</c>。
    /// 取出里面的会话 id 去匹配，工作目录只在会话文件本身缺 cwd 时才用得上。
    /// </summary>
    public static (string? Id, string? Cwd) ParseResumeCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var id = FirstMatch(text, @"--session[=\s]+[""']?([0-9a-fA-F-]{8,})");
        var cwd = FirstMatch(text, @"-LiteralPath\s+[""']([^""']+)[""']")
            ?? FirstMatch(text, @"(?:Set-Location|cd|chdir)\s+[""']([^""']+)[""']")
            ?? FirstMatch(text, @"-Path\s+[""']([^""']+)[""']");

        return (id, cwd);
    }

    /// <summary>
    /// 把会话文件里真正的对话搬出来：只留用户、助手和工具结果。
    /// <paramref name="progress"/> 每读一段回一次（已读字节、总字节、已收条数），
    /// 37 MB 那种大文件要读一两秒，不回报进度界面就一直转圈。
    /// </summary>
    public static JsonArray ReadMessages(string path, Action<long, long, int>? progress = null)
    {
        var messages = new JsonArray();
        long total = 0;
        try
        {
            total = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // 拿不到大小就不报进度，读还是要读。
        }

        long read = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                read += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                if (progress is not null && (messages.Count % 200 == 0))
                {
                    progress(read, total, messages.Count);
                }

                if (line.Length == 0)
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (node?["type"]?.GetValue<string>() != "message")
                {
                    continue;
                }

                var message = node["message"];
                var role = message?["role"]?.GetValue<string>();
                if (role is not ("user" or "assistant" or "toolResult"))
                {
                    continue;
                }

                messages.Add(message!.DeepClone());
            }
        }
        catch (IOException)
        {
            // 读到一半文件被移走：已经读到的照用。
        }

        progress?.Invoke(total, total, messages.Count);
        return messages;
    }

    private static RestoreCandidate? Summarize(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(file, out var entry) && entry.Ticks == info.LastWriteTimeUtc.Ticks && entry.Length == info.Length)
            {
                return entry.Summary;
            }
        }

        RestoreCandidate? summary;
        try
        {
            summary = ReadSummary(file, info);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        lock (Gate)
        {
            Cache[file] = new CacheEntry(info.LastWriteTimeUtc.Ticks, info.Length, summary);
        }

        return summary;
    }

    private static RestoreCandidate? ReadSummary(string file, FileInfo info)
    {
        string id = string.Empty;
        string cwd = string.Empty;
        long startedAt = 0;
        var messageCount = 0;
        string? model = null;
        var title = string.Empty;
        var lastAssistant = string.Empty;

        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var type = node?["type"]?.GetValue<string>();
            if (type == "session")
            {
                id = node?["id"]?.GetValue<string>() ?? string.Empty;
                cwd = node?["cwd"]?.GetValue<string>() ?? string.Empty;
                startedAt = ToUnixMilliseconds(node?["timestamp"]?.GetValue<string>());
                continue;
            }

            if (type == "model_change")
            {
                model ??= string.Join("/", new[]
                {
                    node?["provider"]?.GetValue<string>(),
                    node?["modelId"]?.GetValue<string>(),
                }.Where(part => !string.IsNullOrWhiteSpace(part)));
                continue;
            }

            if (type != "message")
            {
                continue;
            }

            var message = node?["message"];
            var role = message?["role"]?.GetValue<string>();
            if (role == "user")
            {
                messageCount += 1;
                if (title.Length == 0)
                {
                    title = Clip(Flatten(message?["content"]), 90);
                }
            }
            else if (role == "assistant")
            {
                var text = Flatten(message?["content"]);
                if (text.Length > 0)
                {
                    lastAssistant = Clip(text, 400);
                }
            }
        }

        if (id.Length == 0 && startedAt == 0 && messageCount == 0)
        {
            return null;
        }

        if (id.Length == 0)
        {
            // 老文件没有首行元信息时，文件名里的 id 还能用。
            var name = Path.GetFileNameWithoutExtension(file);
            var underscore = name.IndexOf('_');
            id = underscore >= 0 ? name[(underscore + 1)..] : name;
        }

        if (startedAt == 0)
        {
            startedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        }

        if (title.Length == 0)
        {
            title = lastAssistant.Length > 0 ? Clip(lastAssistant, 60) : "（空会话）";
        }

        return new RestoreCandidate(id, cwd, title, startedAt, messageCount, info.Length, model, file, lastAssistant);
    }

    private static string Flatten(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        var parts = new List<string>();
        if (content is JsonArray array)
        {
            foreach (var block in array)
            {
                var piece = block?["text"]?.GetValue<string>() ?? block?["content"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(piece))
                {
                    parts.Add(piece);
                }
            }
        }

        return string.Join(" ", parts);
    }

    private static string Clip(string text, int length)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= length ? flat : flat[..length] + "…";
    }

    private static string? FirstMatch(string text, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static long ToUnixMilliseconds(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return 0;
        }

        return DateTimeOffset.TryParse(iso, out var parsed) ? parsed.ToUnixTimeMilliseconds() : 0;
    }
}

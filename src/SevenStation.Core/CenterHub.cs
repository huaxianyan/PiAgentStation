using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SevenStation.Core.Rpc;
using SevenStation.Core.Agents;
using SevenStation.Core.Storage;
using SevenStation.Shared;

namespace SevenStation.Core;

/// <summary>
/// 对话中心。对话是 SevenStation.Core 自己的概念：存在本地数据库里，客户端只跟这里打交道。
/// 每个对话在需要时对应一个 <c>pi --mode rpc</c> 子进程，Pi 的会话文件只是一条内部绑定，
/// 用来续聊，不暴露给客户端。每轮结束把消息取回来存进数据库，之后看历史不再经过 Pi。
/// </summary>
public sealed class CenterHub
{
    private sealed class Live
    {
        public required Conversation Conversation { get; init; }
        public required PiSession Session { get; init; }
        public bool Streaming { get; set; }

        /// <summary>本轮已经写进库的消息指纹，同一条只落一次。</summary>
        public HashSet<string> Persisted { get; } = new();
    }

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, Live> _live = new();
    private readonly Dictionary<string, TerminalSession> _terminals = new();
    private readonly object _terminalGate = new();
    private readonly List<CenterConnection> _clients = new();
    private readonly ConversationStore _store;

    public CenterHub(ConversationStore store, string listenUrl)
    {
        _store = store;
        ListenUrl = listenUrl;
    }

    /// <summary>中心对外报的地址，界面拿去给人看，方便照着连别的设备。</summary>
    public string ListenUrl { get; }

    public void Join(CenterConnection connection)
    {
        lock (_gate)
        {
            _clients.Add(connection);
        }

        connection.Send(new JsonObject
        {
            ["type"] = "hello",
            ["center"] = "SevenStation",
            ["version"] = typeof(CenterHub).Assembly.GetName().Version?.ToString() ?? "0",
            ["data"] = _store.DatabasePath,
            ["agent"] = AgentRegistry.PrimaryId,
            ["agents"] = AgentPayload(),
            ["listen"] = ListenUrl,
            ["css"] = _store.GetSetting("custom_css"),
            ["workspaces"] = new JsonArray(RecentWorkspaces().Select(entry => (JsonNode?)JsonValue.Create(entry)).ToArray()),
        });
    }

    public void Leave(CenterConnection connection)
    {
        lock (_gate)
        {
            _clients.Remove(connection);
        }
    }

    private void Broadcast(JsonObject payload)
    {
        CenterConnection[] targets;
        lock (_gate)
        {
            targets = _clients.ToArray();
        }

        foreach (var target in targets)
        {
            target.Send(payload);
        }
    }

    public async Task HandleAsync(JsonObject message, CenterConnection sender)
    {
        var type = message["type"]?.GetValue<string>();
        var key = message["key"]?.GetValue<string>();
        try
        {
            switch (type)
            {
                case "list_conversations":
                    SendConversationList(sender);
                    break;

                case "open_conversation":
                    SendState(Require(message["id"]?.GetValue<string>(), "对话标识"), sender);
                    break;

                case "new_conversation":
                    await NewConversationAsync(message, sender);
                    break;

                case "rename_conversation":
                    RenameConversation(Require(message["id"]?.GetValue<string>(), "对话标识"), message, sender);
                    break;

                case "delete_conversation":
                    await DeleteConversationAsync(Require(message["id"]?.GetValue<string>(), "对话标识"));
                    SendConversationList(sender);
                    break;

                case "refresh_state":
                    SendState(Require(key, "对话标识"), sender);
                    break;

                case "refresh_stats":
                    SendStats(Require(key, "对话标识"), sender);
                    break;

                case "prompt":
                    await PromptAsync(Require(key, "对话标识"), message, sender);
                    break;

                case "abort":
                    await SimpleAsync(Require(key, "对话标识"), "abort");
                    break;

                case "compact":
                    await SimpleAsync(Require(key, "对话标识"), "compact");
                    break;

                case "set_model":
                    await SetModelAsync(Require(key, "对话标识"), message, sender);
                    break;

                case "set_thinking":
                    await SetThinkingAsync(Require(key, "对话标识"), message, sender);
                    break;

                case "list_models":
                    await SendModelsAsync(Require(key, "对话标识"), sender);
                    break;

                case "list_commands":
                    await SendCommandsAsync(Require(key, "对话标识"), sender);
                    break;

                case "complete_files":
                    CompleteFiles(Require(key, "对话标识"), message, sender);
                    break;

                case "set_css":
                    SetCustomCss(message["css"]?.GetValue<string>() ?? string.Empty);
                    break;

                case "read_log":
                    ReadLog(message["lines"]?.GetValue<int?>() ?? 120, sender);
                    break;

                case "search":
                    SearchConversations(Require(message["query"]?.GetValue<string>() ?? string.Empty, "关键词"), sender);
                    break;

                case "list_skills":
                    SendSkills(message["cwd"]?.GetValue<string>() ?? string.Empty, sender);
                    break;

                case "list_extensions":
                    _ = SendExtensionsAsync(message["cwd"]?.GetValue<string>() ?? string.Empty, sender);
                    break;

                case "browse_dir":
                    BrowseDirectory(message["path"]?.GetValue<string>() ?? string.Empty, sender);
                    break;

                case "check_path":
                    CheckPath(Require(message["path"]?.GetValue<string>() ?? string.Empty, "路径"), sender);
                    break;

                case "list_dir":
                    ListDirectory(Require(key, "对话标识"), message, sender);
                    break;

                case "read_file":
                    ReadFile(Require(key, "对话标识"), message, sender);
                    break;

                case "terminal_start":
                    StartTerminal(Require(key, "对话标识"), sender);
                    break;

                case "terminal_input":
                    WriteTerminal(Require(key, "对话标识"), message);
                    break;

                case "terminal_stop":
                    StopTerminal(Require(key, "对话标识"), sender);
                    break;

                case "extension_response":
                    RespondToExtension(Require(key, "对话标识"), message);
                    break;

                default:
                    sender.Send(new JsonObject
                    {
                        ["type"] = "error",
                        ["key"] = key,
                        ["message"] = $"未知命令：{type}",
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"命令 {type} 失败（key={key}）", ex);
            sender.Send(new JsonObject
            {
                ["type"] = "error",
                ["key"] = key,
                ["message"] = ex.Message,
            });
        }
    }

    private static string Require(string? value, string what)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"缺少{what}。");
        }

        return value;
    }

    private Conversation RequireConversation(string id) =>
        _store.Get(id) ?? throw new InvalidOperationException("这个对话已经不在了。");

    private void SendConversationList(CenterConnection sender)
    {
        var items = new JsonArray();
        foreach (var conversation in _store.List())
        {
            bool working;
            lock (_gate)
            {
                working = _live.TryGetValue(conversation.Id, out var live) && live.Streaming;
            }

            items.Add(new JsonObject
            {
                ["id"] = conversation.Id,
                ["title"] = conversation.Title,
                ["cwd"] = conversation.Cwd,
                ["updatedAt"] = conversation.UpdatedAt,
                ["messageCount"] = conversation.MessageCount,
                ["working"] = working,
                // 跑完的对话是「完成」，空对话才是「没开始」，界面上用不同颜色区分。
                ["status"] = working ? "working" : conversation.MessageCount > 0 ? "ready" : "empty",
            });
        }

        sender.Send(new JsonObject
        {
            ["type"] = "conversation_list",
            ["items"] = items,
            ["workspaces"] = new JsonArray(RecentWorkspaces().Select(entry => (JsonNode?)JsonValue.Create(entry)).ToArray()),
        });
    }

    private void SendState(string id, CenterConnection sender)
    {
        var conversation = RequireConversation(id);
        Live? live;
        lock (_gate)
        {
            _live.TryGetValue(id, out live);
        }

        sender.Send(new JsonObject
        {
            ["type"] = "session_state",
            ["key"] = id,
            ["cwd"] = conversation.Cwd,
            ["title"] = conversation.Title,
            ["state"] = BuildState(conversation, live?.Streaming == true),
            ["messages"] = _store.Messages(id),
            ["stats"] = Parse(conversation.Stats),
        });
    }

    /// <summary>对话的标题、模型、思考等级变了：只回这几项，绝不重发消息，免得覆盖界面上的内容。</summary>
    private static JsonObject Settings(Conversation conversation)
    {
        var payload = new JsonObject
        {
            ["type"] = "conversation_settings",
            ["key"] = conversation.Id,
            ["title"] = conversation.Title,
        };

        if (Parse(conversation.Model) is { } model)
        {
            payload["model"] = model.DeepClone();
        }

        if (!string.IsNullOrEmpty(conversation.ThinkingLevel))
        {
            payload["thinkingLevel"] = conversation.ThinkingLevel;
        }

        return payload;
    }

    /// <summary>
    /// 记住用过的工作区，新建对话时直接挑，不用每次都手打路径。
    /// </summary>
    private void RememberWorkspace(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return;
        }

        var list = RecentWorkspaces();
        list.RemoveAll(entry => string.Equals(entry, cwd, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, cwd);

        var payload = new JsonArray();
        foreach (var entry in list.Take(8))
        {
            payload.Add(entry);
        }

        _store.SetSetting("recent_workspaces", payload.ToJsonString());
    }

    private List<string> RecentWorkspaces()
    {
        if (ParseNode(_store.GetSetting("recent_workspaces")) is not JsonArray stored)
        {
            return new List<string>();
        }

        return stored
            .Select(node => node?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    /// <summary>中心这台机器上的 Pi 装好了没有，界面在设置里给个准话。</summary>
    /// <summary>把注册表里的 Agent 名片转成协议里的形状。</summary>
    private static JsonArray AgentPayload()
    {
        var agents = new JsonArray();
        foreach (var agent in AgentRegistry.Describe())
        {
            var capabilities = new JsonObject();
            capabilities["tools"] = agent.Capabilities.Tools;
            capabilities["thinkingLevels"] = agent.Capabilities.ThinkingLevels;
            capabilities["models"] = agent.Capabilities.Models;
            capabilities["contextUsage"] = agent.Capabilities.ContextUsage;
            capabilities["compaction"] = agent.Capabilities.Compaction;
            capabilities["extensions"] = agent.Capabilities.Extensions;
            capabilities["skills"] = agent.Capabilities.Skills;
            capabilities["commands"] = agent.Capabilities.Commands;
            capabilities["attachments"] = agent.Capabilities.Attachments;

            var info = new JsonArray();
            foreach (var row in agent.Info)
            {
                info.Add(new JsonObject
                {
                    ["label"] = row.Label,
                    ["value"] = row.Value,
                });
            }

            agents.Add(new JsonObject
            {
                ["id"] = agent.Id,
                ["name"] = agent.Name,
                ["version"] = agent.Version,
                ["ready"] = agent.Ready,
                ["capabilities"] = capabilities,
                ["info"] = info,
            });
        }

        return agents;
    }

    /// <summary>
    /// 选工作区时在里面翻目录用：只给盘符和子目录名，不读文件内容。
    /// </summary>
    private static void BrowseDirectory(string path, CenterConnection sender)
    {
        var drives = new JsonArray();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

                drives.Add(new JsonObject
                {
                    ["name"] = label,
                    ["path"] = drive.Name,
                });
            }
            catch (Exception)
            {
                // 光驱、没插的移动盘读卷标会抛，跳过就好。
            }
        }

        var current = string.Empty;
        string? parent = null;
        var dirs = new JsonArray();
        var error = string.Empty;

        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var full = Path.GetFullPath(path.Trim().Trim('"'));
                if (Directory.Exists(full))
                {
                    current = full;
                    parent = Directory.GetParent(full)?.FullName;
                    dirs = ListSubdirectories(full);
                }
                else
                {
                    error = "这个目录不存在";
                }
            }
            catch (Exception)
            {
                error = "这个路径打不开";
            }
        }

        sender.Send(new JsonObject
        {
            ["type"] = "browse_dir",
            ["path"] = current,
            ["parent"] = parent,
            ["error"] = error,
            ["drives"] = drives,
            ["dirs"] = dirs,
        });
    }

    private static JsonArray ListSubdirectories(string folder)
    {
        var items = new JsonArray();
        List<string> entries;

        try
        {
            entries = Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception)
        {
            return items;
        }

        foreach (var entry in entries.OrderBy(item => Path.GetFileName(item), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new DirectoryInfo(entry);
                if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.System))
                {
                    continue;
                }

                items.Add(new JsonObject
                {
                    ["name"] = info.Name,
                    ["path"] = info.FullName,
                });
            }
            catch (Exception)
            {
                // 权限不足的目录单独跳过，不影响整层。
            }
        }

        return items;
    }

    /// <summary>
    /// 技能页的清单：直接读磁盘上的 SKILL.md，不经过 Pi，没有对话也能看。
    /// </summary>
    private static void SendSkills(string cwd, CenterConnection sender)
    {
        var items = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, scope) in SkillRoots(cwd))
        {
            List<string> directories;
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var directory in directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(file) || !seen.Add(file))
                {
                    continue;
                }

                var skill = ReadSkillHeader(file, Path.GetFileName(directory));
                items.Add(new JsonObject
                {
                    ["name"] = skill.Name,
                    ["description"] = skill.Description,
                    ["path"] = file,
                    ["scope"] = scope,
                    ["commandOnly"] = skill.CommandOnly,
                });
            }
        }

        sender.Send(new JsonObject
        {
            ["type"] = "skills",
            ["cwd"] = cwd,
            ["items"] = items,
        });
    }

    private static IEnumerable<(string Root, string Scope)> SkillRoots(string cwd)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return (Path.Combine(home, ".pi", "agent", "skills"), "用户");
        yield return (Path.Combine(home, ".agents", "skills"), "用户");

        if (string.IsNullOrWhiteSpace(cwd))
        {
            yield break;
        }

        string full;
        try
        {
            full = Path.GetFullPath(cwd);
        }
        catch (Exception)
        {
            yield break;
        }

        yield return (Path.Combine(full, ".pi", "skills"), "项目");
        yield return (Path.Combine(full, ".agents", "skills"), "项目");
    }

    private static (string Name, string Description, bool CommandOnly) ReadSkillHeader(string file, string fallbackName)
    {
        var name = fallbackName;
        var description = string.Empty;
        var commandOnly = false;

        try
        {
            var lines = File.ReadAllLines(file).Take(80).ToList();
            if (lines.Count == 0 || lines[0].Trim() != "---")
            {
                return (name, description, commandOnly);
            }

            for (var index = 1; index < lines.Count; index++)
            {
                var line = lines[index];
                if (line.Trim() == "---")
                {
                    break;
                }

                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();

                if (key == "disable-model-invocation")
                {
                    commandOnly = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (key == "name" && value.Length > 0)
                {
                    name = Unquote(value);
                    continue;
                }

                if (key != "description")
                {
                    continue;
                }

                // 描述可能是 > 或 | 折起来的块，往下把缩进行都收过来。
                if (value is ">" or ">-" or "|" or "|-")
                {
                    var parts = new List<string>();
                    while (index + 1 < lines.Count)
                    {
                        var next = lines[index + 1];
                        if (next.Trim().Length == 0)
                        {
                            index++;
                            continue;
                        }

                        if (next.StartsWith("  ", StringComparison.Ordinal) || next.StartsWith('\t'))
                        {
                            parts.Add(next.Trim());
                            index++;
                            continue;
                        }

                        break;
                    }

                    description = string.Join(" ", parts);
                }
                else
                {
                    description = Unquote(value);
                }
            }
        }
        catch (Exception)
        {
            // 读不通就当没有描述，别让一页清单挂掉。
        }

        return (name, description, commandOnly);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    /// <summary>
    /// 扩展页：Pi 配置里声明的扩展包（装没装、什么版本），加上当前工作区里
    /// Pi 报过的扩展命令。
    /// </summary>
    private async Task SendExtensionsAsync(string cwd, CenterConnection sender)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var settingsPath = Path.Combine(home, ".pi", "agent", "settings.json");
        var problems = new JsonArray();
        var packages = new JsonArray();
        var declared = new List<string>();

        try
        {
            if (File.Exists(settingsPath) &&
                JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject settings &&
                settings["packages"] is JsonArray list)
            {
                foreach (var entry in list)
                {
                    var text = entry?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        declared.Add(text!);
                    }
                }
            }
        }
        catch (Exception)
        {
            problems.Add("读不出 Pi 的扩展配置");
        }

        foreach (var source in declared)
        {
            var (kind, name) = SplitPackageSource(source);
            var directory = PackageDirectory(home, kind, name);
            packages.Add(new JsonObject
            {
                ["source"] = source,
                ["kind"] = kind,
                ["name"] = name,
                ["path"] = directory ?? string.Empty,
                ["installed"] = directory is not null && Directory.Exists(directory),
                ["version"] = directory is null ? string.Empty : ReadPackageVersion(directory),
            });
        }

        // 扩展命令只认缓存：不去为了列个清单悄悄起一个 Pi 会话。
        // 用户打开对话、在输入框里打过 / 之后，中心这边就有了这个工作区的缓存。
        var commands = new JsonArray();
        if (!string.IsNullOrWhiteSpace(cwd) &&
            ParseNode(_store.GetSetting("commands:" + cwd.ToLowerInvariant())) is JsonArray cached)
        {
            foreach (var entry in cached.OfType<JsonObject>())
            {
                if (entry["source"]?.GetValue<string>() != "extension")
                {
                    continue;
                }

                commands.Add(new JsonObject
                {
                    ["name"] = entry["name"]?.GetValue<string>() ?? string.Empty,
                    ["description"] = entry["description"]?.GetValue<string>() ?? string.Empty,
                    ["scope"] = (entry["sourceInfo"] as JsonObject)?["scope"]?.GetValue<string>() ?? string.Empty,
                });
            }
        }

        sender.Send(new JsonObject
        {
            ["type"] = "extensions",
            ["cwd"] = cwd,
            ["settingsPath"] = settingsPath,
            ["packages"] = packages,
            ["commands"] = commands,
            ["problems"] = problems,
        });

        await Task.CompletedTask;
    }

    private static (string Kind, string Name) SplitPackageSource(string source)
    {
        if (source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
        {
            return ("npm", source[4..].Trim());
        }

        if (source.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = source[4..].Trim();
            var at = rest.LastIndexOf('@');
            if (at > rest.LastIndexOf('/'))
            {
                rest = rest[..at];
            }

            return ("git", rest);
        }

        return ("path", source.Trim());
    }

    private static string? PackageDirectory(string home, string kind, string name)
    {
        return kind switch
        {
            "npm" => Path.Combine(home, ".pi", "agent", "npm", "node_modules", name),
            "git" => Path.Combine(home, ".pi", "agent", "git", name),
            "path" => name,
            _ => null,
        };
    }

    private static string ReadPackageVersion(string directory)
    {
        try
        {
            var manifest = Path.Combine(directory, "package.json");
            if (File.Exists(manifest) &&
                JsonNode.Parse(File.ReadAllText(manifest)) is JsonObject json)
            {
                return json["version"]?.GetValue<string>() ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // 版本读不出来不影响列表。
        }

        return string.Empty;
    }

    /// <summary>
    /// 自定义样式存在中心，换设备打开也是同一套；改完推给所有在线客户端，立刻生效。
    /// </summary>
    private void SetCustomCss(string css)
    {
        _store.SetSetting("custom_css", css);
        Broadcast(new JsonObject
        {
            ["type"] = "css",
            ["css"] = css,
        });
    }

    /// <summary>中心日志的尾巴。手机上出了问题先看这一屏，不用回电脑翻文件。</summary>
    private static void ReadLog(int lines, CenterConnection sender)
    {
        var tail = Math.Clamp(lines, 20, 500);
        var path = Log.FilePath;
        var text = string.Empty;

        try
        {
            if (File.Exists(path))
            {
                text = string.Join('\n', File.ReadLines(path).TakeLast(tail));
            }
        }
        catch (Exception ex)
        {
            text = "日志读不出来：" + ex.Message;
        }

        sender.Send(new JsonObject
        {
            ["type"] = "log_tail",
            ["path"] = path,
            ["text"] = text,
        });
    }

    /// <summary>按标题和正文搜对话。</summary>
    private void SearchConversations(string query, CenterConnection sender)
    {
        var hits = _store.Search(query);
        var items = new JsonArray();

        foreach (var (conversation, snippet) in hits)
        {
            items.Add(new JsonObject
            {
                ["id"] = conversation.Id,
                ["title"] = conversation.Title,
                ["cwd"] = conversation.Cwd,
                ["updatedAt"] = conversation.UpdatedAt,
                ["messageCount"] = conversation.MessageCount,
                ["snippet"] = snippet,
            });
        }

        sender.Send(new JsonObject
        {
            ["type"] = "search_results",
            ["query"] = query,
            ["items"] = items,
        });
    }

    /// <summary>新建对话前先看看这个目录在不在，别等建完才发现写不进会话。</summary>
    private static void CheckPath(string path, CenterConnection sender)
    {
        var cleaned = path.Trim().Trim('"');
        var exists = false;
        var isDirectory = false;
        var reason = string.Empty;

        if (cleaned.Length == 0)
        {
            reason = "先填一个目录";
        }
        else
        {
            try
            {
                var full = Path.GetFullPath(cleaned);
                exists = Directory.Exists(full) || File.Exists(full);
                isDirectory = Directory.Exists(full);
                if (!exists)
                {
                    reason = "这个目录不存在";
                }
                else if (!isDirectory)
                {
                    reason = "这是一个文件，不是目录";
                }
                else
                {
                    cleaned = full;
                }
            }
            catch (Exception)
            {
                reason = "这个路径写不出合法路径";
            }
        }

        sender.Send(new JsonObject
        {
            ["type"] = "path_check",
            ["path"] = cleaned,
            ["exists"] = exists && isDirectory,
            ["reason"] = reason,
        });
    }

    /// <summary>工作区文件浏览：只读，只允许在工作区里走。</summary>
    private void ListDirectory(string id, JsonObject message, CenterConnection sender)
    {
        const int limit = 500;
        var conversation = RequireConversation(id);
        var relative = message["path"]?.GetValue<string>() ?? string.Empty;
        var target = ResolveInWorkspace(conversation.Cwd, relative);
        var items = new JsonArray();

        if (target is not null && Directory.Exists(target))
        {
            try
            {
                var entries = new List<(string Name, bool IsDirectory, long Size, DateTime Modified)>();
                foreach (var directory in Directory.EnumerateDirectories(target))
                {
                    entries.Add((Path.GetFileName(directory), true, 0, SafeModified(directory)));
                }

                foreach (var file in Directory.EnumerateFiles(target))
                {
                    entries.Add((Path.GetFileName(file), false, SafeSize(file), SafeModified(file)));
                }

                foreach (var entry in entries
                             .OrderBy(entry => entry.IsDirectory ? 0 : 1)
                             .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                             .Take(limit))
                {
                    var childRelative = string.IsNullOrEmpty(relative)
                        ? entry.Name
                        : relative.TrimEnd('/') + "/" + entry.Name;

                    items.Add(new JsonObject
                    {
                        ["name"] = entry.Name,
                        ["path"] = childRelative,
                        ["dir"] = entry.IsDirectory,
                        ["size"] = entry.Size,
                        ["modified"] = entry.Modified.ToString("O"),
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Write($"列目录失败：{target}", ex);
            }
        }

        sender.Send(new JsonObject
        {
            ["type"] = "dir_listing",
            ["key"] = id,
            ["path"] = relative,
            ["items"] = items,
            ["exists"] = target is not null && Directory.Exists(target),
        });
    }

    /// <summary>读一个文本文件给界面看。二进制与超大文件不猜，直接说明原因。</summary>
    private void ReadFile(string id, JsonObject message, CenterConnection sender)
    {
        const long maxBytes = 512 * 1024;
        var conversation = RequireConversation(id);
        var relative = message["path"]?.GetValue<string>() ?? string.Empty;
        var target = ResolveInWorkspace(conversation.Cwd, relative);

        string? content = null;
        string? reason = null;

        if (target is null || !File.Exists(target))
        {
            reason = "找不到这个文件";
        }
        else
        {
            var info = new FileInfo(target);
            if (info.Length > maxBytes)
            {
                reason = "文件超过 512 KB，没有打开";
            }
            else
            {
                try
                {
                    var bytes = File.ReadAllBytes(target);
                    var probe = bytes.AsSpan(0, Math.Min(bytes.Length, 8 * 1024));
                    if (probe.IndexOf((byte)0) >= 0)
                    {
                        reason = "看起来是二进制文件，没有打开";
                    }
                    else
                    {
                        content = System.Text.Encoding.UTF8.GetString(bytes);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"读文件失败：{target}", ex);
                    reason = "读不出来：" + ex.Message;
                }
            }
        }

        sender.Send(new JsonObject
        {
            ["type"] = "file_content",
            ["key"] = id,
            ["path"] = relative,
            ["text"] = content,
            ["reason"] = reason,
        });
    }

    /// <summary>把相对路径收进工作区。越界（..、绝对路径跑到外面）就返回 null。</summary>
    private static string? ResolveInWorkspace(string cwd, string relative)
    {
        try
        {
            var root = Path.GetFullPath(cwd);
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/')));

            if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            return target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? target : null;
        }
        catch
        {
            return null;
        }
    }

    private static long SafeSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime SafeModified(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Pi 里能用的 / 命令：扩展命令、提示词模板、技能。跟工作区有关，按目录缓存。</summary>
    private async Task SendCommandsAsync(string id, CenterConnection sender)
    {
        var conversation = RequireConversation(id);
        var cacheKey = "commands:" + conversation.Cwd.ToLowerInvariant();
        if (ParseNode(_store.GetSetting(cacheKey)) is JsonArray cached)
        {
            sender.Send(new JsonObject
            {
                ["type"] = "commands",
                ["key"] = id,
                ["items"] = cached.DeepClone(),
            });
            return;
        }

        var live = EnsureLive(conversation);
        var response = await live.Session.SendAsync(new JsonObject { ["type"] = "get_commands" });
        var commands = Data(response)?["commands"] as JsonArray;
        if (commands is not null)
        {
            _store.SetSetting(cacheKey, commands.ToJsonString());
        }

        sender.Send(new JsonObject
        {
            ["type"] = "commands",
            ["key"] = id,
            ["items"] = commands?.DeepClone() ?? new JsonArray(),
        });
    }

    /// <summary>
    /// 输入框里打 @ 时的路径补全。这活不经过 Pi：中心自己扫工作区，
    /// 会话在哪台机器上，补的就是哪台机器上的文件。
    /// </summary>
    private void CompleteFiles(string id, JsonObject message, CenterConnection sender)
    {
        var conversation = RequireConversation(id);
        var query = message["query"]?.GetValue<string>() ?? string.Empty;
        var items = new JsonArray();

        var normalized = query.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var dirPart = slash >= 0 ? normalized[..(slash + 1)] : string.Empty;
        var prefix = slash >= 0 ? normalized[(slash + 1)..] : normalized;

        var root = Path.GetFullPath(conversation.Cwd);
        var baseDir = Path.GetFullPath(Path.Combine(root, dirPart.TrimEnd('/')));

        // 只允许在工作区里面找。
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!baseDir.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(baseDir, root, StringComparison.OrdinalIgnoreCase))
        {
            sender.Send(new JsonObject { ["type"] = "file_completions", ["key"] = id, ["query"] = query, ["items"] = items });
            return;
        }

        if (Directory.Exists(baseDir))
        {
            var entries = new List<(string Name, bool IsDirectory)>();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(baseDir))
                {
                    entries.Add((Path.GetFileName(directory), true));
                }

                foreach (var file in Directory.EnumerateFiles(baseDir))
                {
                    entries.Add((Path.GetFileName(file), false));
                }
            }
            catch (Exception ex)
            {
                Log.Write($"补全时读目录失败：{baseDir}", ex);
            }

            foreach (var entry in entries
                         .Where(entry => entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(entry => entry.IsDirectory ? 0 : 1)
                         .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(60))
            {
                items.Add(new JsonObject
                {
                    ["path"] = dirPart + entry.Name + (entry.IsDirectory ? "/" : string.Empty),
                    ["dir"] = entry.IsDirectory,
                });
            }
        }

        sender.Send(new JsonObject
        {
            ["type"] = "file_completions",
            ["key"] = id,
            ["query"] = query,
            ["items"] = items,
        });
    }

    /// <summary>只发用量，不碰线程内容：收尾后界面会来问一次，不能拿库里的旧副本覆盖流式结果。</summary>
    private void SendStats(string id, CenterConnection sender)
    {
        var conversation = RequireConversation(id);
        sender.Send(new JsonObject
        {
            ["type"] = "stats",
            ["key"] = id,
            ["data"] = Parse(conversation.Stats),
        });
    }

    private static JsonObject BuildState(Conversation conversation, bool streaming)
    {
        var state = new JsonObject
        {
            ["sessionName"] = conversation.Title,
            ["isStreaming"] = streaming,
        };

        if (Parse(conversation.Model) is { } model)
        {
            state["model"] = model;
        }

        if (!string.IsNullOrEmpty(conversation.ThinkingLevel))
        {
            state["thinkingLevel"] = conversation.ThinkingLevel;
        }

        return state;
    }

    private async Task NewConversationAsync(JsonObject message, CenterConnection sender)
    {
        var cwd = message["cwd"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            throw new InvalidOperationException($"工作目录不存在：{cwd}");
        }

        var title = message["title"]?.GetValue<string>();
        var conversation = _store.Create(cwd, title);
        RememberWorkspace(conversation.Cwd);
        Log.Write($"新对话 {conversation.Id} cwd={cwd}");
        SendState(conversation.Id, sender);
        SendConversationList(sender);
    }

    private void RenameConversation(string id, JsonObject message, CenterConnection sender)
    {
        RequireConversation(id);
        var title = message["title"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            throw new InvalidOperationException("标题不能为空。");
        }

        _store.Rename(id, title);
        var updated = RequireConversation(id);
        Broadcast(Settings(updated));
        SendConversationList(sender);
    }

    private async Task DeleteConversationAsync(string id)
    {
        Live? live;
        lock (_gate)
        {
            _live.Remove(id, out live);
        }

        if (live is not null)
        {
            await live.Session.DisposeAsync();
        }

        StopTerminalInternal(id);
        _store.Delete(id);
        Log.Write($"删除对话 {id}（Pi 那边的会话文件保留在原处）");
    }

    /// <summary>拿到这个对话对应的活子进程，没有就按绑定续上。</summary>
    private Live EnsureLive(Conversation conversation)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(conversation.Id, out var existing) && existing.Session.IsRunning)
            {
                return existing;
            }
        }

        if (!Directory.Exists(conversation.Cwd))
        {
            throw new InvalidOperationException(
                $"这个对话的工作目录不在了（{conversation.Cwd}），历史还能看，但没法继续聊。");
        }

        var resume = conversation.PiSessionFile;
        if (!string.IsNullOrEmpty(resume) && !File.Exists(resume))
        {
            // Pi 那边的记录被清掉了：历史还在 SevenStation.Core，只是接不上原来那段上下文。
            Log.Write($"对话 {conversation.Id} 的 Pi 会话文件已不存在，改开新会话：{resume}");
            resume = null;
        }

        var session = PiSession.Start(conversation.Cwd, resume);
        var live = new Live { Conversation = conversation, Session = session };
        Log.Write($"对话 {conversation.Id} 起子进程 cwd={conversation.Cwd} resume={resume ?? "（新建）"}");

        session.RecordReceived += record =>
        {
            var name = record["type"]?.GetValue<string>();
            if (name == "agent_start")
            {
                live.Streaming = true;
                // 新一轮从零开始记，判重集合不用一直长。
                lock (live.Persisted)
                {
                    live.Persisted.Clear();
                }
            }
            else if (name == "agent_settled")
            {
                live.Streaming = false;
                _ = PersistTurnAsync(live);
            }
            else if (name == "message_end")
            {
                // 用户的话和模型每段输出一产生就落库：这一轮还没结束，
                // 中途切到别的对话再切回来，看到的不是空白。
                AppendLiveMessage(live, record["message"] as JsonObject);
            }
            else if (name == "tool_execution_end")
            {
                AppendLiveMessage(live, ToolResultMessage(record));
            }

            Broadcast(new JsonObject
            {
                ["type"] = "event",
                ["key"] = conversation.Id,
                ["event"] = record.DeepClone(),
            });
        };

        session.Exited += (code, stderr) =>
        {
            lock (_gate)
            {
                if (_live.TryGetValue(conversation.Id, out var current) && ReferenceEquals(current.Session, session))
                {
                    _live.Remove(conversation.Id);
                }
            }

            Log.Write($"对话 {conversation.Id} 子进程退出 code={code} stderr={stderr.Replace(Environment.NewLine, " | ")}");
            Broadcast(new JsonObject
            {
                ["type"] = "process_exit",
                ["key"] = conversation.Id,
                ["code"] = code,
                ["stderr"] = stderr,
            });
        };

        lock (_gate)
        {
            _live[conversation.Id] = live;
        }

        // 子进程一起来就把默认模型与思考等级记下来，之后打开这个对话不用再问 Pi。
        _ = CaptureSettingsAsync(live);
        Broadcast(new JsonObject { ["type"] = "conversation_saved", ["key"] = conversation.Id });

        return live;
    }

    /// <summary>把一条消息追加进库，同一轮里重复的事件不重复写。</summary>
    private void AppendLiveMessage(Live live, JsonObject? message)
    {
        if (message is null)
        {
            return;
        }

        var role = message["role"]?.GetValue<string>();
        if (role is not ("user" or "assistant" or "toolResult"))
        {
            return;
        }

        // 工具结果按调用号判重，其它按时间点；拿不到标识就照写，宁可多一条也不漏。
        var identity = role == "toolResult"
            ? message["toolCallId"]?.GetValue<string>()
            : message["timestamp"]?.ToJsonString();

        lock (live.Persisted)
        {
            if (!string.IsNullOrEmpty(identity) && !live.Persisted.Add(role + ":" + identity))
            {
                return;
            }
        }

        try
        {
            _store.AppendMessages(live.Conversation.Id, new JsonNode?[] { message.DeepClone() });
        }
        catch (Exception ex)
        {
            // 落不下来只是这段历史少一截，不能让正在跑的这一轮跟着挂。
            Log.Write($"对话 {live.Conversation.Id} 追加消息失败", ex);
        }
    }

    /// <summary>
    /// 工具结果事件转成 Pi 的消息形状，跟历史里存的那条一致，
    /// 这样中途切回来、完成后再看，走的是同一套渲染。
    /// </summary>
    private static JsonObject? ToolResultMessage(JsonObject record)
    {
        var id = record["toolCallId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return new JsonObject
        {
            ["role"] = "toolResult",
            ["toolCallId"] = id,
            ["toolName"] = record["toolName"]?.GetValue<string>(),
            ["content"] = record["result"] is JsonObject result && result["content"] is JsonArray content
                ? content.DeepClone()
                : new JsonArray(),
            ["isError"] = record["isError"]?.GetValue<bool>() ?? false,
            ["durationMs"] = record["durationMs"]?.GetValue<long>() ?? 0,
            ["timestamp"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>一轮结束后把 Pi 那边的消息取回来，存成 SevenStation.Core 自己的副本。</summary>
    private async Task PersistTurnAsync(Live live)
    {
        try
        {
            var messages = await live.Session.SendAsync(new JsonObject { ["type"] = "get_messages" });
            var stats = await live.Session.SendAsync(new JsonObject { ["type"] = "get_session_stats" });
            var state = await live.Session.SendAsync(new JsonObject { ["type"] = "get_state" });

            var data = Data(messages);
            var stored = data?["messages"] as JsonArray;
            var conversation = _store.Get(live.Conversation.Id);
            var title = string.IsNullOrEmpty(conversation?.Title)
                ? DeriveTitleFrom(stored)
                : null;

            var statsData = Data(stats);
            _store.SaveTurn(live.Conversation.Id, stored, statsData, Data(state), title);

            // 存完立刻把最新用量推回去，别让界面等下一次刷新。
            Broadcast(new JsonObject
            {
                ["type"] = "stats",
                ["key"] = live.Conversation.Id,
                ["data"] = statsData?.DeepClone(),
            });
            Broadcast(new JsonObject
            {
                ["type"] = "conversation_saved",
                ["key"] = live.Conversation.Id,
            });
        }
        catch (Exception ex)
        {
            // 存不下来只是历史少一段，不能让对话本身挂掉。
            Log.Write($"对话 {live.Conversation.Id} 保存失败", ex);
        }
    }

    private static string? DeriveTitleFrom(JsonArray? messages)
    {
        if (messages is null)
        {
            return null;
        }

        foreach (var node in messages)
        {
            if (node is not JsonObject message || message["role"]?.GetValue<string>() != "user")
            {
                continue;
            }

            var text = message["content"] switch
            {
                JsonValue value => value.ToString(),
                JsonArray blocks => string.Join(" ", blocks
                    .OfType<JsonObject>()
                    .Where(block => block["type"]?.GetValue<string>() == "text")
                    .Select(block => block["text"]?.GetValue<string>())),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var firstLine = text.Split('\n')[0].Trim();
            return firstLine.Length > 40 ? firstLine[..40] + "…" : firstLine;
        }

        return null;
    }

    private async Task PromptAsync(string id, JsonObject message, CenterConnection sender)
    {
        var conversation = RequireConversation(id);
        var live = EnsureLive(conversation);

        var command = new JsonObject
        {
            ["type"] = "prompt",
            ["message"] = message["message"]?.GetValue<string>() ?? string.Empty,
        };

        if (message["images"] is JsonArray images)
        {
            command["images"] = images.DeepClone();
        }

        var behavior = message["streamingBehavior"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(behavior))
        {
            command["streamingBehavior"] = behavior;
        }

        // 第一次说话就用这句话当标题，省一次模型调用。
        if (string.IsNullOrEmpty(conversation.Title))
        {
            var text = command["message"]?.GetValue<string>() ?? string.Empty;
            var firstLine = text.Split('\n')[0].Trim();
            if (firstLine.Length > 0)
            {
                _store.Rename(id, firstLine.Length > 40 ? firstLine[..40] + "…" : firstLine);
                // 标题是服务端定的，推回去让开着的界面立刻改名，不用等重开。
                Broadcast(Settings(RequireConversation(id)));
                Broadcast(new JsonObject { ["type"] = "conversation_saved", ["key"] = id });
            }
        }

        var response = await live.Session.SendAsync(command, TimeSpan.FromSeconds(60));
        sender.Send(new JsonObject
        {
            ["type"] = "prompt_accepted",
            ["key"] = id,
            ["disposition"] = Data(response)?["disposition"]?.GetValue<string>(),
        });
    }

    private async Task SimpleAsync(string id, string command)
    {
        var live = EnsureLive(RequireConversation(id));
        await live.Session.SendAsync(new JsonObject { ["type"] = command }, TimeSpan.FromSeconds(120));
    }

    private async Task SetModelAsync(string id, JsonObject message, CenterConnection sender)
    {
        var live = EnsureLive(RequireConversation(id));
        await live.Session.SendAsync(new JsonObject
        {
            ["type"] = "set_model",
            ["provider"] = message["provider"]?.GetValue<string>(),
            ["modelId"] = message["modelId"]?.GetValue<string>(),
        });
        await CaptureSettingsAsync(live);
        sender.Send(Settings(RequireConversation(id)));
    }

    private async Task SetThinkingAsync(string id, JsonObject message, CenterConnection sender)
    {
        var live = EnsureLive(RequireConversation(id));
        await live.Session.SendAsync(new JsonObject
        {
            ["type"] = "set_thinking_level",
            ["level"] = message["level"]?.GetValue<string>(),
        });
        await CaptureSettingsAsync(live);
        sender.Send(Settings(RequireConversation(id)));
    }

    /// <summary>模型与思考等级是对话自己的设置，改了立刻记下来，下次打开还对得上。</summary>
    private async Task CaptureSettingsAsync(Live live)
    {
        var state = await live.Session.SendAsync(new JsonObject { ["type"] = "get_state" });
        _store.SaveTurn(live.Conversation.Id, null, null, Data(state));
    }

    private async Task SendModelsAsync(string id, CenterConnection sender)
    {
        RequireConversation(id);

        // 模型列表跟对话无关，缓存起来：只为看一眼历史不该起 Pi 进程。
        var cachedModels = Parse(_store.GetSetting("models"));
        var cachedLevels = Parse(_store.GetSetting("thinkingLevels"));
        if (cachedModels is not null && cachedLevels is not null)
        {
            sender.Send(new JsonObject
            {
                ["type"] = "models",
                ["key"] = id,
                ["data"] = cachedModels.DeepClone(),
                ["thinkingLevels"] = cachedLevels.DeepClone(),
            });
            return;
        }

        var live = EnsureLive(RequireConversation(id));
        var models = await live.Session.SendAsync(new JsonObject { ["type"] = "get_available_models" });
        var levels = await live.Session.SendAsync(new JsonObject { ["type"] = "get_available_thinking_levels" });
        var modelData = Data(models);
        var levelData = Data(levels);

        if (modelData is not null && levelData is not null)
        {
            _store.SetSetting("models", modelData.ToJsonString());
            _store.SetSetting("thinkingLevels", levelData.ToJsonString());
        }

        sender.Send(new JsonObject
        {
            ["type"] = "models",
            ["key"] = id,
            ["data"] = modelData?.DeepClone(),
            ["thinkingLevels"] = levelData?.DeepClone(),
        });
    }

    private void RespondToExtension(string id, JsonObject message)
    {
        Live live;
        lock (_gate)
        {
            if (!_live.TryGetValue(id, out live!))
            {
                return;
            }
        }

        var response = new JsonObject
        {
            ["type"] = "extension_ui_response",
            ["id"] = message["id"]?.GetValue<string>(),
        };

        if (message["cancelled"]?.GetValue<bool>() == true)
        {
            response["cancelled"] = true;
        }
        else if (message.ContainsKey("confirmed"))
        {
            response["confirmed"] = message["confirmed"]?.GetValue<bool>() ?? false;
        }
        else
        {
            response["value"] = message["value"]?.DeepClone();
        }

        live.Session.SendNoReply(response);
    }

    public async Task ShutdownAsync()
    {
        Live[] entries;
        lock (_gate)
        {
            entries = _live.Values.ToArray();
            _live.Clear();
        }

        foreach (var entry in entries)
        {
            await entry.Session.DisposeAsync();
        }

        TerminalSession[] terminals;
        lock (_terminalGate)
        {
            terminals = _terminals.Values.ToArray();
            _terminals.Clear();
        }

        foreach (var terminal in terminals)
        {
            terminal.Dispose();
        }
    }

    /* ------------------------------------------------------------------ *
     * 终端：每条对话一个常驻 shell，多端看到的是同一份输出
     * ------------------------------------------------------------------ */

    private void StartTerminal(string id, CenterConnection sender)
    {
        var conversation = RequireConversation(id);
        var terminal = EnsureTerminal(conversation);

        sender.Send(new JsonObject
        {
            ["type"] = "terminal_state",
            ["key"] = id,
            ["running"] = terminal.Running,
            ["cwd"] = terminal.Cwd,
            ["shell"] = terminal.ShellName,
        });

        // 另一个设备先开过，就把已经跑出来的输出补上。
        var tail = terminal.Tail;
        if (tail.Length > 0)
        {
            sender.Send(new JsonObject
            {
                ["type"] = "terminal_output",
                ["key"] = id,
                ["text"] = tail,
                ["replay"] = true,
            });
        }
    }

    private void WriteTerminal(string id, JsonObject message)
    {
        var conversation = RequireConversation(id);
        var line = message["text"]?.GetValue<string>() ?? string.Empty;
        var terminal = EnsureTerminal(conversation);
        terminal.Write(line);
    }

    private void StopTerminal(string id, CenterConnection sender)
    {
        StopTerminalInternal(id);
        sender.Send(new JsonObject
        {
            ["type"] = "terminal_state",
            ["key"] = id,
            ["running"] = false,
        });
    }

    private void StopTerminalInternal(string id)
    {
        TerminalSession? terminal;
        lock (_terminalGate)
        {
            _terminals.Remove(id, out terminal);
        }

        terminal?.Dispose();
    }

    private TerminalSession EnsureTerminal(Conversation conversation)
    {
        lock (_terminalGate)
        {
            if (_terminals.TryGetValue(conversation.Id, out var existing) && existing.Running)
            {
                return existing;
            }
        }

        var terminal = new TerminalSession(
            conversation.Cwd,
            onOutput: (text) => Broadcast(new JsonObject
            {
                ["type"] = "terminal_output",
                ["key"] = conversation.Id,
                ["text"] = text,
            }),
            onExit: (code) =>
            {
                lock (_terminalGate)
                {
                    _terminals.Remove(conversation.Id);
                }

                Broadcast(new JsonObject
                {
                    ["type"] = "terminal_exit",
                    ["key"] = conversation.Id,
                    ["code"] = code,
                });
            });

        lock (_terminalGate)
        {
            _terminals[conversation.Id] = terminal;
        }

        return terminal;
    }

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? Data(JsonObject response)
    {
        if (response["success"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException(
                response["error"]?.GetValue<string>() ?? "Pi 拒绝了这条命令。");
        }

        return response["data"] as JsonObject;
    }
}

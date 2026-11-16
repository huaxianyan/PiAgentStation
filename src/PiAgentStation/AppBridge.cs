using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiAgentStation.Rpc;
using PiAgentStation.Sessions;

namespace PiAgentStation;

/// <summary>界面（WebView2）与 Pi 子进程之间的桥。消息体是 JSON，两边约定 type 字段。</summary>
public sealed class AppBridge
{
    private sealed class Entry
    {
        public required string Key { get; init; }
        public required PiSession Session { get; init; }
        public string? SessionPath { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byKey = new();
    private readonly Dictionary<string, string> _keyByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>发往界面的 JSON 字符串。已切回 UI 线程。</summary>
    public event Action<string>? Outbound;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public void Handle(string json)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return;
        }

        if (message is null)
        {
            return;
        }

        _ = Task.Run(() => DispatchAsync(message));
    }

    private async Task DispatchAsync(JsonObject message)
    {
        var type = message["type"]?.GetValue<string>();
        var key = message["key"]?.GetValue<string>();
        try
        {
            switch (type)
            {
                case "list_sessions":
                    SendSessionList();
                    break;

                case "open_session":
                    await OpenSessionAsync(message);
                    break;

                case "new_session":
                    await NewSessionAsync(message);
                    break;

                case "refresh_state":
                    await SendStateAsync(Require(key));
                    break;

                case "refresh_stats":
                    await SendStatsAsync(Require(key));
                    break;

                case "prompt":
                    await PromptAsync(Require(key), message);
                    break;

                case "abort":
                    await SimpleAsync(Require(key), "abort");
                    break;

                case "compact":
                    await SimpleAsync(Require(key), "compact");
                    break;

                case "set_model":
                    await SetModelAsync(Require(key), message);
                    break;

                case "set_thinking":
                    await SetThinkingAsync(Require(key), message);
                    break;

                case "list_models":
                    await SendModelsAsync(Require(key));
                    break;

                case "extension_response":
                    RespondToExtension(Require(key), message);
                    break;

                case "close_session":
                    await CloseSessionAsync(Require(key));
                    break;

                default:
                    Emit(new JsonObject
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
            Emit(new JsonObject
            {
                ["type"] = "error",
                ["key"] = key,
                ["message"] = ex.Message,
            });
        }
    }

    private static string Require(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("缺少会话标识。");
        }

        return key;
    }

    private void SendSessionList()
    {
        var items = new JsonArray();
        foreach (var summary in SessionStore.List())
        {
            items.Add(new JsonObject
            {
                ["path"] = summary.Path,
                ["id"] = summary.SessionId,
                ["cwd"] = summary.Cwd,
                ["name"] = summary.Name,
                ["title"] = summary.Title,
                ["updatedAt"] = summary.UpdatedAt.ToUnixTimeMilliseconds(),
                ["size"] = summary.SizeBytes,
            });
        }

        Emit(new JsonObject
        {
            ["type"] = "session_list",
            ["root"] = SessionStore.RootDirectory,
            ["items"] = items,
        });
    }

    private async Task OpenSessionAsync(JsonObject message)
    {
        var path = message["path"]?.GetValue<string>()
            ?? throw new InvalidOperationException("缺少会话文件路径。");

        var summary = SessionStore.TryRead(path)
            ?? throw new InvalidOperationException("读不到这个会话文件。");

        if (string.IsNullOrEmpty(summary.Cwd) || !Directory.Exists(summary.Cwd))
        {
            throw new InvalidOperationException(
                $"这个会话记录的工作目录已不存在（{summary.Cwd}），无法在其中继续工作。");
        }

        var entry = GetOrStart(path, summary.Cwd, path);
        await SendStateAsync(entry.Key);
    }

    private async Task NewSessionAsync(JsonObject message)
    {
        var cwd = message["cwd"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            throw new InvalidOperationException($"工作目录不存在：{cwd}");
        }

        var entry = GetOrStart(null, cwd, null);
        await SendStateAsync(entry.Key);
    }

    private Entry GetOrStart(string? sessionPath, string workingDirectory, string? resumePath)
    {
        lock (_gate)
        {
            if (sessionPath is not null && _keyByPath.TryGetValue(sessionPath, out var existingKey) &&
                _byKey.TryGetValue(existingKey, out var existing) && existing.Session.IsRunning)
            {
                return existing;
            }

            var key = "s-" + Guid.NewGuid().ToString("N")[..12];
            Log.Write($"启动子进程 key={key} cwd={workingDirectory} session={resumePath ?? "（新建）"}");
            var session = PiSession.Start(workingDirectory, resumePath);
            var entry = new Entry { Key = key, Session = session, SessionPath = sessionPath };

            session.RecordReceived += record => Emit(new JsonObject
            {
                ["type"] = "event",
                ["key"] = key,
                ["event"] = record.DeepClone(),
            });

            session.Exited += (code, stderr) =>
            {
                Log.Write($"子进程退出 key={key} code={code} stderr={stderr.Replace(Environment.NewLine, " | ")}");
                lock (_gate)
                {
                    if (_byKey.TryGetValue(key, out var current) && ReferenceEquals(current.Session, session))
                    {
                        _byKey.Remove(key);
                        if (current.SessionPath is not null)
                        {
                            _keyByPath.Remove(current.SessionPath);
                        }
                    }
                }

                Emit(new JsonObject
                {
                    ["type"] = "process_exit",
                    ["key"] = key,
                    ["code"] = code,
                    ["stderr"] = stderr,
                });
            };

            _byKey[key] = entry;
            if (sessionPath is not null)
            {
                _keyByPath[sessionPath] = key;
            }

            return entry;
        }
    }

    private Entry Find(string key)
    {
        lock (_gate)
        {
            return _byKey.TryGetValue(key, out var entry)
                ? entry
                : throw new InvalidOperationException("会话不存在或已关闭。");
        }
    }

    private async Task SendStateAsync(string key)
    {
        var entry = Find(key);
        var state = await entry.Session.SendAsync(new JsonObject { ["type"] = "get_state" });
        var messages = await entry.Session.SendAsync(new JsonObject { ["type"] = "get_messages" });
        var stats = await entry.Session.SendAsync(new JsonObject { ["type"] = "get_session_stats" });
        var stateData = Data(state);
        var payload = new JsonObject
        {
            ["type"] = "session_state",
            ["key"] = key,
            ["cwd"] = entry.Session.WorkingDirectory,
            // 响应里的节点已经挂在原响应树上，挂到新对象前必须深拷贝。
            ["state"] = stateData?.DeepClone(),
            ["messages"] = Data(messages)?["messages"]?.DeepClone(),
            ["stats"] = Data(stats)?.DeepClone(),
        };

        var sessionFile = stateData?["sessionFile"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(sessionFile))
        {
            lock (_gate)
            {
                entry.SessionPath = sessionFile;
                _keyByPath[sessionFile] = key;
            }
        }

        Emit(payload);
    }

    private async Task SendStatsAsync(string key)
    {
        var entry = Find(key);
        var stats = await entry.Session.SendAsync(new JsonObject { ["type"] = "get_session_stats" });
        Emit(new JsonObject
        {
            ["type"] = "stats",
            ["key"] = key,
            ["data"] = Data(stats)?.DeepClone(),
        });
    }

    private async Task PromptAsync(string key, JsonObject message)
    {
        var entry = Find(key);
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

        var response = await entry.Session.SendAsync(command, TimeSpan.FromSeconds(60));
        Emit(new JsonObject
        {
            ["type"] = "prompt_accepted",
            ["key"] = key,
            ["disposition"] = Data(response)?["disposition"]?.GetValue<string>(),
        });
    }

    private async Task SimpleAsync(string key, string command)
    {
        var entry = Find(key);
        await entry.Session.SendAsync(new JsonObject { ["type"] = command }, TimeSpan.FromSeconds(120));
    }

    private async Task SetModelAsync(string key, JsonObject message)
    {
        var entry = Find(key);
        var command = new JsonObject
        {
            ["type"] = "set_model",
            ["provider"] = message["provider"]?.GetValue<string>(),
            ["modelId"] = message["modelId"]?.GetValue<string>(),
        };
        await entry.Session.SendAsync(command);
        await SendStateAsync(key);
    }

    private async Task SetThinkingAsync(string key, JsonObject message)
    {
        var entry = Find(key);
        var command = new JsonObject
        {
            ["type"] = "set_thinking_level",
            ["level"] = message["level"]?.GetValue<string>(),
        };
        await entry.Session.SendAsync(command);
        await SendStateAsync(key);
    }

    private async Task SendModelsAsync(string key)
    {
        var entry = Find(key);
        var response = await entry.Session.SendAsync(new JsonObject { ["type"] = "get_available_models" });
        var levels = await entry.Session.SendAsync(new JsonObject { ["type"] = "get_available_thinking_levels" });
        Emit(new JsonObject
        {
            ["type"] = "models",
            ["key"] = key,
            ["data"] = Data(response)?.DeepClone(),
            ["thinkingLevels"] = Data(levels)?.DeepClone(),
        });
    }

    private void RespondToExtension(string key, JsonObject message)
    {
        var entry = Find(key);
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

        // 扩展应答不产生普通 response，按请求 id 直接写。
        entry.Session.SendNoReply(response);
    }

    private async Task CloseSessionAsync(string key)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_byKey.Remove(key, out entry))
            {
                return;
            }

            if (entry.SessionPath is not null)
            {
                _keyByPath.Remove(entry.SessionPath);
            }
        }

        await entry.Session.DisposeAsync();
    }

    public async Task ShutdownAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = _byKey.Values.ToArray();
            _byKey.Clear();
            _keyByPath.Clear();
        }

        foreach (var entry in entries)
        {
            await entry.Session.DisposeAsync();
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

    private void Emit(JsonObject payload)
    {
        Outbound?.Invoke(payload.ToJsonString(SerializerOptions));
    }
}

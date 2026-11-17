using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAStation.Rpc;
using MAStation.Storage;
using PiAgentStation.Shared;

namespace MAStation;

/// <summary>
/// 对话中心。对话是 MAStation 自己的概念：存在本地数据库里，客户端只跟这里打交道。
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
    }

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, Live> _live = new();
    private readonly List<CenterConnection> _clients = new();
    private readonly ConversationStore _store;

    public CenterHub(ConversationStore store)
    {
        _store = store;
    }

    public void Join(CenterConnection connection)
    {
        lock (_gate)
        {
            _clients.Add(connection);
        }

        connection.Send(new JsonObject
        {
            ["type"] = "hello",
            ["center"] = "MAStation",
            ["version"] = typeof(CenterHub).Assembly.GetName().Version?.ToString() ?? "0",
            ["data"] = _store.DatabasePath,
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
            items.Add(new JsonObject
            {
                ["id"] = conversation.Id,
                ["title"] = conversation.Title,
                ["cwd"] = conversation.Cwd,
                ["updatedAt"] = conversation.UpdatedAt,
                ["messageCount"] = conversation.MessageCount,
            });
        }

        sender.Send(new JsonObject
        {
            ["type"] = "conversation_list",
            ["items"] = items,
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
            // Pi 那边的记录被清掉了：历史还在 MAStation，只是接不上原来那段上下文。
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
            }
            else if (name == "agent_settled")
            {
                live.Streaming = false;
                _ = PersistTurnAsync(live);
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

    /// <summary>一轮结束后把 Pi 那边的消息取回来，存成 MAStation 自己的副本。</summary>
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

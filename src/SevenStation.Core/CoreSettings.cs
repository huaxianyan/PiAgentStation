using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SevenStation.Shared;

namespace SevenStation.Core;

/// <summary>
/// 中心的配置。安装时不问 Pi 装在哪：第一次启动自己找一遍，找到什么写进这里，之后都按配置走。
/// Pi 只是目前唯一实现了的 Agent，配置按 Agent 分组存，以后接别的不用改结构。
/// </summary>
public sealed class CoreSettings
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>进程内唯一的一份，启动时装载，界面改了就重写。</summary>
    public static CoreSettings Current { get; private set; } = new();

    /// <summary>监听地址。命令行环境变量优先于它，方便开发时临时换端口。</summary>
    public string Listen { get; set; } = "http://127.0.0.1:8787";

    /// <summary>已纳入配置的 Agent，键是 Agent 标识（目前只有 <c>pi</c>）。</summary>
    public Dictionary<string, AgentSettings> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AgentSettings? Agent(string id) =>
        Agents.TryGetValue(id, out var found) ? found : null;

    /// <summary>
    /// 装载配置。文件不存在就先探测一遍本机已装的 Agent 再落盘——
    /// 这一步就是「安装后第一次启动自动把 Pi 纳进来」。探测不到也照常写文件，界面上可以再填。
    /// </summary>
    public static CoreSettings LoadOrInitialize(string path)
    {
        var exists = File.Exists(path);
        CoreSettings settings;
        if (exists)
        {
            try
            {
                settings = JsonSerializer.Deserialize<CoreSettings>(File.ReadAllText(path), Format) ?? new CoreSettings();
            }
            catch (JsonException ex)
            {
                Log.Write($"配置读不出来，按默认值重来：{ex.Message}");
                settings = new CoreSettings();
            }
        }
        else
        {
            settings = new CoreSettings();
        }

        var discovered = settings.DiscoverMissingAgents();
        if (!exists || discovered)
        {
            settings.Save(path);
            Log.Write((exists ? "配置里缺的 Agent 已补上：" : "首次启动，已生成配置：") + path);
        }

        Current = settings;
        return settings;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, Format));
    }

    /// <summary>把本机能找到、配置里还没有的 Agent 补进来。返回是否有变化。</summary>
    public bool DiscoverMissingAgents()
    {
        var changed = false;
        var pi = FindPi();
        if (pi is not null && !IsUsable(Agent("pi")))
        {
            Agents["pi"] = pi;
            Log.Write($"找到 Pi：{pi.Directory}");
            changed = true;
        }

        return changed;
    }

    private static bool IsUsable(AgentSettings? agent) =>
        agent is not null && Directory.Exists(agent.Directory);

    /// <summary>
    /// 找 Pi 的安装目录。Pi 把 agent 装在自己的家目录下，路径固定，所以按常见位置挨个看一遍即可。
    /// 环境变量 <c>PI_AGENT_DIR</c> 最高优先，服务账户下把路径指过去就能用。
    /// </summary>
    private static AgentSettings? FindPi()
    {
        var candidates = new List<string>();

        var fromEnvironment = Environment.GetEnvironmentVariable("PI_AGENT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            candidates.Add(fromEnvironment.Trim());
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            candidates.Add(Path.Combine(profile, ".pi", "agent"));
        }

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(common))
        {
            candidates.Add(Path.Combine(common, "pi", "agent"));
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            candidates.Add(Path.Combine(local, "pi", "agent"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "bin", "pi-launcher.js")))
            {
                return new AgentSettings { Directory = candidate };
            }
        }

        return null;
    }
}

/// <summary>
/// 一个 Agent 在本机的落点。<see cref="Node"/> 留给需要自己解释器的 Agent，
/// 现在只有 Pi 用得上（Windows 上要拿 node 跑 launcher）。
/// </summary>
public sealed class AgentSettings
{
    public string Directory { get; set; } = string.Empty;

    public string? Node { get; set; }
}

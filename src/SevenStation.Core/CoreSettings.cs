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

        var discovered = settings.DiscoverMissingAgents() | settings.FillMissingHomes();
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

    /// <summary>系统账户下按用户目录找一遍 Pi，最近写过的排前面。</summary>
    private static IEnumerable<(string Directory, string Home)> ProfileCandidates()
    {
        // 服务账户的家目录在 C:\Windows\system32\config 下，真正的用户目录要从 Windows 目录往上找。
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var usersRoot = Path.Combine(Path.GetDirectoryName(windows) ?? "C:", "Users");

        string[] users;
        try
        {
            users = Directory.GetDirectories(usersRoot);
        }
        catch (Exception)
        {
            yield break;
        }

        var found = new List<(string Directory, string Home, DateTime Stamp)>();
        foreach (var user in users)
        {
            var candidate = Path.Combine(user, ".pi", "agent", "bin", "pi-launcher.js");
            try
            {
                if (File.Exists(candidate))
                {
                    found.Add((Path.Combine(user, ".pi", "agent"), user, File.GetLastWriteTimeUtc(candidate)));
                }
            }
            catch (Exception)
            {
                // 别的用户目录读不了就跳过，不是错误。
            }
        }

        foreach (var item in found.OrderByDescending(entry => entry.Stamp))
        {
            yield return (item.Directory, item.Home);
        }
    }

    /// <summary>
    /// 老配置里只记了目录没记家目录。目录在某个用户的家目录下时把家目录补上——
    /// 中心作为服务跑，缺这一项 Pi 就会去找系统账户的 ~/.pi。
    /// </summary>
    public bool FillMissingHomes()
    {
        var changed = false;
        foreach (var agent in Agents.Values)
        {
            if (!string.IsNullOrWhiteSpace(agent.Home) || string.IsNullOrWhiteSpace(agent.Directory))
            {
                continue;
            }

            var home = HomeOf(agent.Directory);
            if (home is not null)
            {
                agent.Home = home;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>从 &lt;用户目录&gt;\.pigent 反推用户目录；不是这个形状就返回 null。</summary>
    private static string? HomeOf(string agentDirectory)
    {
        var pi = Path.GetDirectoryName(agentDirectory);
        if (pi is null || !string.Equals(Path.GetFileName(pi), ".pi", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var home = Path.GetDirectoryName(pi);
        return string.IsNullOrEmpty(home) || !Directory.Exists(home) ? null : home;
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
                return new AgentSettings
                {
                    Directory = candidate,
                    Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                };
            }
        }

        // 中心作为服务跑时用的是系统账户，它的家目录里没有 Pi；Pi 装在某个真实用户的家目录下。
        // 这时候挨个用户目录找一遍，挑最近动过的那个——安装完成第一次启动就是靠这一步认出 Pi。
        foreach (var (directory, home) in ProfileCandidates())
        {
            if (File.Exists(Path.Combine(directory, "bin", "pi-launcher.js")))
            {
                return new AgentSettings { Directory = directory, Home = home };
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

    /// <summary>
    /// 以哪个用户的家目录跑这个 Agent。中心作为服务跑时进程属于系统账户，
    /// 而 Pi 的配置与密钥在真实用户的家目录下，所以要显式指过去。
    /// </summary>
    public string? Home { get; set; }
}

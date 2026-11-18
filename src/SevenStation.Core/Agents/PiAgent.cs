using SevenStation.Core.Rpc;

namespace SevenStation.Core.Agents;

/// <summary>
/// Pi 作为 Agent 的名片与能力。Pi 专属的路径、版本、会话目录都留在这里，
/// 不上浮到 Host 的调度里。
/// </summary>
public static class PiAgent
{
    public static AgentDescriptor Describe()
    {
        // 服务跑在系统账户下时，进程自己的家目录是系统账户的；Pi 的子进程是按配置里的
        // 真实用户家目录起的，名片上显示的路径也得跟着那份走，否则两边对不上。
        var home = CoreSettings.Current.Agent(AgentRegistry.PiId)?.Home;
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var agentDirectory = PiLocator.AgentDirectory;
        var launcher = PiLocator.LauncherPath;
        var sessions = Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR")
            ?? Path.Combine(home, ".pi", "sessions");

        return new AgentDescriptor(
            AgentRegistry.PiId,
            "Pi",
            InstalledVersion(agentDirectory),
            File.Exists(launcher),
            new AgentCapabilities
            {
                Tools = true,
                ThinkingLevels = true,
                Models = true,
                ContextUsage = true,
                Compaction = true,
                Extensions = true,
                Skills = true,
                Commands = true,
                Attachments = true,
            },
            [
                new AgentInfoRow("版本", InstalledVersion(agentDirectory)),
                new AgentInfoRow("程序目录", agentDirectory),
                new AgentInfoRow("会话目录", sessions),
            ]);
    }

    /// <summary>装了几个版本时，报最近装的那个；读不到就留空，界面显示未知。</summary>
    private static string InstalledVersion(string agentDirectory)
    {
        try
        {
            var releases = Path.Combine(agentDirectory, "install", "releases");
            if (!Directory.Exists(releases))
            {
                return "";
            }

            return Directory.GetDirectories(releases)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? "";
        }
        catch
        {
            return "";
        }
    }
}

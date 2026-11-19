using System.Diagnostics;
using System.IO;
using SevenStation.Core.Agents;

namespace SevenStation.Core.Rpc;

/// <summary>
/// 定位本机已安装的 Pi。不打包自己的 Pi，只用 ~/.pi/agent 下那一份。
/// Windows 上 ~/.pi/agent/bin/pi.cmd 与 sh 脚本都不能被 CreateProcess 直接执行，
/// 所以统一走 node + pi-launcher.js，由 launcher 自己解析当前版本目录。
/// </summary>
public static class PiLocator
{
    /// <summary>配置里记下的 Pi 目录；没配过就是空串，<see cref="EnsureInstalled"/> 会给出提示。</summary>
    public static string AgentDirectory => CoreSettings.Current.Agent(AgentRegistry.PiId)?.Directory ?? string.Empty;

    public static string LauncherPath => string.IsNullOrEmpty(AgentDirectory)
        ? string.Empty
        : Path.Combine(AgentDirectory, "bin", "pi-launcher.js");


    /// <summary>
    /// 这份 Pi 认不认 <c>--append-system-prompt</c>。老版本遇到不认识的参数会直接报错退出，
    /// 所以先问一次 <c>--help</c>；结果缓存，一个进程只问一次。
    /// </summary>
    public static bool SupportsAppendSystemPrompt { get; } = ProbeAppendSystemPrompt();

    private static bool ProbeAppendSystemPrompt()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ResolveNode())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { LauncherPath, "--help" },
            });
            if (process is null) return false;

            var text = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(4000) && text.Result.Contains("--append-system-prompt", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Node 解释器。优先环境变量覆盖，其次 PATH 上的 node，最后是 Pi 自带的 node 副本。</summary>
    public static string ResolveNode()
    {
        var configured = CoreSettings.Current.Agent(AgentRegistry.PiId)?.Node;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var overridePath = Environment.GetEnvironmentVariable("PI_GUI_NODE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var managed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pi-node", "current", "node.exe");
        if (File.Exists(managed))
        {
            return managed;
        }

        // PATH 上的 node：交给系统解析，找不到时 Start 会抛异常并由调用方报给界面。
        return "node";
    }

    public static void EnsureInstalled()
    {
        if (string.IsNullOrEmpty(AgentDirectory))
        {
            throw new InvalidOperationException(
                "还没有配置 Pi。在 Seven Station Core 的设置里填上 Pi 的安装目录，或设置环境变量 PI_AGENT_DIR 后重启 Seven Station Core。");
        }

        if (!File.Exists(LauncherPath))
        {
            throw new FileNotFoundException(
                $"配置里的 Pi 目录下没有启动器：{LauncherPath}。检查目录是否被删掉或换过盘。",
                LauncherPath);
        }
    }

    public static string Describe()
    {
        return $"node={ResolveNode()}; launcher={LauncherPath}";
    }

    public static bool TryStartProbe(out string detail)
    {
        try
        {
            EnsureInstalled();
            detail = Describe();
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }
}

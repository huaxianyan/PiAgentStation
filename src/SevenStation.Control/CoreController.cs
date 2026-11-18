using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SevenStation.Control;

/// <summary>界面上要显示的一份状态快照。派生量（地址、范围）都在这儿算好，前端不做判断。</summary>
internal sealed record Snapshot
{
    public required bool ServiceInstalled { get; init; }
    public required bool Running { get; init; }
    public required string Mode { get; init; }          // service / portable / stopped
    public required string Listen { get; init; }
    public required string Token { get; init; }
    public required string DataDirectory { get; init; }
    public required string Version { get; init; }
    public required bool Autostart { get; init; }
    public required string Scope { get; init; }         // local / lan
    public required bool FirewallRule { get; init; }
    public required string[] Logs { get; init; }
    public required bool Elevated { get; init; }
    public required string Since { get; init; }
}

internal static class CoreController
{
    private const string ServiceName = "SevenStationCore";
    private const string FirewallRule = "Seven Station 中心";
    private const string CoreExeName = "SevenStation.Core.exe";

    /// <summary>要提权才能做的事，单独走一次 UAC；子进程按这个参数执行一次就退出。</summary>
    public const string ElevateFlag = "--do";

    public static string DataHome => SevenStation.Shared.ProductPaths.CoreHome;

    private static string SettingsFile => Path.Combine(DataHome, "settings.json");
    private static string TokenFile => Path.Combine(DataHome, "token.txt");
    private static string LogFile => Path.Combine(DataHome, "logs", "center.log");

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static Snapshot Read()
    {
        var installed = ServiceExists();
        var running = installed ? ServiceState() == ServiceControllerStatus.Running : PortableRunning();
        var listen = ReadListen();
        var port = PortOf(listen);

        return new Snapshot
        {
            ServiceInstalled = installed,
            Running = running,
            Mode = installed ? (running ? "service" : "stopped") : (running ? "portable" : "stopped"),
            Listen = listen,
            Token = ReadToken(),
            DataDirectory = DataHome,
            Version = typeof(CoreController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            Autostart = installed && ServiceStartType() == ServiceStartMode.Automatic,
            Scope = IsLan(listen) ? "lan" : "local",
            FirewallRule = HasFirewallRule(port),
            Logs = TailLog(6),
            Elevated = IsElevated,
            Since = SinceText(),
        };
    }

    // ------------------------------ 服务与进程 ------------------------------

    private static bool ServiceExists()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static ServiceControllerStatus ServiceState()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return ServiceControllerStatus.Stopped;
        }
    }

    private static ServiceStartMode ServiceStartType()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.StartType;
        }
        catch (Exception)
        {
            return ServiceStartMode.Manual;
        }
    }

    /// <summary>便携模式：中心是我们自己拉起来的子进程，靠 exe 名认。</summary>
    private static bool PortableRunning() => Portable() is not null;

    private static Process? Portable() =>
        Process.GetProcessesByName("SevenStation.Core").FirstOrDefault();

    public static string Start()
    {
        if (ServiceExists() && !IsElevated)
        {
            return Elevate("start");
        }

        if (ServiceExists())
        {
            StartServiceRaw();
            return string.Empty;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, CoreExeName);
        if (!File.Exists(exe))
        {
            return "没找到 " + CoreExeName + "，它应该和控制台放在同一个目录里。";
        }

        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(start);
        return string.Empty;
    }

    public static string Stop()
    {
        if (ServiceExists() && !IsElevated)
        {
            return Elevate("stop");
        }

        if (ServiceExists())
        {
            StopServiceRaw();
            return string.Empty;
        }

        var process = Portable();
        if (process is null)
        {
            return string.Empty;
        }

        process.Kill(entireProcessTree: true);
        process.WaitForExit(10_000);
        return string.Empty;
    }

    public static string Restart()
    {
        if (ServiceExists() && !IsElevated)
        {
            return Elevate("restart");
        }

        if (!ServiceExists())
        {
            Stop();
            return Start();
        }

        StopServiceRaw();
        StartServiceRaw();
        return string.Empty;
    }

    private static void StartServiceRaw()
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status != ServiceControllerStatus.Running)
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }
    }

    private static void StopServiceRaw()
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status != ServiceControllerStatus.Stopped)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>开机自启就是服务的启动类型，改它要管理员。</summary>
    public static string SetAutostart(bool on)
    {
        if (!ServiceExists())
        {
            return "还没装成服务，便携模式没有开机自启。";
        }

        if (!IsElevated)
        {
            return Elevate(on ? "autostart-on" : "autostart-off");
        }

        Run("sc.exe", $"config {ServiceName} start= {(on ? "auto" : "demand")}");
        return string.Empty;
    }

    // ------------------------------ 监听范围与防火墙 ------------------------------

    public static string SetScope(string scope)
    {
        var port = PortOf(ReadListen());
        var listen = scope == "lan" ? $"http://0.0.0.0:{port}" : $"http://127.0.0.1:{port}";
        WriteListen(listen);

        if (scope != "lan")
        {
            return Restart();
        }

        if (!HasFirewallRule(port))
        {
            if (!IsElevated)
            {
                return Elevate($"firewall {port}");
            }

            AddFirewallRule(port);
        }

        return Restart();
    }

    private static void AddFirewallRule(int port) =>
        Run("netsh.exe",
            $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain");

    private static bool HasFirewallRule(int port)
    {
        var output = Run("netsh.exe", $"advfirewall firewall show rule name=\"{FirewallRule}\"");
        return output.Contains("LocalPort", StringComparison.OrdinalIgnoreCase)
            && output.Contains(port.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------ 令牌 ------------------------------

    public static string RegenerateToken()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .ToLowerInvariant();
        Directory.CreateDirectory(DataHome);
        File.WriteAllText(TokenFile, token);
        // 中心只在启动时读令牌，改完得让它重新起一次，否则新令牌不生效。
        return Restart();
    }

    private static string ReadToken()
    {
        try
        {
            return File.Exists(TokenFile) ? File.ReadAllText(TokenFile).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    // ------------------------------ 配置与日志 ------------------------------

    private static string ReadListen()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return "http://127.0.0.1:8787";
            }

            if (JsonNode.Parse(File.ReadAllText(SettingsFile)) is JsonObject settings
                && settings["Listen"]?.GetValue<string>() is { Length: > 0 } listen)
            {
                return listen;
            }
        }
        catch (Exception)
        {
            // 配置坏了按默认值显示，改配置会覆盖它。
        }

        return "http://127.0.0.1:8787";
    }

    private static void WriteListen(string listen)
    {
        Directory.CreateDirectory(DataHome);
        JsonObject settings = new();
        try
        {
            if (File.Exists(SettingsFile)
                && JsonNode.Parse(File.ReadAllText(SettingsFile)) is JsonObject existing)
            {
                settings = existing;
            }
        }
        catch (Exception)
        {
            // 解析不了就重写一份新的，反正只有 Listen 和 Agents 两项。
        }

        settings["Listen"] = listen;
        File.WriteAllText(SettingsFile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int PortOf(string listen)
    {
        return Uri.TryCreate(listen, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : 8787;
    }

    private static bool IsLan(string listen) =>
        listen.Contains("0.0.0.0", StringComparison.Ordinal)
        || listen.Contains("[::]", StringComparison.Ordinal)
        || listen.Contains("*", StringComparison.Ordinal);

    private static string[] TailLog(int lines)
    {
        try
        {
            if (!File.Exists(LogFile))
            {
                return [];
            }

            using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return all.TakeLast(lines).Select(line => line.TrimEnd('\r')).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// 服务跑在系统账户下，普通权限读不到它的 StartTime，所以从日志里的「中心启动」反推；
    /// 便携模式是我们自己的子进程，直接读进程时间。
    /// </summary>
    private static string SinceText()
    {
        DateTime? since = null;
        if (ServiceExists())
        {
            try
            {
                var line = TailLog(400).LastOrDefault(entry => entry.Contains("中心启动", StringComparison.Ordinal));
                if (line is not null)
                {
                    var head = line.Length >= 23 ? line[..23] : string.Empty;
                    if (DateTime.TryParse(head, out var parsed))
                    {
                        since = parsed;
                    }
                }
            }
            catch (Exception)
            {
                // 日志格式变了就算了，不显示运行时长也不影响用。
            }
        }
        else if (Portable() is { } process)
        {
            try
            {
                since = process.StartTime;
            }
            catch (Exception)
            {
                since = null;
            }
        }

        if (since is not { } start)
        {
            return string.Empty;
        }

        var span = DateTime.Now - start;
        if (span.TotalHours >= 1)
        {
            return $"已运行 {(int)span.TotalHours} 小时 {span.Minutes} 分";
        }

        return span.TotalMinutes >= 1 ? $"已运行 {(int)span.TotalMinutes} 分钟" : "刚启动";
    }

    // ------------------------------ 提权 ------------------------------

    /// <summary>需要管理员时把自己再拉起来一次，做完就退；返回空串表示做完了，否则是要显示的话。</summary>
    private static string Elevate(string arguments)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? string.Empty)
            {
                Arguments = $"{ElevateFlag} {arguments}",
                UseShellExecute = true,
                Verb = "runas",
            };
            using var child = Process.Start(start);
            child?.WaitForExit(30_000);
            return string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "没拿到管理员权限，这一步没做成。";
        }
    }

    /// <summary>提权子进程的入口：做一件事就退。</summary>
    public static int RunElevated(string[] args)
    {
        if (args.Length < 2)
        {
            return 2;
        }

        switch (args[0])
        {
            case "start":
                StartServiceRaw();
                return 0;
            case "stop":
                StopServiceRaw();
                return 0;
            case "restart":
                StopServiceRaw();
                StartServiceRaw();
                return 0;
            case "autostart-on":
                Run("sc.exe", $"config {ServiceName} start= auto");
                return 0;
            case "autostart-off":
                Run("sc.exe", $"config {ServiceName} start= demand");
                return 0;
            case "firewall":
                if (int.TryParse(args[1], out var port))
                {
                    AddFirewallRule(port);
                }

                return 0;
            default:
                return 3;
        }
    }

    private static string Run(string file, string arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);
            return output;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

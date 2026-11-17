using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PiAgentStation.Shared;

namespace MAStation;

/// <summary>
/// 一条对话背后的常驻 shell。
/// 走 <c>pwsh -Command -</c>：命令从标准输入一行一行喂进去，变量与工作目录都能延续，
/// 不需要 PTY，也不用在前端塞一个终端模拟器。代价是交互式全屏程序（vim、top）没法用。
/// </summary>
internal sealed partial class TerminalSession : IDisposable
{
    private const int MaxBuffer = 64 * 1024;

    private readonly Process _process;
    private readonly StringBuilder _tail = new();
    private readonly object _gate = new();

    public TerminalSession(string cwd, Action<string> onOutput, Action<int> onExit)
    {
        Cwd = cwd;
        ShellName = OperatingSystem.IsWindows() ? "PowerShell" : "bash";
        var (shell, arguments) = ResolveShell();

        var info = new ProcessStartInfo
        {
            FileName = shell,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Emit(e.Data, onOutput);
        _process.ErrorDataReceived += (_, e) => Emit(e.Data, onOutput);
        _process.Exited += (_, _) => onExit(_process.ExitCode);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        if (OperatingSystem.IsWindows())
        {
            // 中文与换行都按 UTF-8 走，不然 GBK 混进来就是乱码。
            Write("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            Write("Write-Output ('PowerShell ' + $PSVersionTable.PSVersion + '  ' + (Get-Location).Path)");
        }
        else
        {
            Write("echo \"bash $BASH_VERSION  $PWD\"");
        }
    }

    /// <summary>给界面看的名字。Windows 上是 PowerShell，其它平台是 bash。</summary>
    public string ShellName { get; }

    public string Cwd { get; set; }

    public bool Running => !_process.HasExited;

    public string Tail
    {
        get
        {
            lock (_gate)
            {
                return _tail.ToString();
            }
        }
    }

    public void Write(string line)
    {
        if (_process.HasExited)
        {
            return;
        }

        try
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            Log.Write("终端写入失败", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(1500))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // 关掉就算了。
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>
    /// 优先 pwsh（7+），没有就退回系统自带的 powershell；非 Windows 用 bash 读标准输入。
    /// </summary>
    private static (string Shell, string[] Arguments) ResolveShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("/bin/bash", new[] { "-s" });
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var hasPwsh = path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, "pwsh.exe")));

        var shell = hasPwsh ? "pwsh" : "powershell.exe";
        return (shell, new[] { "-NoProfile", "-NoLogo", "-NonInteractive", "-Command", "-" });
    }

    private void Emit(string? line, Action<string> onOutput)
    {
        if (line is null)
        {
            return;
        }

        var text = StripAnsi(line) + "\n";
        lock (_gate)
        {
            _tail.Append(text);
            if (_tail.Length > MaxBuffer)
            {
                _tail.Remove(0, _tail.Length - MaxBuffer);
            }
        }

        onOutput(text);
    }

    /// <summary>把颜色与光标这类控制序列去掉：界面是纯文本，留着只会变成乱码。</summary>
    private static string StripAnsi(string text)
    {
        var stripped = CsiSequence().Replace(text, string.Empty);
        stripped = OscSequence().Replace(stripped, string.Empty);
        return stripped.Replace("\r", string.Empty);
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex CsiSequence();

    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(\x07|\x1B\\)")]
    private static partial Regex OscSequence();
}

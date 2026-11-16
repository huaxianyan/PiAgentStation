using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace PiGui.Rpc;

/// <summary>
/// 一个 <c>pi --mode rpc</c> 子进程。协议是严格 JSONL：一行一条记录，只按 LF 切分。
/// stdout 只走协议记录，stderr 只作诊断，不参与解析。
/// </summary>
public sealed class PiSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _writeLock = new();
    private int _nextId;
    private volatile bool _disposed;

    /// <summary>收到的每一条 stdout 记录（响应之外的会话事件与扩展请求）。</summary>
    public event Action<JsonObject>? RecordReceived;

    /// <summary>子进程退出。参数为退出码与 stderr 尾部。</summary>
    public event Action<int, string>? Exited;

    public string WorkingDirectory { get; }
    public string? SessionPath { get; }
    public bool IsRunning => !_disposed && !_process.HasExited;

    private PiSession(Process process, string workingDirectory, string? sessionPath)
    {
        _process = process;
        WorkingDirectory = workingDirectory;
        SessionPath = sessionPath;
    }

    public static PiSession Start(string workingDirectory, string? sessionPath)
    {
        PiLocator.EnsureInstalled();
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{workingDirectory}");
        }

        var psi = new ProcessStartInfo(PiLocator.ResolveNode())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(PiLocator.LauncherPath);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("rpc");
        if (!string.IsNullOrEmpty(sessionPath))
        {
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add(sessionPath);
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Pi 子进程。");

        var session = new PiSession(process, workingDirectory, sessionPath);
        session.ReadStdout();
        session.ReadStderr();
        return session;
    }

    private void ReadStdout()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var reader = _process.StandardOutput;
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    JsonObject? record;
                    try
                    {
                        record = JsonNode.Parse(line) as JsonObject;
                    }
                    catch
                    {
                        continue;
                    }

                    if (record is null)
                    {
                        continue;
                    }

                    if (record["type"]?.GetValue<string>() == "response")
                    {
                        var id = record["id"]?.GetValue<string>();
                        if (id is not null && _pending.TryRemove(id, out var tcs))
                        {
                            tcs.TrySetResult(record);
                            continue;
                        }

                        // 没有 id 的响应（例如解析失败）也交给上层显示。
                        RecordReceived?.Invoke(record);
                        continue;
                    }

                    RecordReceived?.Invoke(record);
                }
            }
            catch (Exception ex)
            {
                _stderr.Append("[stdout 读取失败] ").Append(ex.Message).Append('\n');
            }

            int code = -1;
            try
            {
                if (_process.WaitForExit(5000))
                {
                    code = _process.ExitCode;
                }
            }
            catch
            {
                // 进程可能已被释放。
            }

            foreach (var kv in _pending)
            {
                kv.Value.TrySetException(new InvalidOperationException("Pi 子进程已退出。"));
            }

            _pending.Clear();
            Exited?.Invoke(code, StderrTail());
        });
    }

    private void ReadStderr()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var reader = _process.StandardError;
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    AppendStderr(line);
                }
            }
            catch
            {
                // 进程退出时正常发生。
            }
        });
    }

    private void AppendStderr(string line)
    {
        lock (_stderr)
        {
            _stderr.Append(line).Append('\n');
            if (_stderr.Length > 32_000)
            {
                _stderr.Remove(0, _stderr.Length - 16_000);
            }
        }
    }

    public string StderrTail()
    {
        lock (_stderr)
        {
            var lines = _stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join('\n', lines.TakeLast(20));
        }
    }

    /// <summary>发一条命令并等它的 response。事件不受影响，继续从 <see cref="RecordReceived"/> 流出。</summary>
    public async Task<JsonObject> SendAsync(JsonObject command, TimeSpan? timeout = null)
    {
        if (_disposed)
        {
            throw new InvalidOperationException("会话已关闭。");
        }

        var id = "req-" + Interlocked.Increment(ref _nextId).ToString();
        command["id"] = id;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            lock (_writeLock)
            {
                _process.StandardInput.WriteLine(command.ToJsonString());
                _process.StandardInput.Flush();
            }
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException($"写入 Pi 子进程失败：{ex.Message}", ex);
        }

        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(limit)).ConfigureAwait(false);
        if (finished != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"Pi 在 {limit.TotalSeconds:0} 秒内没有响应 {command["type"]}。");
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>写一条不期待 response 的记录（扩展 UI 应答等）。</summary>
    public void SendNoReply(JsonObject record)
    {
        if (_disposed)
        {
            return;
        }

        lock (_writeLock)
        {
            _process.StandardInput.WriteLine(record.ToJsonString());
            _process.StandardInput.Flush();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // 关 stdin 请求有序退出；Pi 处理完手头工作再退出，所以给一段时间再强杀。
            _process.StandardInput.Close();
        }
        catch
        {
            // 已退出。
        }

        await Task.Run(() =>
        {
            try
            {
                if (!_process.WaitForExit(8000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 已退出。
            }
        }).ConfigureAwait(false);

        _process.Dispose();
    }
}

using System.IO;

namespace SevenStation.Shared;

/// <summary>
/// 极简运行日志。两个进程都没有 Seven Station Control，出问题时只能靠文件留证据。
/// 每个进程启动时调一次 <see cref="Initialize"/>，各写各的目录，超过 1 MB 时截断重来。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _path = DefaultPath();

    public static string FilePath => _path;

    /// <summary>例：<c>Log.Initialize(ProductPaths.CoreLogs, "center.log")</c>。传的是绝对目录。</summary>
    public static void Initialize(string directory, string fileName = "app.log")
    {
        _path = Path.Combine(directory, fileName);
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var info = new FileInfo(_path);
                if (info.Exists && info.Length > 1024 * 1024)
                {
                    File.Delete(_path);
                }

                File.AppendAllText(
                    _path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写不了就算了，不能因为日志再次崩溃。
        }
    }

    public static void Write(string message, Exception? exception)
    {
        Write(exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");
    }

    private static string DefaultPath() =>
        Path.Combine(ProductPaths.ClientHome, "logs", "app.log");
}

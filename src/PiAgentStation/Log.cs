using System.IO;

namespace PiAgentStation;

/// <summary>
/// 极简运行日志。这个进程是 WinExe，没有控制台，出问题时只能靠文件留证据。
/// 位置：%LOCALAPPDATA%\PiAgentStation\logs\app.log，超过 1 MB 时截断重来。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PiAgentStation", "logs", "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1024 * 1024)
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(
                    FilePath,
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
}

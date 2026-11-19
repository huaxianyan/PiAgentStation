using Microsoft.Extensions.Logging;
using FileLog = SevenStation.Shared.Log;

namespace SevenStation.Core;

/// <summary>
/// 把框架日志（Kestrel、中间件、异常）写进同一个文件。
/// 这个 Seven Station Core 是个后台服务，出问题时没人盯着 Seven Station Control。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;

        public FileLogger(string category)
        {
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            FileLog.Write($"[{logLevel}] {_category}: {formatter(state, exception)}", exception);
        }
    }
}

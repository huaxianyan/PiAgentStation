using System.Windows;
using System.Windows.Threading;

namespace PiAgentStation;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Write($"启动：{Environment.ProcessPath}");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("界面线程未处理异常", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write("未处理异常", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("未观察的任务异常", args.Exception);
        };

        base.OnStartup(e);
    }
}

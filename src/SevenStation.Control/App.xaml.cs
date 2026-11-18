using System.IO;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace SevenStation.Control;

public partial class App : Application
{
    private const string InstanceId = "SevenStation.Control.SingleInstance";
    private Mutex? _mutex;

    private static void Trace(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SevenStation.Control");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "control.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}" + (char)10);
        }
        catch (IOException)
        {
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Trace("未处理：" + args.ExceptionObject);
        Trace("启动，参数 " + string.Join(' ', e.Args));
        // 提权子进程：做一件事就退，不建窗口。
        if (e.Args.Length >= 2 && e.Args[0] == CoreController.ElevateFlag)
        {
            Shutdown(CoreController.RunElevated(e.Args[1..]));
            return;
        }

        _mutex = new Mutex(true, InstanceId, out var first);
        Trace("互斥量 first=" + first);
        if (!first)
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Seven Station 中心控制台");
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Trace("退出，代码 " + e.ApplicationExitCode);
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

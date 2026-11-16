using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace PiGui;

public partial class MainWindow : Window
{
    private readonly AppBridge _bridge = new();
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        _bridge.Outbound += SendToWeb;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PiGui", "WebView2");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(environment);

            var core = Web.CoreWebView2;
            var assets = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping(
                "app.local", assets, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += OnNewWindowRequested;
            core.Navigate("https://app.local/index.html");
            Log.Write($"界面已加载：资源目录 {assets}");
        }
        catch (Exception ex)
        {
            Log.Write("界面没能启动", ex);
            MessageBox.Show(
                $"界面没能启动：{ex.Message}",
                "Pi",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.WebMessageAsJson;
        try
        {
            // 选目录要用系统对话框，只能在 UI 线程做，所以这条不进桥。
            if (JsonNode.Parse(json) is JsonObject message && message["type"]?.GetValue<string>() == "pick_folder")
            {
                PickFolder(message);
                return;
            }
        }
        catch
        {
            // 交给桥按普通消息处理。
        }

        _bridge.Handle(json);
    }

    private void PickFolder(JsonObject message)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择工作目录",
            Multiselect = false,
        };

        var initial = message["initial"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        var picked = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        SendToWeb(new JsonObject
        {
            ["type"] = "folder_picked",
            ["path"] = picked,
        }.ToJsonString());
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private static void OpenExternal(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，不该因此崩界面。
        }
    }

    private void SendToWeb(string json)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SendToWeb(json));
            return;
        }

        try
        {
            Web.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch
        {
            // 控件已释放。
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            _closing = true;
            try
            {
                // 关 stdin 让每个 Pi 子进程自己收尾，别留下孤儿进程。
                await _bridge.ShutdownAsync();
            }
            catch
            {
                // 退出路径上不报错。
            }

            Close();
        }

        base.OnClosing(e);
    }
}

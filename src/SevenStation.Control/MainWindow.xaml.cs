using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SevenStation.Control;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await StartAsync();
        Closing += (_, args) =>
        {
            if (_quitting)
            {
                return;
            }

            // 关窗口只是收起来，中心照常跑。
            args.Cancel = true;
            WindowState = WindowState.Minimized;
        };
        _poll.Tick += (_, _) =>
        {
            if (IsVisible)
            {
                PushState();
            }
        };
    }

    private async Task StartAsync()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SevenStation.Control");
        Directory.CreateDirectory(dataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
        await View.EnsureCoreWebView2Async(environment);

        var page = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        View.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "sevenstation.control", page, CoreWebView2HostResourceAccessKind.Allow);
        View.CoreWebView2.WebMessageReceived += OnWebMessage;
        View.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        View.CoreWebView2.Settings.IsStatusBarEnabled = false;
        View.CoreWebView2.Navigate("https://sevenstation.control/index.html");

        _poll.Start();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        PushState();
    }

    private void Quit()
    {
        _quitting = true;
        System.Windows.Application.Current.Shutdown();
    }

    // ------------------------------ 前端消息 ------------------------------

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject message;
        try
        {
            message = JsonNode.Parse(e.WebMessageAsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return;
        }

        var type = message["type"]?.GetValue<string>() ?? string.Empty;
        switch (type)
        {
            case "ready":
                PushState();
                break;

            case "start":
            case "stop":
            case "restart": {
                var error = type switch
                {
                    "start" => CoreController.Start(),
                    "stop" => CoreController.Stop(),
                    _ => CoreController.Restart(),
                };
                if (error.Length > 0)
                {
                    PushToast(error);
                }

                PushState();
                break;
            }

            case "autostart": {
                var on = message["on"]?.GetValue<bool>() ?? false;
                var error = CoreController.SetAutostart(on);
                if (error.Length > 0)
                {
                    PushToast(error);
                }

                PushState();
                break;
            }

            case "scope": {
                var scope = message["scope"]?.GetValue<string>() ?? "local";
                var error = CoreController.SetScope(scope);
                if (error.Length > 0)
                {
                    PushToast(error);
                }

                PushState();
                break;
            }

            case "regenerate": {
                var error = CoreController.RegenerateToken();
                PushToast(error.Length > 0 ? error : "换了新令牌，已经连着的设备要重新填一次。");
                PushState();
                break;
            }

            case "copy":
                Copy(message["text"]?.GetValue<string>() ?? string.Empty);
                break;

            case "open":
                Open(message["target"]?.GetValue<string>() ?? string.Empty);
                break;

            case "minimize":
                WindowState = WindowState.Minimized;
                break;

            case "close":
                WindowState = WindowState.Minimized;
                break;
        }
    }

    private void PushState()
    {
        var state = CoreController.Read();
        var payload = new JsonObject
        {
            ["type"] = "state",
            ["state"] = new JsonObject
            {
                ["serviceInstalled"] = state.ServiceInstalled,
                ["running"] = state.Running,
                ["mode"] = state.Mode,
                ["listen"] = state.Listen,
                ["token"] = state.Token,
                ["dataDirectory"] = state.DataDirectory,
                ["version"] = state.Version,
                ["autostart"] = state.Autostart,
                ["scope"] = state.Scope,
                ["firewallRule"] = state.FirewallRule,
                ["elevated"] = state.Elevated,
                ["since"] = state.Since,
                ["logs"] = new JsonArray(state.Logs.Select(line => (JsonNode)JsonValue.Create(line)!).ToArray()),
            },
        };

        var json = payload.ToJsonString();
        View.CoreWebView2?.PostWebMessageAsJson(json);

    }

    private void PushToast(string text) =>
        View.CoreWebView2?.PostWebMessageAsJson(
            new JsonObject { ["type"] = "toast", ["text"] = text }.ToJsonString());

    private static string ShortAddress(Snapshot state) =>
        state.Listen.Replace("http://", string.Empty).TrimEnd('/');

    private void Copy(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            PushToast("已复制");
        }
        catch (Exception)
        {
            PushToast("剪贴板被别的程序占着，再试一次。");
        }
    }

    private void Open(string target)
    {
        var state = CoreController.Read();
        var (file, arguments) = target switch
        {
            "url" => ("cmd.exe", $"/c start \"\" \"{state.Listen.TrimEnd('/')}/?token={state.Token}\""),
            "data" => ("explorer.exe", state.DataDirectory),
            "logs" => ("explorer.exe", Path.Combine(state.DataDirectory, "logs")),
            _ => (string.Empty, string.Empty),
        };

        if (file.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
        }
        catch (Exception)
        {
            PushToast("打不开，路径可能已经不在了。");
        }
    }
}

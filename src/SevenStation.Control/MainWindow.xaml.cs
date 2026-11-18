using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SevenStation.Control;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly H.NotifyIcon.TaskbarIcon _tray = (H.NotifyIcon.TaskbarIcon)System.Windows.Application.Current.Resources["Tray"];
    private bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => RestoreFrame();
        BuildTray();
        Loaded += async (_, _) => await StartAsync();
        Closing += (_, args) =>
        {
            if (_quitting)
            {
                return;
            }

            // 关窗口只是收进托盘，中心照常跑；真退出走托盘右键。
            args.Cancel = true;
            Hide();
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

    /// <summary>
    /// 无边框窗口默认没有窗口阴影，跟别的软件叠在一起看不出边界。
    /// 把 WS_CAPTION / WS_THICKFRAME 加回去，DWM 才会画阴影和描边；
    /// 客户区仍由 WindowChrome 铺满整窗，看不到系统标题栏。
    /// </summary>
    private void RestoreFrame()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, -16 /* GWL_STYLE */);
        SetWindowLong(handle, -16, style | 0x00C00000 /* WS_CAPTION */ | 0x00040000 /* WS_THICKFRAME */);

        // 让 DWM 自己画非客户区，窗口阴影跟着一起回来。
        var enabled = 1; // DWMNCRP_ENABLED
        DwmSetWindowAttribute(handle, 2 /* DWMWA_NCRENDERING_POLICY */, ref enabled, sizeof(int));

        // 改完风格要通知 DWM 重算边框，不然阴影不会出现。
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */ | 0x0004 /* SWP_NOZORDER */ | 0x0020 /* SWP_FRAMECHANGED */);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    /// <summary>把拖动交给系统：模拟按住标题栏，系统自己跑移动循环，贴边吸附也一起有了。</summary>
    private void StartWindowDrag()
    {
        var handle = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(handle, 0x00A1 /* WM_NCLBUTTONDOWN */, 0x0002 /* HTCAPTION */, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, int wParam, IntPtr lParam);

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
        _tray.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // ------------------------------ 托盘 ------------------------------

    private void BuildTray()
    {
        _tray.IconSource = TrayIcon.Load();
        _tray.TrayLeftMouseUp += (_, _) => ShowWindow();
        _tray.ContextMenu = BuildTrayMenu();

        // 资源里的 TaskbarIcon 不在可视树上，得自己让它真正挂到通知区域。
        _tray.ForceCreate();
        App.Trace("托盘 IsCreated=" + _tray.IsCreated);
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        var headline = new MenuItem { IsEnabled = false };
        var toggle = new MenuItem();
        toggle.Click += (_, _) =>
        {
            var state = CoreController.Read();
            var error = state.Running ? CoreController.Stop() : CoreController.Start();
            if (error.Length > 0)
            {
                ShowWindow();
                PushToast(error);
            }
            else
            {
                PushState();
            }
        };

        var openUrl = new MenuItem { Header = "打开访问地址" };
        openUrl.Click += (_, _) => Open("url");

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => Quit();

        menu.Items.Add(headline);
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("打开控制台", ShowWindow));
        menu.Items.Add(toggle);
        menu.Items.Add(openUrl);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        menu.Opened += (_, _) =>
        {
            var state = CoreController.Read();
            headline.Header = state.Running ? "正在运行 · " + ShortAddress(state) : "已停止";
            toggle.Header = state.Running ? "停止中心" : "启动中心";
            openUrl.Visibility = state.Running ? Visibility.Visible : Visibility.Collapsed;
        };

        return menu;
    }

    private static MenuItem Entry(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
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

            case "drag":
                StartWindowDrag();
                break;
            case "toggle-max":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case "close":
                Hide();
                break;
        }
    }

    private void PushState()
    {
        var state = CoreController.Read();
        _tray.ToolTipText = state.Running ? "Seven Station 中心 · 正在运行" : "Seven Station 中心 · 已停止";
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

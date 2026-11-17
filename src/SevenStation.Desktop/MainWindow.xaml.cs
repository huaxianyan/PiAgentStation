using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using SevenStation.Shared;

namespace SevenStation.Desktop;

/// <summary>
/// 桌面客户端只是一层壳：连中心、把中心发来的页面显示出来。
/// Pi 子进程与会话都在中心那边，这个进程不碰。
/// </summary>
public partial class MainWindow : Window
{
    private bool _showingOffline;

    private IntPtr _hwnd;

    private bool _pickingFolder;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => PostWindowState();
    }

    /// <summary>
    /// 窗口没有系统标题栏：最大化要自己收进工作区（别盖住任务栏），
    /// 边框由窗口自己的缩放边缘负责，所以这里只处理最大化的边界。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _hwnd = source.Handle;
            source.AddHook(WindowProc);
            RoundWindowCorners(_hwnd);
            EnableNativeShadow(_hwnd);
        }
    }

    /// <summary>
    /// 自绘边框的窗口默认没有系统阴影，跟桌面糊成一片。给 DWM 留 1 像素边框，
    /// 它会照常画投影和一圈边线，窗口边界就清楚了。
    /// </summary>
    private void EnableNativeShadow(IntPtr hwnd)
    {
        try
        {
            var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch (Exception ex)
        {
            Log.Write("窗口阴影没开出来", ex);
        }
    }

    /// <summary>
    /// 圆角：Windows 11 有 DWM 这个属性，一句就行。
    /// Windows 10 没有这个属性，也不给窗口裁区域——裁出来的边缘是硬切的，有锯齿，
    /// 留给壳层的方案再定。
    /// </summary>
    private void RoundWindowCorners(IntPtr hwnd)
    {
        const int DwmwaWindowCornerPreference = 33;
        const int DwmwcpRound = 2;

        try
        {
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Write("窗口圆角设置不上", ex);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;

        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return IntPtr.Zero;
        }

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition.X = (int)Math.Round((info.Work.Left - info.Monitor.Left) / scaleX);
        minMax.MaxPosition.Y = (int)Math.Round((info.Work.Top - info.Monitor.Top) / scaleY);
        minMax.MaxSize.X = (int)Math.Round((info.Work.Right - info.Work.Left) / scaleX);
        minMax.MaxSize.Y = (int)Math.Round((info.Work.Bottom - info.Work.Top) / scaleY);
        Marshal.StructureToPtr(minMax, lParam, true);

        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>把当前是不是最大化告诉页面，好让自己画的按钮换成还原图标。</summary>
    private void PostWindowState()
    {
        try
        {
            Web?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "window-state",
                maximized = WindowState == WindowState.Maximized,

                // 中心在本机时，选目录才轮得到系统的对话框：选出来的是这台机器的路径。
                local = CenterUrl.IsLocal,
            }));
        }
        catch (Exception ex)
        {
            Log.Write("窗口状态发不出去", ex);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowBounds();
        await StartWebViewAsync();
    }

    private void RestoreWindowBounds()
    {
        try
        {
            var path = Path.Combine(CenterUrl.DirectoryPath, "window.json");
            if (!File.Exists(path))
            {
                return;
            }

            var saved = System.Text.Json.JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(path));
            if (saved is null || saved.Width < MinWidth || saved.Height < MinHeight)
            {
                return;
            }

            // 换了显示器就可能有坐标落在屏幕外，先确认还能看见。
            var left = saved.Left;
            var top = saved.Top;
            var visible = left >= SystemParameters.VirtualScreenLeft - saved.Width + 120
                && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120
                && top >= SystemParameters.VirtualScreenTop - 40
                && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 120;

            Width = saved.Width;
            Height = saved.Height;
            if (visible)
            {
                Left = left;
                Top = top;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }
        catch (Exception ex)
        {
            Log.Write("窗口位置读不出来，按默认位置开窗", ex);
        }
    }

    /// <summary>
    /// 关窗口前先把 WebView2 明确释放掉。让它在窗口销毁之后被 GC 收，
    /// 浏览器那套 GDI 资源会在 WPF 已经拆掉窗口时回收，容易连带整进程一起崩。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            Web?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("关窗口时释放浏览器出错", ex);
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// 页面顶栏里自己画的拖动区、最小化/最大化/关闭按钮，都从这条通道进来。
    /// </summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? action = null;
        string? initial = null;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            if (document.RootElement.TryGetProperty("action", out var kind))
            {
                action = kind.GetString();
            }

            if (document.RootElement.TryGetProperty("initial", out var start))
            {
                initial = start.GetString();
            }
        }
        catch (Exception ex)
        {
            Log.Write("看不懂页面发来的窗口指令", ex);
            return;
        }

        Log.Write($"窗口指令：{action}");

        switch (action)
        {
            case "drag":
                StartSystemDrag();
                break;

            case "minimize":
                WindowState = WindowState.Minimized;
                break;

            case "toggle-maximize":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                PostWindowState();
                break;

            case "close":
                Close();
                break;

            case "pick-folder":
                PickFolder(initial);
                break;
        }
    }

    /// <summary>
    /// 新建对话时的选目录交给系统：用惯了资源管理器那一套，自己画的树只是负担。
    /// 选完把路径发回页面，取消就发空。
    /// </summary>
    private void PickFolder(string? initial)
    {
        // 对话框自己会跑消息循环，期间再点一次「新建对话」会又开一个，挡住。
        if (_pickingFolder)
        {
            return;
        }

        _pickingFolder = true;
        string? picked = null;
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选一个目录当工作区",
                Multiselect = false,
            };

            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
            {
                dialog.InitialDirectory = initial;
            }

            if (dialog.ShowDialog(this) == true)
            {
                picked = dialog.FolderName;
            }
        }
        catch (Exception ex)
        {
            Log.Write("选目录的对话框没能打开", ex);
        }

        _pickingFolder = false;

        try
        {
            Web?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "folder-picked",
                path = picked,
            }));
        }
        catch (Exception ex)
        {
            Log.Write("选好的目录发不回页面", ex);
        }
    }

    /// <summary>
    /// 把自己画的顶栏交给系统的拖动循环：鼠标一松系统自己完成贴边分屏，
    /// 不用在页面里算坐标。
    /// </summary>
    private void StartSystemDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(hwnd, 0x00A1, (IntPtr)0x0002, IntPtr.Zero); // WM_NCLBUTTONDOWN + HTCAPTION
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    private const uint MonitorDefaultToNearest = 0x00000002;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new WindowBounds(Left, Top, Width, Height)
                : new WindowBounds(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height);

            // 先写临时文件再换上去：上次进程要是写到一半崩了，留下的是空文件，
            // 下次启动就得从默认位置开窗。
            var path = Path.Combine(CenterUrl.DirectoryPath, "window.json");
            var temp = path + ".tmp";
            Directory.CreateDirectory(CenterUrl.DirectoryPath);
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(bounds));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("窗口位置存不下来", ex);
        }

        base.OnClosed(e);
    }

    private sealed record WindowBounds(double Left, double Top, double Width, double Height);

    private async Task StartWebViewAsync()
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SevenStation", "WebView2");
            Directory.CreateDirectory(userData);

            var environment = await CreateEnvironmentAsync(userData);
            await Web.EnsureCoreWebView2Async(environment);

            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternal(args.Uri);
            };
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;

            // 页面加载完再报一次：刚导航时那条消息页面还没开始听。
            core.NavigationCompleted += (_, _) => PostWindowState();
            core.WebMessageReceived += OnWebMessage;

            var center = CenterUrl.Resolve();
            core.Navigate(center);
            Log.Write($"客户端加载中心页面：{center}");
            PostWindowState();
        }
        catch (Exception ex)
        {
            Log.Write("界面没能启动", ex);
            var busy = ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x800700AA) };
            MessageBox.Show(
                busy
                    ? "没能打开界面：还有旧的 SevenStation 占着缓存目录。" + Environment.NewLine + Environment.NewLine +
                      "先把之前的窗口都关掉（任务管理器里残留的 msedgewebview2 也一起结束），再打开一次。"
                    : $"界面没能启动：{ex.Message}",
                "SevenStation",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    /// <summary>
    /// 上一次被强制结束时，WebView2 的浏览器进程可能还占着缓存目录（0x800700AA）。
    /// 这种情况等一会儿重试通常就好了，不必让用户自己去清理。
    /// </summary>
    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(string userData)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CoreWebView2Environment.CreateAsync(null, userData);
            }
            catch (Exception ex) when (attempt < 2 &&
                ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x800700AA) })
            {
                Log.Write($"缓存目录被占用，{attempt + 1} 次重试");
                await Task.Delay(1500);
            }
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_showingOffline)
        {
            return;
        }

        // 只让中心页面留在窗口里，其余链接交给系统浏览器。
        if (!CenterUrl.IsCenterPage(e.Uri))
        {
            e.Cancel = true;
            OpenExternal(e.Uri);
        }
    }

    /// <summary>
    /// 中心没起来时 WebView2 只会给一片空白。换成一页能读的说明：连的哪个地址、配置在哪、多久重试一次。
    /// </summary>
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_showingOffline)
        {
            _showingOffline = false;
            return;
        }

        if (e.IsSuccess)
        {
            return;
        }

        var center = CenterUrl.Resolve();
        var config = CenterUrl.ConfigPath;
        var display = CenterUrl.DisplayUrl;
        Log.Write($"连不上中心 {center}：{e.WebErrorStatus}");

        _showingOffline = true;
        Web.CoreWebView2.NavigateToString($$"""
            <!doctype html>
            <meta charset="utf-8">
            <style>
              body { margin: 0; font-family: "Microsoft YaHei UI", system-ui, sans-serif; background: #f6f6f8;
                     color: #1c1c22; display: flex; min-height: 100vh; align-items: center; justify-content: center; }
              .card { max-width: 520px; padding: 32px; background: #fff; border: 1px solid #e6e6ea;
                       border-radius: 14px; box-shadow: 0 12px 32px rgba(16,16,20,.08); }
              h1 { font-size: 20px; margin: 0 0 10px; }
              p { margin: 0 0 14px; line-height: 1.7; color: #4b4b56; font-size: 13.5px; }
              code { font-family: Consolas, monospace; background: #f2f2f5; padding: 1px 5px; border-radius: 4px; }
              a { display: inline-block; margin-top: 6px; padding: 8px 16px; border-radius: 8px;
                   background: #1c1c22; color: #fff; text-decoration: none; font-size: 13px; }
            </style>
            <div class="card">
              <h1>连不上中心</h1>
              <p>要连的地址是 <code>{{display}}</code>。</p>
              <p>中心那台机器上的 SevenStation 中心没起来，或者这个地址写错了。
                 中心用 <code>scripts/start-center.ps1</code> 起；装过自启的话，登录后它会自己起来。</p>
              <p>地址与令牌在这个文件里：<br><code>{{config}}</code></p>
              <p>5 秒后自动重试。</p>
              <a href="{{center}}">现在重试</a>
            </div>
            <script>setTimeout(function () { location.href = "{{center}}"; }, 5000);</script>
            """);
    }

    private static void OpenExternal(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"打不开外部链接 {uri}", ex);
        }
    }
}

/// <summary>
/// 中心地址与访问令牌。默认本机中心；配置文件放在 <c>%LOCALAPPDATA%\SevenStation\center.json</c>，
/// 换远程中心时只改这里。
/// </summary>
internal static class CenterUrl
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SevenStation");

    public static string ConfigPath { get; } = Path.Combine(DirectoryPath, "center.json");

    /// <summary>中心与本机同一台机器时，直接读中心写下的令牌文件。</summary>
    public static string TokenPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SevenStation", "token.txt");

    public static string Resolve()
    {
        var (baseUrl, token) = ReadConfig();
        token ??= TryRead(TokenPath)
            ?? Environment.GetEnvironmentVariable("SEVENSTATION_TOKEN")?.Trim();

        if (string.IsNullOrEmpty(token))
        {
            return baseUrl;
        }

        var separator = baseUrl.Contains('?') ? '&' : '?';
        return $"{baseUrl}{separator}token={Uri.EscapeDataString(token)}";
    }

    /// <summary>给人看的地址：不带令牌，方便截图与报错。</summary>
    public static string DisplayUrl => ReadConfig().BaseUrl;

    public static bool IsCenterPage(string uri)
    {
        var (baseUrl, _) = ReadConfig();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var center) ||
            !Uri.TryCreate(uri, UriKind.Absolute, out var target))
        {
            return false;
        }

        return string.Equals(center.Host, target.Host, StringComparison.OrdinalIgnoreCase) &&
               center.Port == target.Port;
    }

    /// <summary>中心跟客户端是不是同一台机器：决定选目录时给谁的路径。</summary>
    public static bool IsLocal
    {
        get
        {
            if (!Uri.TryCreate(ReadConfig().BaseUrl, UriKind.Absolute, out var center))
            {
                return true;
            }

            return center.IsLoopback ||
                   string.Equals(center.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(center.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static (string BaseUrl, string? Token) ReadConfig()
    {
        const string fallback = "http://127.0.0.1:8787/";
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(
                    ConfigPath,
                    "{\n  \"url\": \"http://127.0.0.1:8787/\",\n  \"token\": \"\"\n}\n");
                return (fallback, null);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = document.RootElement;
            var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var token = root.TryGetProperty("token", out var tokenElement) ? tokenElement.GetString() : null;

            var normalized = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim();
            if (!normalized.EndsWith('/'))
            {
                normalized += "/";
            }

            return (normalized, string.IsNullOrWhiteSpace(token) ? null : token.Trim());
        }
        catch (Exception ex)
        {
            Log.Write($"读不了 {ConfigPath}，用本机默认地址", ex);
            return (fallback, null);
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var value = File.ReadAllText(path).Trim();
            return value.Length >= 16 ? value : null;
        }
        catch
        {
            return null;
        }
    }
}

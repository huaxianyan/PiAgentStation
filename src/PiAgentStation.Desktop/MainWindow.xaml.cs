using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PiAgentStation.Shared;

namespace PiAgentStation.Desktop;

/// <summary>
/// 桌面客户端只是一层壳：连中心、把中心发来的页面显示出来。
/// Pi 子进程与会话都在中心那边，这个进程不碰。
/// </summary>
public partial class MainWindow : Window
{
    private bool _showingOffline;
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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
            Log.Write("窗口位置读不出来", ex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new WindowBounds(Left, Top, Width, Height)
                : new WindowBounds(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height);

            var path = Path.Combine(CenterUrl.DirectoryPath, "window.json");
            Directory.CreateDirectory(CenterUrl.DirectoryPath);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(bounds));
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
                "PiAgentStation", "WebView2");
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

            var center = CenterUrl.Resolve();
            core.Navigate(center);
            Log.Write($"客户端加载中心页面：{center}");
        }
        catch (Exception ex)
        {
            Log.Write("界面没能启动", ex);
            var busy = ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x800700AA) };
            MessageBox.Show(
                busy
                    ? "没能打开界面：还有旧的 PiAgentStation 占着缓存目录。" + Environment.NewLine + Environment.NewLine +
                      "先把之前的窗口都关掉（任务管理器里残留的 msedgewebview2 也一起结束），再打开一次。"
                    : $"界面没能启动：{ex.Message}",
                "PiAgentStation",
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
              <p>中心那台机器上的 PiAgentStation 中心没起来，或者这个地址写错了。
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
/// 中心地址与访问令牌。默认本机中心；配置文件放在 <c>%LOCALAPPDATA%\PiAgentStation\center.json</c>，
/// 换远程中心时只改这里。
/// </summary>
internal static class CenterUrl
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiAgentStation");

    public static string ConfigPath { get; } = Path.Combine(DirectoryPath, "center.json");

    /// <summary>中心与本机同一台机器时，直接读中心写下的令牌文件。</summary>
    public static string TokenPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAStation", "token.txt");

    public static string Resolve()
    {
        var (baseUrl, token) = ReadConfig();
        token ??= TryRead(TokenPath)
            ?? Environment.GetEnvironmentVariable("MASTATION_TOKEN")?.Trim();

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

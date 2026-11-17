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
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PiAgentStation", "WebView2");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
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

            var center = CenterUrl.Resolve();
            core.Navigate(center);
            Log.Write($"客户端加载中心页面：{center}");
        }
        catch (Exception ex)
        {
            Log.Write("界面没能启动", ex);
            MessageBox.Show(
                $"界面没能启动：{ex.Message}",
                "PiAgentStation",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // 只让中心页面留在窗口里，其余链接交给系统浏览器。
        if (!CenterUrl.IsCenterPage(e.Uri))
        {
            e.Cancel = true;
            OpenExternal(e.Uri);
        }
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
    private static readonly string DirectoryPath = Path.Combine(
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

using System.IO;

namespace SevenStation.Shared;

/// <summary>
/// 两个产品的目录，各归各的。Seven Station Core 是机器级——它当服务跑的时候没有「当前用户」这回事，
/// 数据、令牌、配置、日志都放 <c>%ProgramData%\SevenStation.Core</c>；客户端是用户级。
/// </summary>
public static class ProductPaths
{
    /// <summary>
    /// Seven Station Core 根目录。<c>SEVENSTATION_CORE_HOME</c> 可整体挪走，开发和测试用它隔离，
    /// 免得动到真正在用的那一份。
    /// </summary>
    public static string CoreHome { get; } = Resolve(
        "SEVENSTATION_CORE_HOME",
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SevenStation.Core"));

    public static string CoreTokenFile => Path.Combine(CoreHome, "token.txt");

    public static string CoreSettingsFile => Path.Combine(CoreHome, "settings.json");

    public static string CoreLogs => Path.Combine(CoreHome, "logs");

    /// <summary>对话库所在目录；<c>SEVENSTATION_DATA_DIR</c> 覆盖（测试里指到临时目录）。</summary>
    public static string CoreDataDirectory => Resolve(
        "SEVENSTATION_DATA_DIR",
        Path.Combine(CoreHome, "data"));

    /// <summary>客户端的数据：WebView2 缓存、日志、center.json、界面偏好。机器级的配对信息另放。</summary>
    public static string ClientHome { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SevenStation");

    private static string Resolve(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}

using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using PiAgentStation.Server;
using PiAgentStation.Server.Sessions;
using PiAgentStation.Shared;

var listen = Environment.GetEnvironmentVariable("PIAGENTSTATION_LISTEN") ?? "http://127.0.0.1:8787";
var token = TokenStore.Resolve();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(listen);
// 日志写自己的文件，控制台只留启动那几行，免得被 ASP.NET 默认日志淹掉。
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider());

var app = builder.Build();
var hub = new CenterHub();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!TokenStore.Matches(context, token))
    {
        Log.Write("拒绝了一个令牌不匹配的连接");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connection = new CenterConnection(socket, hub);
    await connection.RunAsync(context.RequestAborted);
});

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    sessionRoot = SessionStore.RootDirectory,
}));

app.Lifetime.ApplicationStopping.Register(() =>
{
    hub.ShutdownAsync().GetAwaiter().GetResult();
    Log.Write("中心停止");
});

Log.Write($"中心启动 {listen}，会话目录 {SessionStore.RootDirectory}");
Console.WriteLine($"PiAgentStation 中心已启动：{listen}");
Console.WriteLine($"打开地址：{listen.TrimEnd('/')}/");
Console.WriteLine($"访问令牌：见 {TokenStore.FilePath}（桌面客户端会自动读取；手机用 ?token= 拼在地址后）");

app.Run();

internal static class TokenStore
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PiAgentStation", "center-token.txt");

    public static string Resolve()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("PIAGENTSTATION_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        if (File.Exists(FilePath))
        {
            var existing = File.ReadAllText(FilePath).Trim();
            if (existing.Length >= 16)
            {
                return existing;
            }
        }

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, generated);
        Log.Write($"生成了新的访问令牌，写在 {FilePath}");
        return generated;
    }

    /// <summary>接受 <c>?token=</c> 或 <c>Authorization: Bearer</c>。</summary>
    public static bool Matches(HttpContext context, string expected)
    {
        var provided = context.Request.Query["token"].ToString();
        if (string.IsNullOrEmpty(provided))
        {
            var header = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                provided = header[prefix.Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

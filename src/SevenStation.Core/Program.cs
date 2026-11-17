using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using SevenStation.Core;
using SevenStation.Core.Storage;
using SevenStation.Shared;

SevenStation.Shared.Log.Initialize("SevenStation", "center.log");

var listen = Environment.GetEnvironmentVariable("SEVENSTATION_LISTEN") ?? "http://127.0.0.1:8787";
var token = TokenStore.Resolve();

// 静态页永远跟着 exe 走：不管从哪个目录启动，都能找到 wwwroot。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.UseUrls(listen);
// 日志写自己的文件，控制台只留启动那几行，免得被 ASP.NET 默认日志淹掉。
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider());

var app = builder.Build();

var dataDirectory = Environment.GetEnvironmentVariable("SEVENSTATION_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SevenStation", "data");
var store = new ConversationStore(Path.Combine(dataDirectory, "sevenstation.db"));
Log.Write($"对话库：{store.DatabasePath}");
var hub = new CenterHub(store, listen);

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // 前端是跟着中心一起更新的，客户端不能拿着旧缓存跑：每次都要回中心确认。
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate",
});

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
    database = store.DatabasePath,
}));

app.Lifetime.ApplicationStopping.Register(() =>
{
    hub.ShutdownAsync().GetAwaiter().GetResult();
    store.Dispose();
    Log.Write("中心停止");
});

Log.Write($"中心启动 {listen}，对话库 {store.DatabasePath}");
Console.WriteLine($"SevenStation.Core 已启动：{listen}");
Console.WriteLine($"打开地址：{listen.TrimEnd('/')}/");
Console.WriteLine($"访问令牌：见 {TokenStore.FilePath}（桌面客户端会自动读取；手机用 ?token= 拼在地址后）");

app.Run();

internal static class TokenStore
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SevenStation", "token.txt");

    public static string Resolve()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("SEVENSTATION_TOKEN");
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

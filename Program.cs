using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FFmpegWebUI.Components;
using FFmpegWebUI.Data;
using FFmpegWebUI.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// ────────────────────────────────────────────────────────────────────────────
// 监听地址
// 这是一个本机桌面工具：默认只监听回环地址，避免局域网内其他人访问到
// 「能在本机执行 FFmpeg / 读写任意文件」的接口。
// 确实需要远程访问时用 FFMPEGWEBUI_BIND=0.0.0.0 显式开启。
// ────────────────────────────────────────────────────────────────────────────
var bindAddress = Environment.GetEnvironmentVariable("FFMPEGWEBUI_BIND");
var listenPort = ResolvePort(builder.Configuration, args);
var isLoopbackOnly = string.IsNullOrWhiteSpace(bindAddress);

if (isLoopbackOnly && listenPort == 0) listenPort = FindFreePort(5265);

builder.WebHost.UseUrls($"http://{(isLoopbackOnly ? "127.0.0.1" : bindAddress)}:{listenPort}");

if (isLoopbackOnly)
{
    builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
}

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

// ────────────────────────────────────────────────────────────────────────────
// 服务注册
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 数据库与基础服务（单例：整个应用共享一份状态，页面切换不受影响）
builder.Services.AddSingleton<ILiteDbContext, LiteDbContext>();
builder.Services.AddSingleton<IFFmpegLocator, FFmpegLocator>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IFileService, FileService>();
builder.Services.AddSingleton<IFileDialogService, FileDialogService>();

// FFmpeg：把「进程登记表」放在单例里，才能从任意页面取消正在运行的任务
builder.Services.AddSingleton<IFFmpegService, FFmpegService>();
builder.Services.AddSingleton<IHardwareDetectionService, HardwareDetectionService>();
builder.Services.AddSingleton<ITemplateService, TemplateService>();

// 后台转换队列 + 任务服务
builder.Services.AddSingleton<ConversionQueue>();
builder.Services.AddSingleton<ITaskService, TaskService>();

// 启动期状态检查（FFmpeg 可用性 / 模板同步 / 历史清理）
builder.Services.AddSingleton<IAppStatusService, AppStatusService>();

// 全局轻提示
builder.Services.AddSingleton<IToastService, ToastService>();

var app = builder.Build();

// ────────────────────────────────────────────────────────────────────────────
// 启动初始化（失败不应阻止应用启动，否则用户连设置界面都打不开）
// ────────────────────────────────────────────────────────────────────────────
await InitializeApplicationAsync(app.Services);

// ────────────────────────────────────────────────────────────────────────────
// 请求管线
// ────────────────────────────────────────────────────────────────────────────
app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// 让内嵌脚本能拿到运行期信息（版本、已解析路径等）
app.MapGet("/api/health", (IServiceProvider services) =>
{
    var locator = services.GetRequiredService<IFFmpegLocator>();
    var paths = locator.Resolve();
    return Results.Json(new
    {
        status = "ok",
        version = AppInfo.Version,
        platform = AppPaths.PlatformDisplayName,
        ffmpeg = paths.FFmpeg,
        ffmpegResolved = paths.FFmpegResolved,
        ffprobe = paths.FFprobe,
        ffprobeResolved = paths.FFprobeResolved
    });
});

// 启动完成后按需自动打开浏览器
if (ShouldAutoOpenBrowser(app))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();

        if (!string.IsNullOrEmpty(address))
        {
            OpenBrowser(address.Replace("127.0.0.1", "localhost"));
        }
    });
}

app.Run();

// ────────────────────────────────────────────────────────────────────────────
// 辅助
// ────────────────────────────────────────────────────────────────────────────

static async Task InitializeApplicationAsync(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILogger<Program>>();

    try
    {
        // LiteDB 初始化失败必须让用户看到，否则整个应用都是空数据
        var db = services.GetRequiredService<ILiteDbContext>();
        _ = db.Settings.FindAll().FirstOrDefault();
        logger.LogInformation("数据目录：{DataDirectory}", AppPaths.DataDirectory);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "数据库初始化失败：{DataDirectory}", AppPaths.DataDirectory);
    }

    try
    {
        // 上次非正常退出留下的「运行中」任务需要复位
        var taskService = services.GetRequiredService<ITaskService>();
        var recovered = taskService.RecoverOrphanedTasks();
        if (recovered > 0) logger.LogInformation("已复位 {Count} 个中断的任务", recovered);

        // 系统模板随应用版本同步
        var templateService = services.GetRequiredService<ITemplateService>();
        var sync = await templateService.InitializeSystemTemplatesAsync();
        logger.LogInformation("{Summary}", sync.Summary);

        // 按设置清理过期历史
        var settingsService = services.GetRequiredService<ISettingsService>();
        var settings = await settingsService.GetSettingsAsync();
        if (settings.TaskHistoryRetentionDays > 0)
        {
            var threshold = DateTime.UtcNow.AddDays(-settings.TaskHistoryRetentionDays);
            var removed = await taskService.CleanupHistoryAsync(threshold);
            if (removed > 0) logger.LogInformation("已清理 {Count} 条过期任务记录", removed);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "启动初始化部分步骤失败，应用仍会继续启动");
    }

    // FFmpeg 可用性 + 硬件编码器检测放到后台，不阻塞启动
    _ = Task.Run(async () =>
    {
        try
        {
            var ffmpegService = services.GetRequiredService<IFFmpegService>();
            var info = await ffmpegService.GetFFmpegInfoAsync(forceRefresh: true);

            if (info is null || !info.IsAvailable)
            {
                var locator = services.GetRequiredService<IFFmpegLocator>();
                logger.LogWarning("未能找到可用的 FFmpeg（{Hint}）。请打开「设置」指定 FFmpeg 路径。", locator.Resolve().Hint);
                return;
            }

            logger.LogInformation("FFmpeg {Version}（{Path}）", info.Version, info.Path);

            // 版本提醒：9.0 起移除了若干旧选项，模板需要按版本生成
            if (info.MajorVersion is > 0 and < 6)
            {
                logger.LogWarning("当前 FFmpeg 主版本为 {Major}，较旧。建议升级到 9.x 以获得完整的硬件加速支持。", info.MajorVersion);
            }

            var hardware = services.GetRequiredService<IHardwareDetectionService>();
            var report = await hardware.DetectAsync(forceRefresh: false);
            logger.LogInformation("{Summary}", report.Summary);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "硬件编码器检测失败，将使用软件编码");
        }
    });
}

static int ResolvePort(IConfiguration configuration, string[] args)
{
    // 优先级：命令行 --port > 环境变量 > appsettings.json > 数据库里保存的偏好 > 默认值
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var fromArgs)) return fromArgs;
    }

    var fromEnv = Environment.GetEnvironmentVariable("FFMPEGWEBUI_PORT");
    if (int.TryParse(fromEnv, out var envPort)) return envPort;

    var configured = configuration.GetValue<int?>("FFmpegWebUI:ListenPort");
    if (configured.HasValue) return configured.Value;

    var saved = ReadSavedPreference(configuration, "ListenPort");
    if (int.TryParse(saved, out var savedPort)) return savedPort;

    return 5265;
}

static bool ShouldAutoOpenBrowser(WebApplication app)
{
    if (string.Equals(Environment.GetEnvironmentVariable("FFMPEGWEBUI_NO_BROWSER"), "1", StringComparison.Ordinal)) return false;

    // 开发时 launchSettings 已经会打开浏览器，避免开两个窗口
    if (app.Environment.IsDevelopment()) return false;

    var configured = app.Configuration.GetValue<bool?>("FFmpegWebUI:AutoOpenBrowser");
    if (configured.HasValue) return configured.Value;

    var saved = ReadSavedPreference(app.Configuration, "AutoOpenBrowser");
    if (bool.TryParse(saved, out var savedValue)) return savedValue;

    return true;
}

static int FindFreePort(int preferred)
{
    for (var port = preferred; port < preferred + 100; port++)
    {
        if (IsPortFree(port)) return port;
    }
    return 0; // 交给系统随机分配
}

static bool IsPortFree(int port)
{
    try
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
}

/// <summary>
/// 在 Web 主机启动前直接读一次数据库里的设置。
/// 端口和「是否自动打开浏览器」必须在 UseUrls / Run 之前确定，
/// 这时 DI 容器还没建好，所以这里直接读 LiteDB。
/// 数据库不存在或读取失败时返回 null，由调用方使用默认值。
/// </summary>
static string? ReadSavedPreference(IConfiguration configuration, string fieldName)
{
    try
    {
        var dbPath = configuration["FFmpegWebUI:DatabasePath"];
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            dbPath = Path.Combine(AppPaths.DataDirectory, "data.db");
        }
        else
        {
            dbPath = AppPaths.ToAbsolute(dbPath);
        }

        if (!File.Exists(dbPath)) return null;

        using var db = new LiteDB.LiteDatabase(dbPath);
        var document = db.GetCollection("settings").FindAll().FirstOrDefault();
        if (document is null) return null;

        var value = document[fieldName];
        return value.IsNull ? null : value.ToString();
    }
    catch
    {
        // 数据库损坏 / 被占用时不影响启动
        return null;
    }
}

static void OpenBrowser(string url)
{
    try
    {
        if (AppPaths.IsWindows)
        {
            Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        }
        else if (AppPaths.IsMacOS)
        {
            Process.Start("open", [url]);
        }
        else
        {
            Process.Start("xdg-open", [url]);
        }
    }
    catch
    {
        // 无桌面环境（比如容器 / SSH）时静默忽略
    }
}

using FFmpegWebUI.Components;
using FFmpegWebUI.Data;
using FFmpegWebUI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 注册 LiteDB 上下文（单例）
builder.Services.AddSingleton<ILiteDbContext, LiteDbContext>();

// 注册基础服务
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddScoped<IFileService, FileService>();

// 注册 FFmpeg 相关服务
builder.Services.AddSingleton<IHardwareDetectionService, HardwareDetectionService>();
builder.Services.AddScoped<IFFmpegService, FFmpegService>();

// 注册模板和任务服务
builder.Services.AddSingleton<ITemplateService, TemplateService>();
builder.Services.AddScoped<ITaskService, TaskService>();

var app = builder.Build();

// 应用启动初始化
await InitializeApplicationAsync(app.Services);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>应用启动初始化</summary>
static async Task InitializeApplicationAsync(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILogger<Program>>();

    try
    {
        // 初始化系统模板
        var templateService = services.GetRequiredService<ITemplateService>();
        await templateService.InitializeSystemTemplatesAsync();
        logger.LogInformation("系统模板初始化完成");

        // 后台检测硬件编码器
        var hardwareService = services.GetRequiredService<IHardwareDetectionService>();
        _ = Task.Run(async () =>
        {
            try
            {
                var encoders = await hardwareService.DetectEncodersAsync();
                var availableCount = encoders.Count(e => e.IsAvailable);
                logger.LogInformation("硬件编码器检测完成: {Available}/{Total} 可用",
                    availableCount, encoders.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "硬件编码器检测失败");
            }
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "应用初始化失败");
    }
}

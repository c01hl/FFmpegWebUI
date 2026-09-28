using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>应用整体状态快照</summary>
public sealed record AppStatus
{
    /// <summary>应用版本</summary>
    public string AppVersion { get; init; } = AppInfo.Version;

    /// <summary>当前平台</summary>
    public string Platform { get; init; } = AppPaths.PlatformDisplayName;

    /// <summary>.NET 运行时版本</summary>
    public string Runtime { get; init; } = Environment.Version.ToString();

    /// <summary>数据目录</summary>
    public string DataDirectory { get; init; } = AppPaths.DataDirectory;

    /// <summary>FFmpeg 是否可用</summary>
    public bool FFmpegAvailable { get; init; }

    /// <summary>FFmpeg 版本</summary>
    public string FFmpegVersion { get; init; } = string.Empty;

    /// <summary>FFmpeg 实际使用的路径</summary>
    public string FFmpegPath { get; init; } = string.Empty;

    /// <summary>ffprobe 实际使用的路径</summary>
    public string FFprobePath { get; init; } = string.Empty;

    /// <summary>路径是否为自动探测得到</summary>
    public bool AutoDetected { get; init; }

    /// <summary>FFmpeg 支持的硬件解码方式</summary>
    public List<string> Hwaccels { get; init; } = [];

    /// <summary>FFmpeg 主版本号</summary>
    public int MajorVersion { get; init; }

    /// <summary>可用的硬件编码器数量</summary>
    public int AvailableHardwareEncoderCount { get; init; }

    /// <summary>模板总数</summary>
    public int TemplateCount { get; init; }

    /// <summary>任务总数</summary>
    public int TaskCount { get; init; }

    /// <summary>错误/警告提示</summary>
    public List<string> Notices { get; init; } = [];

    /// <summary>是否一切正常</summary>
    public bool IsHealthy => FFmpegAvailable;
}

/// <summary>为界面提供环境与运行状态，集中实现诊断逻辑</summary>
public interface IAppStatusService
{
    /// <summary>获取当前状态（带短暂缓存）</summary>
    Task<AppStatus> GetStatusAsync(bool forceRefresh = false);

    /// <summary>检查数据目录是否可写</summary>
    bool IsDataDirectoryWritable { get; }
}

/// <summary>应用状态服务实现</summary>
public sealed class AppStatusService(
    IFFmpegService ffmpegService,
    IFFmpegLocator locator,
    IHardwareDetectionService hardwareDetection,
    ITemplateService templateService,
    ITaskService taskService,
    IFileService fileService) : IAppStatusService
{
    private AppStatus? _cached;
    private DateTime _cachedAt = DateTime.MinValue;

    public bool IsDataDirectoryWritable => fileService.IsDirectoryWritable(AppPaths.DataDirectory);

    public async Task<AppStatus> GetStatusAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cached != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 10)
        {
            return _cached;
        }

        var notices = new List<string>();
        var paths = locator.Resolve();

        FFmpegInfo? info = null;
        try
        {
            info = await ffmpegService.GetFFmpegInfoAsync(forceRefresh).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            notices.Add($"检测 FFmpeg 时出错：{ex.Message}");
        }

        var available = info is { IsAvailable: true };
        if (!available)
        {
            notices.Add("未找到可用的 FFmpeg。" +
                        (AppPaths.IsWindows
                            ? "请安装 FFmpeg 并加入 PATH，或在「设置」中指定 ffmpeg.exe 的完整路径。"
                            : "请安装 FFmpeg（macOS: brew install ffmpeg；Linux: apt install ffmpeg），或在「设置」中指定路径。"));
        }
        else if (info!.MajorVersion is > 0 and < 6)
        {
            notices.Add($"当前 FFmpeg 主版本为 {info.MajorVersion}，较旧。建议升级到 9.x 以获得完整的硬件加速与格式支持。");
        }

        var hardwareCount = 0;
        if (available)
        {
            try
            {
                var report = await hardwareDetection.DetectAsync(forceRefresh).ConfigureAwait(false);
                hardwareCount = report.AvailableHardwareEncoders.Count;
                if (hardwareCount == 0)
                {
                    notices.Add("未检测到可用的硬件编码器，转换将使用 CPU 软件编码（速度较慢但结果稳定）。");
                }
            }
            catch (Exception ex)
            {
                notices.Add($"硬件编码器检测失败：{ex.Message}");
            }
        }

        List<CommandTemplate> templates = [];
        try
        {
            templates = await templateService.GetAllTemplatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            notices.Add($"读取模板失败：{ex.Message}");
        }

        var status = new AppStatus
        {
            AppVersion = AppInfo.Version,
            Platform = AppPaths.PlatformDisplayName,
            DataDirectory = AppPaths.DataDirectory,
            FFmpegAvailable = available,
            FFmpegVersion = available ? info!.Version : "未检测到",
            FFmpegPath = available ? info!.Path : paths.FFmpeg,
            FFprobePath = paths.FFprobe,
            AutoDetected = paths.IsAutoDetected,
            Hwaccels = available ? info!.SupportedHwaccels : [],
            MajorVersion = available ? info!.MajorVersion : 0,
            AvailableHardwareEncoderCount = hardwareCount,
            TemplateCount = templates.Count,
            TaskCount = taskService.QueuedCount + taskService.ActiveCount,
            Notices = notices
        };

        _cached = status;
        _cachedAt = DateTime.UtcNow;
        return status;
    }
}

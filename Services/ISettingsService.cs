using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>设置服务接口</summary>
public interface ISettingsService
{
    /// <summary>获取当前设置（已规范化；修改后需显式保存）</summary>
    Task<AppSettings> GetSettingsAsync();

    /// <summary>保存设置</summary>
    Task SaveSettingsAsync(AppSettings settings);

    /// <summary>重置为默认设置</summary>
    Task ResetToDefaultAsync();

    /// <summary>验证 FFmpeg 路径是否可用</summary>
    Task<bool> ValidateFFmpegPathAsync(string path);

    /// <summary>读取 FFmpeg 版本信息（用于设置页展示与诊断）</summary>
    Task<string?> GetFFmpegVersionAsync(string path);

    /// <summary>设置发生变更时触发（用于清理路径缓存等）</summary>
    event EventHandler? SettingsChanged;
}

using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>设置服务接口</summary>
public interface ISettingsService
{
    /// <summary>获取当前设置</summary>
    Task<AppSettings> GetSettingsAsync();

    /// <summary>保存设置</summary>
    Task SaveSettingsAsync(AppSettings settings);

    /// <summary>重置为默认设置</summary>
    Task ResetToDefaultAsync();

    /// <summary>验证 FFmpeg 路径</summary>
    Task<bool> ValidateFFmpegPathAsync(string path);
}

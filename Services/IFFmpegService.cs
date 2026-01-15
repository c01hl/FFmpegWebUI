using FFmpegWebUI.Models;
using LiteDB;

namespace FFmpegWebUI.Services;

/// <summary>FFmpeg 执行服务接口</summary>
public interface IFFmpegService
{
    /// <summary>检测 FFmpeg 是否可用</summary>
    Task<FFmpegInfo?> GetFFmpegInfoAsync();

    /// <summary>获取媒体文件信息</summary>
    Task<MediaInfo?> GetMediaInfoAsync(string filePath);

    /// <summary>执行转换任务</summary>
    Task ExecuteConversionAsync(
        ConversionTask task,
        Action<ConversionProgress> onProgress,
        CancellationToken cancellationToken = default);

    /// <summary>取消正在执行的任务</summary>
    Task CancelTaskAsync(ObjectId taskId);

    /// <summary>构建 FFmpeg 命令行</summary>
    string BuildCommand(
        CommandTemplate template,
        string inputPath,
        string outputPath,
        Dictionary<string, string>? parameters = null);
}

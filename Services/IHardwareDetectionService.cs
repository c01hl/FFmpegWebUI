using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>硬件检测服务接口</summary>
public interface IHardwareDetectionService
{
    /// <summary>执行硬件编码器检测</summary>
    Task<List<HardwareEncoder>> DetectEncodersAsync(bool forceRefresh = false);

    /// <summary>检查特定编码器是否可用</summary>
    Task<bool> IsEncoderAvailableAsync(string encoderName);

    /// <summary>获取推荐的编码器</summary>
    Task<string> GetRecommendedEncoderAsync(string codec);

    /// <summary>获取上次检测时间</summary>
    DateTime? LastDetectionTime { get; }
}

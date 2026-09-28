using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>硬件检测服务接口</summary>
public interface IHardwareDetectionService
{
    /// <summary>执行硬件编码器检测（结果会缓存到数据库）</summary>
    Task<HardwareDetectionReport> DetectAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>获取上次检测报告（不触发检测，未检测过时返回 null）</summary>
    HardwareDetectionReport? LastReport { get; }

    /// <summary>检查特定编码器是否可用</summary>
    Task<bool> IsEncoderAvailableAsync(string encoderName);

    /// <summary>
    /// 获取推荐的编码器。codec 为 h264/hevc/av1/vp9 等；
    /// requiredEncoder 可以是族名（nvenc/qsv/…）或具体编码器名。
    /// </summary>
    /// <param name="strict">
    /// true 表示「指定的编码器必须可用」：当该族在本机没有可用成员时返回 null，
    /// 而不是退而求其次选别的编码器。模板声明了 Required（必须用某厂商硬件）时用这个模式，
    /// 否则会出现「模板写着 NVENC，实际用 VideoToolbox 跑」这种名不副实的结果。
    /// </param>
    Task<string?> GetRecommendedEncoderAsync(string codec, string? requiredEncoder = null, bool strict = false);

    /// <summary>根据编码器名解析其参数画像</summary>
    EncoderProfile? ResolveProfile(string encoderName);

    /// <summary>上次检测时间</summary>
    DateTime? LastDetectionTime { get; }
}

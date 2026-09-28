using LiteDB;

namespace FFmpegWebUI.Models;

/// <summary>硬件编码器（检测结果）</summary>
public class HardwareEncoder
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>编码器名称（如 h264_nvenc）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>显示名称（如 NVIDIA NVENC H.264）</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>编码器类型</summary>
    public EncoderType Type { get; set; } = EncoderType.Software;

    /// <summary>厂商族标识：nvenc / qsv / amf / videotoolbox / vaapi / vulkan / mf / software</summary>
    public string Family { get; set; } = string.Empty;

    /// <summary>目标编码格式：h264 / hevc / av1 / vp9 …</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>是否可用</summary>
    public bool IsAvailable { get; set; }

    /// <summary>是否已通过带质量参数的完整试跑（false 表示只能使用最简参数）</summary>
    public bool SupportsQualityOptions { get; set; } = true;

    /// <summary>是否为硬件编码器</summary>
    public bool IsHardware => Type != EncoderType.Software;

    /// <summary>支持的编码格式（兼容旧字段）</summary>
    public List<string> SupportedCodecs { get; set; } = [];

    /// <summary>最后检测时间</summary>
    public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;

    /// <summary>检测失败原因（如果不可用）</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>试跑时是否不在 FFmpeg 编码器列表中</summary>
    public bool MissingFromBuild { get; set; }

    /// <summary>检测用的 FFmpeg 版本（用于缓存失效判断）</summary>
    public string FFmpegVersion { get; set; } = string.Empty;
}

/// <summary>一次完整的硬件检测报告</summary>
public class HardwareDetectionReport
{
    /// <summary>全部编码器（含不可用的）</summary>
    public List<HardwareEncoder> Encoders { get; set; } = [];

    /// <summary>检测时间</summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>FFmpeg 版本</summary>
    public string FFmpegVersion { get; set; } = string.Empty;

    /// <summary>FFmpeg 可执行文件路径</summary>
    public string FFmpegPath { get; set; } = string.Empty;

    /// <summary>平台名称</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>FFmpeg 编译时支持的硬件解码方式</summary>
    public List<string> Hwaccels { get; set; } = [];

    /// <summary>当前 FFmpeg 构建可用的滤镜（用于判断模板是否可执行）</summary>
    public List<string> SupportedFilters { get; set; } = [];

    /// <summary>判断某个滤镜是否可用</summary>
    public bool HasFilter(string name) =>
        SupportedFilters.Count == 0 || SupportedFilters.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>整体提示信息</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>可用的硬件编码器</summary>
    public List<HardwareEncoder> AvailableHardwareEncoders =>
        Encoders.Where(e => e.IsAvailable && e.IsHardware).ToList();

    /// <summary>是否存在任何可用的硬件编码器</summary>
    public bool HasHardwareAcceleration => Encoders.Any(e => e.IsAvailable && e.IsHardware);
}

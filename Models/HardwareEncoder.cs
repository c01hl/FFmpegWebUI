using LiteDB;

namespace FFmpegWebUI.Models;

/// <summary>硬件编码器</summary>
public class HardwareEncoder
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>编码器名称（如 h264_nvenc）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>显示名称（如 NVIDIA NVENC H.264）</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>编码器类型</summary>
    public EncoderType Type { get; set; } = EncoderType.Software;

    /// <summary>是否可用</summary>
    public bool IsAvailable { get; set; }

    /// <summary>支持的编码格式</summary>
    public List<string> SupportedCodecs { get; set; } = [];

    /// <summary>最后检测时间</summary>
    public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;

    /// <summary>检测失败原因（如果不可用）</summary>
    public string? UnavailableReason { get; set; }
}

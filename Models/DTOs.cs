using LiteDB;

namespace FFmpegWebUI.Models;

/// <summary>FFmpeg 信息</summary>
public record FFmpegInfo(
    string Version,
    string Path,
    string Configuration,
    List<string> SupportedFormats,
    List<string> SupportedCodecs,
    List<string> SupportedEncoders,
    List<string> SupportedHwaccels,
    List<string> SupportedFilters)
{
    /// <summary>主版本号（如 9），解析失败返回 0</summary>
    public int MajorVersion { get; init; }

    /// <summary>是否为自动探测到的路径（而非用户显式配置）</summary>
    public bool IsAutoDetected { get; init; }

    /// <summary>是否存在（false 表示 FFmpeg 不可用）</summary>
    public bool IsAvailable => !string.IsNullOrEmpty(Version) && Version != "Unknown";
}

/// <summary>媒体流信息</summary>
public record MediaStreamInfo(
    int Index,
    string CodecType,
    string? CodecName,
    string? CodecLongName,
    int Width,
    int Height,
    double FrameRate,
    double Duration,
    int Channels,
    int SampleRate,
    long BitRate,
    int Rotation,
    string? PixelFormat);

/// <summary>媒体文件信息</summary>
public record MediaInfo(
    string FilePath,
    double Duration,
    string Format,
    string? VideoCodec,
    string? AudioCodec,
    int Width,
    int Height,
    double FrameRate,
    long FileSize)
{
    /// <summary>格式的友好名称（如 "MP4"）</summary>
    public string FormatDisplay { get; init; } = "unknown";

    /// <summary>容器总码率</summary>
    public long BitRate { get; init; }

    /// <summary>音频声道数</summary>
    public int AudioChannels { get; init; }

    /// <summary>音频采样率</summary>
    public int AudioSampleRate { get; init; }

    /// <summary>视频旋转角度</summary>
    public int Rotation { get; init; }

    /// <summary>像素格式</summary>
    public string? PixelFormat { get; init; }

    /// <summary>是否包含视频流</summary>
    public bool HasVideo { get; init; }

    /// <summary>是否包含音频流</summary>
    public bool HasAudio { get; init; }

    /// <summary>字幕流数量</summary>
    public int SubtitleStreamCount { get; init; }

    /// <summary>全部流</summary>
    public List<MediaStreamInfo> Streams { get; init; } = [];

    /// <summary>时长文本</summary>
    public string DurationDisplay => Duration <= 0
        ? "-"
        : TimeSpan.FromSeconds(Duration).TotalHours >= 1
            ? TimeSpan.FromSeconds(Duration).ToString(@"h\:mm\:ss")
            : TimeSpan.FromSeconds(Duration).ToString(@"m\:ss");
}

/// <summary>转换进度</summary>
public record ConversionProgress(
    double Percentage,
    double CurrentTime,
    double TotalDuration,
    double? Speed,
    double? Eta,
    string RawOutput);

/// <summary>任务进度事件参数</summary>
public record TaskProgressEventArgs(
    ObjectId TaskId,
    double Progress,
    double? Speed,
    double? Eta);

/// <summary>任务状态变更事件参数</summary>
public record TaskStatusEventArgs(
    ObjectId TaskId,
    TaskStatus OldStatus,
    TaskStatus NewStatus,
    string? ErrorMessage);

/// <summary>任务日志追加事件参数</summary>
public record TaskLogEventArgs(ObjectId TaskId, string Line);

/// <summary>模板校验问题</summary>
public record TemplateIssue(
    TemplateIssueSeverity Severity,
    string Message,
    string? Field = null);

/// <summary>编码器可用性检查结果</summary>
public record EncoderCheckResult(
    string EncoderName,
    bool IsAvailable,
    string? Reason,
    string? Output);

/// <summary>模板试运行结果</summary>
public record TemplateTestResult(
    bool Success,
    string Message,
    string Command,
    string Output,
    TimeSpan Duration);

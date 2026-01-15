using LiteDB;

namespace FFmpegWebUI.Models;

/// <summary>FFmpeg 信息</summary>
public record FFmpegInfo(
    string Version,
    string Path,
    List<string> SupportedFormats,
    List<string> SupportedCodecs);

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
    long FileSize);

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

using LiteDB;

namespace FFmpegWebUI.Models;

/// <summary>转换任务</summary>
public class ConversionTask
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>输入文件路径</summary>
    public string InputPath { get; set; } = string.Empty;

    /// <summary>输出文件路径</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>使用的模板 ID</summary>
    public ObjectId TemplateId { get; set; } = ObjectId.Empty;

    /// <summary>实际执行的 FFmpeg 命令</summary>
    public string ActualCommand { get; set; } = string.Empty;

    /// <summary>任务状态</summary>
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    /// <summary>进度百分比 (0-100)</summary>
    public double Progress { get; set; }

    /// <summary>输入文件总时长（秒）</summary>
    public double TotalDuration { get; set; }

    /// <summary>当前处理时间（秒）</summary>
    public double CurrentTime { get; set; }

    /// <summary>预估剩余时间（秒）</summary>
    public double? EstimatedTimeRemaining { get; set; }

    /// <summary>处理速度（倍速）</summary>
    public double? ProcessingSpeed { get; set; }

    /// <summary>FFmpeg 输出日志</summary>
    public string LogOutput { get; set; } = string.Empty;

    /// <summary>错误信息（如果失败）</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>开始执行时间</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>完成时间</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>输入文件大小（字节）</summary>
    public long InputFileSize { get; set; }

    /// <summary>输出文件大小（字节，完成后填充）</summary>
    public long? OutputFileSize { get; set; }

    /// <summary>批处理任务 ID（如果属于批处理）</summary>
    public ObjectId? BatchId { get; set; }

    /// <summary>创建任务时用户选定的模板参数值（重试时复用）</summary>
    public Dictionary<string, string>? TemplateParameters { get; set; }

    /// <summary>使用的模板名称快照（模板被删除后仍可展示）</summary>
    public string TemplateName { get; set; } = string.Empty;

    /// <summary>是否为「重试」产生的任务（用于界面区分）</summary>
    public ObjectId? RetryOfTaskId { get; set; }

    /// <summary>
    /// 输出路径是否已经由界面完成冲突解析。
    /// 为 true 时执行层不再套用「文件已存在时的处理方式」，避免把用户刚选好的路径改名。
    /// </summary>
    public bool OutputPathResolved { get; set; }

    /// <summary>是否允许覆盖已存在的输出文件（仅当 OutputPathResolved 为 true 时有意义）</summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>耗时（秒），未开始或未结束时为 null</summary>
    public double? DurationSeconds => StartedAt.HasValue && CompletedAt.HasValue
        ? (CompletedAt.Value - StartedAt.Value).TotalSeconds
        : null;
}

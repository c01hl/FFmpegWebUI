using LiteDB;

namespace FFmpegWebUI.Models;

/// <summary>批量任务</summary>
public class BatchTask
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>批处理名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>使用的模板 ID</summary>
    public ObjectId TemplateId { get; set; }

    /// <summary>总文件数</summary>
    public int TotalFiles { get; set; }

    /// <summary>已完成文件数</summary>
    public int CompletedFiles { get; set; }

    /// <summary>失败文件数</summary>
    public int FailedFiles { get; set; }

    /// <summary>批处理状态</summary>
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>完成时间</summary>
    public DateTime? CompletedAt { get; set; }
}

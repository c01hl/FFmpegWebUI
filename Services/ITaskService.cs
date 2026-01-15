using FFmpegWebUI.Models;
using LiteDB;
using TaskStatus = FFmpegWebUI.Models.TaskStatus;

namespace FFmpegWebUI.Services;

/// <summary>任务管理服务接口</summary>
public interface ITaskService
{
    /// <summary>创建转换任务</summary>
    Task<ConversionTask> CreateTaskAsync(string inputPath, string outputPath, ObjectId templateId);

    /// <summary>创建批量任务</summary>
    Task<BatchTask> CreateBatchTaskAsync(List<string> inputPaths, string outputDirectory, ObjectId templateId);

    /// <summary>开始执行任务</summary>
    Task StartTaskAsync(ObjectId taskId);

    /// <summary>取消任务</summary>
    Task CancelTaskAsync(ObjectId taskId);

    /// <summary>获取任务详情</summary>
    Task<ConversionTask?> GetTaskByIdAsync(ObjectId taskId);

    /// <summary>获取任务历史</summary>
    Task<List<ConversionTask>> GetTaskHistoryAsync(int limit = 50, TaskStatus? status = null);

    /// <summary>获取当前运行中的任务</summary>
    Task<ConversionTask?> GetRunningTaskAsync();

    /// <summary>清理历史任务</summary>
    Task<int> CleanupHistoryAsync(DateTime olderThan);

    /// <summary>任务进度变更事件</summary>
    event EventHandler<TaskProgressEventArgs>? TaskProgressChanged;

    /// <summary>任务状态变更事件</summary>
    event EventHandler<TaskStatusEventArgs>? TaskStatusChanged;
}

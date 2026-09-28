using FFmpegWebUI.Models;
using LiteDB;
using TaskStatus = FFmpegWebUI.Models.TaskStatus;

namespace FFmpegWebUI.Services;

/// <summary>任务管理服务接口</summary>
public interface ITaskService
{
    /// <summary>创建转换任务（写入数据库，状态为待执行）</summary>
    /// <param name="overwriteExisting">
    /// 非 null 表示输出路径已由界面完成冲突解析：执行层不再套用全局的「文件已存在时的处理方式」。
    /// </param>
    Task<ConversionTask> CreateTaskAsync(
        string inputPath,
        string outputPath,
        ObjectId templateId,
        Dictionary<string, string>? parameters = null,
        bool? overwriteExisting = null);

    /// <summary>创建批量任务及其子任务</summary>
    Task<BatchTask> CreateBatchTaskAsync(
        List<string> inputPaths,
        string outputDirectory,
        ObjectId templateId,
        Dictionary<string, string>? parameters = null);

    /// <summary>开始执行任务（立即返回，任务在后台队列中执行）</summary>
    Task<bool> StartTaskAsync(ObjectId taskId);

    /// <summary>创建并立即执行一个批量任务</summary>
    Task<BatchTask> StartBatchTaskAsync(
        List<string> inputPaths,
        string outputDirectory,
        ObjectId templateId,
        Dictionary<string, string>? parameters = null);

    /// <summary>取消任务</summary>
    /// <param name="graceful">true 时先发送 'q' 让 FFmpeg 写出完整文件</param>
    Task<bool> CancelTaskAsync(ObjectId taskId, bool graceful = false);

    /// <summary>取消整个批次</summary>
    Task CancelBatchAsync(ObjectId batchId);

    /// <summary>获取任务详情</summary>
    Task<ConversionTask?> GetTaskByIdAsync(ObjectId taskId);

    /// <summary>获取任务历史（按创建时间倒序）</summary>
    Task<List<ConversionTask>> GetTaskHistoryAsync(int limit = 50, TaskStatus? status = null);

    /// <summary>获取正在运行的任务</summary>
    Task<List<ConversionTask>> GetRunningTasksAsync();

    /// <summary>获取等待中的任务</summary>
    Task<List<ConversionTask>> GetPendingTasksAsync();

    /// <summary>获取某批次的任务</summary>
    Task<List<ConversionTask>> GetBatchTasksAsync(ObjectId batchId);

    /// <summary>重新执行一个失败/取消/完成的任务（新建任务并复用原参数）</summary>
    Task<ConversionTask?> RetryTaskAsync(ObjectId taskId);

    /// <summary>删除任务记录</summary>
    Task<int> DeleteTasksAsync(IEnumerable<ObjectId> taskIds);

    /// <summary>按状态批量删除任务记录</summary>
    Task<int> DeleteTasksByStatusAsync(TaskStatus status);

    /// <summary>清理早于指定时间的历史任务</summary>
    Task<int> CleanupHistoryAsync(DateTime olderThan);

    /// <summary>清空全部历史（保留运行中/等待中的任务）</summary>
    Task<int> ClearHistoryAsync();

    /// <summary>应用启动时把上次异常退出的「运行中」任务标记为失败</summary>
    int RecoverOrphanedTasks();

    /// <summary>队列中等待执行的任务数</summary>
    int QueuedCount { get; }

    /// <summary>正在执行的任务数</summary>
    int ActiveCount { get; }

    /// <summary>任务进度变更事件</summary>
    event EventHandler<TaskProgressEventArgs>? TaskProgressChanged;

    /// <summary>任务状态变更事件</summary>
    event EventHandler<TaskStatusEventArgs>? TaskStatusChanged;

    /// <summary>任务日志追加事件</summary>
    event EventHandler<TaskLogEventArgs>? TaskLogAppended;
}

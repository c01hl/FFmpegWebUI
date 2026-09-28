using FFmpegWebUI.Data;
using FFmpegWebUI.Models;
using LiteDB;
using TaskStatus = FFmpegWebUI.Models.TaskStatus;

namespace FFmpegWebUI.Services;

/// <summary>
/// 任务管理服务（单例）。
/// 只负责任务的创建/查询/删除，实际执行交给 <see cref="ConversionQueue"/>，
/// 因此页面可以在任意时刻跳转而不影响正在进行的转换。
/// </summary>
public sealed class TaskService : ITaskService
{
    public TaskService(
        ILiteDbContext db,
        IFFmpegService ffmpegService,
        ITemplateService templateService,
        IFileService fileService,
        ISettingsService settingsService,
        ConversionQueue queue)
    {
        Db = db;
        FFmpegService = ffmpegService;
        TemplateService = templateService;
        FileService = fileService;
        SettingsService = settingsService;
        Queue = queue;

        Queue.TaskProgressChanged += (_, e) => TaskProgressChanged?.Invoke(this, e);
        Queue.TaskStatusChanged += (_, e) => TaskStatusChanged?.Invoke(this, e);
        Queue.TaskLogAppended += (_, e) => TaskLogAppended?.Invoke(this, e);
    }

    private ILiteDbContext Db { get; }
    private IFFmpegService FFmpegService { get; }
    private ITemplateService TemplateService { get; }
    private IFileService FileService { get; }
    private ISettingsService SettingsService { get; }
    private ConversionQueue Queue { get; }

    public event EventHandler<TaskProgressEventArgs>? TaskProgressChanged;
    public event EventHandler<TaskStatusEventArgs>? TaskStatusChanged;
    public event EventHandler<TaskLogEventArgs>? TaskLogAppended;

    public int QueuedCount => Queue.QueuedCount;
    public int ActiveCount => Queue.ActiveCount;

    // ────────────────────────────────────────────────────────────────
    // 创建
    // ────────────────────────────────────────────────────────────────

    public async Task<ConversionTask> CreateTaskAsync(
        string inputPath,
        string outputPath,
        ObjectId templateId,
        Dictionary<string, string>? parameters = null,
        bool? overwriteExisting = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("输入文件路径不能为空", nameof(inputPath));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("输出文件路径不能为空", nameof(outputPath));

        var template = await TemplateService.GetTemplateByIdAsync(templateId).ConfigureAwait(false)
            ?? throw new ArgumentException("模板不存在或已被删除", nameof(templateId));

        if (!FileService.IsFileAccessible(inputPath))
            throw new FileNotFoundException($"输入文件不存在或不可读：{inputPath}");

        var mediaInfo = await FFmpegService.GetMediaInfoAsync(inputPath).ConfigureAwait(false);

        var task = new ConversionTask
        {
            InputPath = AppPaths.ToAbsolute(inputPath),
            OutputPath = AppPaths.ToAbsolute(outputPath),
            TemplateId = templateId,
            TemplateName = template.Name,
            TemplateParameters = parameters == null ? null : new Dictionary<string, string>(parameters),
            OutputPathResolved = overwriteExisting.HasValue,
            OverwriteExisting = overwriteExisting ?? true,
            Status = TaskStatus.Pending,
            TotalDuration = mediaInfo?.Duration ?? 0,
            InputFileSize = FileService.GetFileSize(inputPath),
            CreatedAt = DateTime.UtcNow
        };

        Db.Tasks.Insert(task);
        return task;
    }

    public async Task<BatchTask> CreateBatchTaskAsync(
        List<string> inputPaths,
        string outputDirectory,
        ObjectId templateId,
        Dictionary<string, string>? parameters = null)
    {
        if (inputPaths.Count == 0)
            throw new ArgumentException("批量任务至少需要一个文件", nameof(inputPaths));

        var template = await TemplateService.GetTemplateByIdAsync(templateId).ConfigureAwait(false)
            ?? throw new ArgumentException("模板不存在或已被删除", nameof(templateId));

        var batch = new BatchTask
        {
            Name = $"批量任务 - {DateTime.Now:yyyy-MM-dd HH:mm}",
            TemplateId = templateId,
            TotalFiles = inputPaths.Count,
            Status = TaskStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        Db.BatchTasks.Insert(batch);

        var extension = string.IsNullOrWhiteSpace(template.OutputExtension)
            ? "mp4"
            : template.OutputExtension.TrimStart('.');

        // 批量处理与单文件转换使用同一套命名规则，避免「设置改了但批量不生效」
        var settings = (await SettingsService.GetSettingsAsync().ConfigureAwait(false)).Normalize();
        var suffix = settings.OutputNaming == OutputNamingRule.Suffix ? settings.OutputSuffix : null;

        foreach (var inputPath in inputPaths)
        {
            var outputPath = FileService.GenerateOutputPath(inputPath, outputDirectory, extension, suffix);
            var mediaInfo = await FFmpegService.GetMediaInfoAsync(inputPath).ConfigureAwait(false);

            var task = new ConversionTask
            {
                InputPath = AppPaths.ToAbsolute(inputPath),
                OutputPath = AppPaths.ToAbsolute(outputPath),
                TemplateId = templateId,
                TemplateName = template.Name,
                TemplateParameters = parameters == null ? null : new Dictionary<string, string>(parameters),
                Status = TaskStatus.Pending,
                TotalDuration = mediaInfo?.Duration ?? 0,
                InputFileSize = FileService.GetFileSize(inputPath),
                BatchId = batch.Id,
                CreatedAt = DateTime.UtcNow
            };
            Db.Tasks.Insert(task);
        }

        return batch;
    }

    // ────────────────────────────────────────────────────────────────
    // 执行
    // ────────────────────────────────────────────────────────────────

    public Task<bool> StartTaskAsync(ObjectId taskId)
    {
        var task = Db.Tasks.FindById(taskId);
        if (task == null) return Task.FromResult(false);

        if (task.Status is not (TaskStatus.Pending or TaskStatus.Failed or TaskStatus.Cancelled or TaskStatus.Skipped))
        {
            return Task.FromResult(false);
        }

        // 重置为待执行，之后交由队列处理
        task.Status = TaskStatus.Pending;
        task.Progress = 0;
        task.CurrentTime = 0;
        task.EstimatedTimeRemaining = null;
        task.ProcessingSpeed = null;
        task.ErrorMessage = null;
        task.CompletedAt = null;
        task.LogOutput = string.Empty;
        Db.Tasks.Update(task);

        return Task.FromResult(Queue.Enqueue(taskId));
    }

    public async Task<BatchTask> StartBatchTaskAsync(
        List<string> inputPaths,
        string outputDirectory,
        ObjectId templateId,
        Dictionary<string, string>? parameters = null)
    {
        var batch = await CreateBatchTaskAsync(inputPaths, outputDirectory, templateId, parameters).ConfigureAwait(false);

        var tasks = Db.Tasks.Query()
            .Where(t => t.BatchId == batch.Id && t.Status == TaskStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .ToList();

        foreach (var task in tasks)
        {
            Queue.Enqueue(task.Id);
        }

        batch.Status = TaskStatus.Running;
        Db.BatchTasks.Update(batch);
        return batch;
    }

    public Task<bool> CancelTaskAsync(ObjectId taskId, bool graceful = false) =>
        Queue.CancelAsync(taskId, graceful);

    public Task CancelBatchAsync(ObjectId batchId) => Queue.CancelBatchAsync(batchId);

    // ────────────────────────────────────────────────────────────────
    // 查询
    // ────────────────────────────────────────────────────────────────

    public Task<ConversionTask?> GetTaskByIdAsync(ObjectId taskId) =>
        // LiteDB 的 FindById 未标注可空性，这里显式声明返回类型
        Task.FromResult<ConversionTask?>(Db.Tasks.FindById(taskId));

    public Task<List<ConversionTask>> GetTaskHistoryAsync(int limit = 50, TaskStatus? status = null)
    {
        var query = Db.Tasks.Query();
        if (status.HasValue)
        {
            var snapshot = status.Value;
            query = query.Where(t => t.Status == snapshot);
        }

        var tasks = query
            .OrderByDescending(t => t.CreatedAt)
            .Limit(Math.Max(1, limit))
            .ToList();

        return Task.FromResult(tasks);
    }

    public Task<List<ConversionTask>> GetRunningTasksAsync()
    {
        var tasks = Db.Tasks.Query()
            .Where(t => t.Status == TaskStatus.Running)
            .OrderByDescending(t => t.CreatedAt)
            .ToList();
        return Task.FromResult(tasks);
    }

    public Task<List<ConversionTask>> GetPendingTasksAsync()
    {
        var tasks = Db.Tasks.Query()
            .Where(t => t.Status == TaskStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .ToList();
        return Task.FromResult(tasks);
    }

    public Task<List<ConversionTask>> GetBatchTasksAsync(ObjectId batchId)
    {
        var tasks = Db.Tasks.Query()
            .Where(t => t.BatchId == batchId)
            .OrderBy(t => t.CreatedAt)
            .ToList();
        return Task.FromResult(tasks);
    }

    // ────────────────────────────────────────────────────────────────
    // 重试 / 删除
    // ────────────────────────────────────────────────────────────────

    public async Task<ConversionTask?> RetryTaskAsync(ObjectId taskId)
    {
        var source = Db.Tasks.FindById(taskId);
        if (source == null) return null;

        // 同一批次里已有等待/运行中的同名任务时不重复创建
        var retry = new ConversionTask
        {
            InputPath = source.InputPath,
            OutputPath = source.OutputPath,
            TemplateId = source.TemplateId,
            TemplateName = source.TemplateName,
            TemplateParameters = source.TemplateParameters == null
                ? null
                : new Dictionary<string, string>(source.TemplateParameters),
            Status = TaskStatus.Pending,
            TotalDuration = source.TotalDuration,
            InputFileSize = source.InputFileSize,
            BatchId = source.BatchId,
            RetryOfTaskId = source.Id,
            CreatedAt = DateTime.UtcNow
        };

        Db.Tasks.Insert(retry);
        Queue.Enqueue(retry.Id);
        return retry;
    }

    public Task<int> DeleteTasksAsync(IEnumerable<ObjectId> taskIds)
    {
        var ids = taskIds.ToList();
        var removed = 0;

        foreach (var id in ids)
        {
            var task = Db.Tasks.FindById(id);
            if (task == null) continue;

            // 运行中的任务不允许直接删除
            if (task.Status == TaskStatus.Running)
            {
                _ = Queue.CancelAsync(id, graceful: false);
                continue;
            }

            if (Db.Tasks.Delete(id)) removed++;
        }

        return Task.FromResult(removed);
    }

    public Task<int> DeleteTasksByStatusAsync(TaskStatus status)
    {
        var removed = Db.Tasks.DeleteMany(t => t.Status == status);
        return Task.FromResult(removed);
    }

    public Task<int> CleanupHistoryAsync(DateTime olderThan)
    {
        var count = Db.Tasks.DeleteMany(t =>
            t.CreatedAt < olderThan &&
            (t.Status == TaskStatus.Completed || t.Status == TaskStatus.Failed ||
             t.Status == TaskStatus.Cancelled || t.Status == TaskStatus.Skipped));

        // 顺带清理已经没有子任务的批次记录
        var staleBatches = Db.BatchTasks.Query().Where(b => b.CreatedAt < olderThan).ToList();
        foreach (var batch in staleBatches)
        {
            var remaining = Db.Tasks.Query().Where(t => t.BatchId == batch.Id).Count();
            if (remaining == 0) Db.BatchTasks.Delete(batch.Id);
        }

        return Task.FromResult(count);
    }

    public Task<int> ClearHistoryAsync()
    {
        var count = Db.Tasks.DeleteMany(t =>
            t.Status == TaskStatus.Completed || t.Status == TaskStatus.Failed ||
            t.Status == TaskStatus.Cancelled || t.Status == TaskStatus.Skipped);

        Db.BatchTasks.DeleteAll();
        return Task.FromResult(count);
    }

    public int RecoverOrphanedTasks() => Queue.RecoverOrphanedTasks();
}

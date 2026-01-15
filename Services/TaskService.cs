using FFmpegWebUI.Data;
using FFmpegWebUI.Models;
using LiteDB;
using TaskStatus = FFmpegWebUI.Models.TaskStatus;

namespace FFmpegWebUI.Services;

/// <summary>任务管理服务实现</summary>
public class TaskService(
    ILiteDbContext db,
    IFFmpegService ffmpegService,
    ITemplateService templateService,
    IFileService fileService) : ITaskService
{
    private CancellationTokenSource? _currentCts;

    public event EventHandler<TaskProgressEventArgs>? TaskProgressChanged;
    public event EventHandler<TaskStatusEventArgs>? TaskStatusChanged;

    public async Task<ConversionTask> CreateTaskAsync(string inputPath, string outputPath, ObjectId templateId)
    {
        var template = await templateService.GetTemplateByIdAsync(templateId)
            ?? throw new ArgumentException("Template not found", nameof(templateId));

        var mediaInfo = await ffmpegService.GetMediaInfoAsync(inputPath);

        var task = new ConversionTask
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            TemplateId = templateId,
            ActualCommand = ffmpegService.BuildCommand(template, inputPath, outputPath),
            Status = TaskStatus.Pending,
            TotalDuration = mediaInfo?.Duration ?? 0,
            InputFileSize = fileService.GetFileSize(inputPath),
            CreatedAt = DateTime.UtcNow
        };

        db.Tasks.Insert(task);
        return task;
    }

    public async Task<BatchTask> CreateBatchTaskAsync(List<string> inputPaths, string outputDirectory, ObjectId templateId)
    {
        var template = await templateService.GetTemplateByIdAsync(templateId)
            ?? throw new ArgumentException("Template not found", nameof(templateId));

        var batch = new BatchTask
        {
            Name = $"批量任务 - {DateTime.Now:yyyy-MM-dd HH:mm}",
            TemplateId = templateId,
            TotalFiles = inputPaths.Count,
            Status = TaskStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        db.BatchTasks.Insert(batch);

        // 为每个文件创建子任务
        foreach (var inputPath in inputPaths)
        {
            var outputPath = fileService.GenerateOutputPath(
                inputPath,
                outputDirectory,
                template.OutputExtension,
                "_converted");

            var mediaInfo = await ffmpegService.GetMediaInfoAsync(inputPath);

            var task = new ConversionTask
            {
                InputPath = inputPath,
                OutputPath = outputPath,
                TemplateId = templateId,
                ActualCommand = ffmpegService.BuildCommand(template, inputPath, outputPath),
                Status = TaskStatus.Pending,
                TotalDuration = mediaInfo?.Duration ?? 0,
                InputFileSize = fileService.GetFileSize(inputPath),
                BatchId = batch.Id,
                CreatedAt = DateTime.UtcNow
            };

            db.Tasks.Insert(task);
        }

        return batch;
    }

    public async Task StartTaskAsync(ObjectId taskId)
    {
        var task = db.Tasks.FindById(taskId)
            ?? throw new ArgumentException("Task not found", nameof(taskId));

        if (task.Status != TaskStatus.Pending)
        {
            throw new InvalidOperationException("Task is not in pending state");
        }

        // 更新状态为运行中
        var oldStatus = task.Status;
        task.Status = TaskStatus.Running;
        task.StartedAt = DateTime.UtcNow;
        db.Tasks.Update(task);

        OnTaskStatusChanged(task.Id, oldStatus, task.Status, null);

        _currentCts = new CancellationTokenSource();

        try
        {
            await ffmpegService.ExecuteConversionAsync(
                task,
                progress =>
                {
                    task.Progress = progress.Percentage;
                    task.CurrentTime = progress.CurrentTime;
                    task.ProcessingSpeed = progress.Speed;
                    task.EstimatedTimeRemaining = progress.Eta;
                    task.LogOutput += progress.RawOutput + Environment.NewLine;

                    db.Tasks.Update(task);
                    OnTaskProgressChanged(task.Id, progress.Percentage, progress.Speed, progress.Eta);
                },
                _currentCts.Token);

            // 任务完成
            task.Status = TaskStatus.Completed;
            task.Progress = 100;
            task.CompletedAt = DateTime.UtcNow;
            task.OutputFileSize = fileService.GetFileSize(task.OutputPath);
            db.Tasks.Update(task);

            OnTaskStatusChanged(task.Id, TaskStatus.Running, TaskStatus.Completed, null);

            // 更新批量任务状态
            await UpdateBatchTaskStatusAsync(task.BatchId);
        }
        catch (OperationCanceledException)
        {
            task.Status = TaskStatus.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
            db.Tasks.Update(task);

            OnTaskStatusChanged(task.Id, TaskStatus.Running, TaskStatus.Cancelled, "用户取消");
            await UpdateBatchTaskStatusAsync(task.BatchId);
        }
        catch (Exception ex)
        {
            task.Status = TaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.CompletedAt = DateTime.UtcNow;
            db.Tasks.Update(task);

            OnTaskStatusChanged(task.Id, TaskStatus.Running, TaskStatus.Failed, ex.Message);
            await UpdateBatchTaskStatusAsync(task.BatchId);
        }
        finally
        {
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    public async Task CancelTaskAsync(ObjectId taskId)
    {
        var task = db.Tasks.FindById(taskId);
        if (task == null) return;

        if (task.Status == TaskStatus.Running)
        {
            _currentCts?.Cancel();
            await ffmpegService.CancelTaskAsync(taskId);
        }
        else if (task.Status == TaskStatus.Pending)
        {
            var oldStatus = task.Status;
            task.Status = TaskStatus.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
            db.Tasks.Update(task);

            OnTaskStatusChanged(task.Id, oldStatus, TaskStatus.Cancelled, "用户取消");
        }
    }

    public Task<ConversionTask?> GetTaskByIdAsync(ObjectId taskId)
    {
        var task = db.Tasks.FindById(taskId);
        return Task.FromResult(task);
    }

    public Task<List<ConversionTask>> GetTaskHistoryAsync(int limit = 50, TaskStatus? status = null)
    {
        var query = db.Tasks.Query();

        if (status.HasValue)
        {
            query = query.Where(t => t.Status == status.Value);
        }

        var tasks = query
            .OrderByDescending(t => t.CreatedAt)
            .Limit(limit)
            .ToList();

        return Task.FromResult(tasks);
    }

    public Task<ConversionTask?> GetRunningTaskAsync()
    {
        var task = db.Tasks
            .Query()
            .Where(t => t.Status == TaskStatus.Running)
            .FirstOrDefault();

        return Task.FromResult(task);
    }

    public Task<int> CleanupHistoryAsync(DateTime olderThan)
    {
        var count = db.Tasks.DeleteMany(t => t.CreatedAt < olderThan);
        return Task.FromResult(count);
    }

    private Task UpdateBatchTaskStatusAsync(ObjectId? batchId)
    {
        if (batchId == null) return Task.CompletedTask;

        var batch = db.BatchTasks.FindById(batchId);
        if (batch == null) return Task.CompletedTask;

        var tasks = db.Tasks.Query().Where(t => t.BatchId == batchId).ToList();

        batch.CompletedFiles = tasks.Count(t => t.Status == TaskStatus.Completed);
        batch.FailedFiles = tasks.Count(t => t.Status == TaskStatus.Failed || t.Status == TaskStatus.Cancelled);

        if (tasks.All(t => t.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Cancelled))
        {
            batch.Status = batch.FailedFiles == 0 ? TaskStatus.Completed : TaskStatus.Failed;
            batch.CompletedAt = DateTime.UtcNow;
        }
        else if (tasks.Any(t => t.Status == TaskStatus.Running))
        {
            batch.Status = TaskStatus.Running;
        }

        db.BatchTasks.Update(batch);
        return Task.CompletedTask;
    }

    private void OnTaskProgressChanged(ObjectId taskId, double progress, double? speed, double? eta)
    {
        TaskProgressChanged?.Invoke(this, new TaskProgressEventArgs(taskId, progress, speed, eta));
    }

    private void OnTaskStatusChanged(ObjectId taskId, TaskStatus oldStatus, TaskStatus newStatus, string? errorMessage)
    {
        TaskStatusChanged?.Invoke(this, new TaskStatusEventArgs(taskId, oldStatus, newStatus, errorMessage));
    }
}

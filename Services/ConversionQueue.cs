using System.Collections.Concurrent;
using System.Threading.Channels;
using FFmpegWebUI.Data;
using FFmpegWebUI.Models;
using LiteDB;
using Microsoft.Extensions.Logging;
using TaskStatus = FFmpegWebUI.Models.TaskStatus;

namespace FFmpegWebUI.Services;

/// <summary>
/// 转换任务执行引擎（单例）。
/// 任务在后台队列中执行，因此<b>页面跳转、刷新甚至重新连接都不会中断转换</b>；
/// 数据库写入被节流，日志有长度上限，取消操作可以在任意页面生效。
/// </summary>
public sealed class ConversionQueue : IAsyncDisposable
{
    private readonly Channel<ObjectId> _queue = Channel.CreateUnbounded<ObjectId>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    private readonly ConcurrentDictionary<ObjectId, RunningTaskState> _running = new();
    private readonly ConcurrentDictionary<ObjectId, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<ObjectId, string> _pendingLogs = new();
    private readonly ConcurrentDictionary<ObjectId, DateTime> _lastPersisted = new();
    private readonly List<Task> _workers = [];
    private readonly Lock _workerGate = new();

    private volatile int _workerCount;

    /// <summary>
    /// 正在运行时完成任务。可空实现为：
    /// 传入的运行中任务在应用重启后没有对应进程，启动时需要被重置。
    /// </summary>
    private sealed class RunningTaskState
    {
        public required ConversionTask Task { get; init; }
        public required CommandTemplate? Template { get; init; }
        public required EncoderProfile? Encoder { get; set; }
        public required bool Overwrite { get; init; }
        public required DateTime StartedAt { get; init; }
        /// <summary>硬件解码失败后置为 true，后续重试不再注入 -hwaccel</summary>
        public bool HardwareDecodeDisabled { get; set; }

        /// <summary>本次尝试是否已经产出过数据（用于判断失败发生在初始化阶段还是中途）</summary>
        public bool ProducedAnyOutput;
    }

    public ConversionQueue(
        ILiteDbContext db,
        IFFmpegService ffmpegService,
        ITemplateService templateService,
        IFileService fileService,
        ISettingsService settingsService,
        IHardwareDetectionService hardwareDetection,
        ILogger<ConversionQueue> logger)
    {
        Db = db;
        FFmpegService = ffmpegService;
        TemplateService = templateService;
        FileService = fileService;
        SettingsService = settingsService;
        HardwareDetection = hardwareDetection;
        Logger = logger;
    }

    private ILiteDbContext Db { get; }
    private IFFmpegService FFmpegService { get; }
    private ITemplateService TemplateService { get; }
    private IFileService FileService { get; }
    private ISettingsService SettingsService { get; }
    private IHardwareDetectionService HardwareDetection { get; }
    private ILogger Logger { get; }

    /// <summary>进度变更</summary>
    public event EventHandler<TaskProgressEventArgs>? TaskProgressChanged;

    /// <summary>状态变更</summary>
    public event EventHandler<TaskStatusEventArgs>? TaskStatusChanged;

    /// <summary>日志追加</summary>
    public event EventHandler<TaskLogEventArgs>? TaskLogAppended;

    /// <summary>队列中等待的任务数</summary>
    public int QueuedCount => _queue.Reader.Count;

    /// <summary>正在执行的任务数</summary>
    public int ActiveCount => _running.Count;

    // ────────────────────────────────────────────────────────────────
    // 队列控制
    // ────────────────────────────────────────────────────────────────

    /// <summary>把任务加入执行队列（不阻塞调用方）</summary>
    public bool Enqueue(ObjectId taskId)
    {
        if (!_queue.Writer.TryWrite(taskId))
        {
            Logger.LogWarning("任务 {TaskId} 入队失败", taskId);
            return false;
        }
        EnsureWorkers();
        return true;
    }

    /// <summary>按设置中的并发数动态保证 worker 数量</summary>
    private void EnsureWorkers()
    {
        var desired = Math.Clamp(SettingsService.GetSettingsAsync().GetAwaiter().GetResult().Normalize().MaxConcurrentTasks, 1, 16);
        lock (_workerGate)
        {
            while (_workerCount < desired)
            {
                _workerCount++;
                _workers.Add(Task.Run(() => WorkerLoopAsync()));
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var taskId in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await ProcessTaskAsync(taskId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "执行任务 {TaskId} 时发生未处理异常", taskId);
                    TryMarkFailed(taskId, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "转换队列 worker 异常退出");
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 单个任务执行
    // ────────────────────────────────────────────────────────────────

    private async Task ProcessTaskAsync(ObjectId taskId)
    {
        var task = Db.Tasks.FindById(taskId);
        if (task == null) return;
        if (task.Status is not (TaskStatus.Pending or TaskStatus.Running))
        {
            // 已被用户取消/跳过
            return;
        }

        var settings = (await SettingsService.GetSettingsAsync().ConfigureAwait(false)).Normalize();
        var template = await TemplateService.GetTemplateByIdAsync(task.TemplateId).ConfigureAwait(false);
        if (template == null)
        {
            TryMarkFailed(taskId, "模板不存在，可能已被删除。");
            return;
        }

        var mediaInfo = await FFmpegService.GetMediaInfoAsync(task.InputPath).ConfigureAwait(false);

        // 输出冲突策略：绝不把决定权交给 FFmpeg 的交互提问。
        // 界面若已解析过路径（用户明确选了「覆盖」或「保留两者」），这里不再改动路径，
        // 但仍会做「输出 == 输入」的安全检查。
        OutputPathResolution resolution;
        if (task.OutputPathResolved)
        {
            resolution = new OutputPathResolution
            {
                OutputPath = task.OutputPath,
                Overwrite = task.OverwriteExisting,
                Skip = false,
                Renamed = false
            };

            if (OutputPathResolver.PathsEqual(resolution.OutputPath, task.InputPath))
            {
                resolution = OutputPathResolver.Rename(resolution.OutputPath) with
                {
                    Note = "输出路径与输入文件相同，已自动改名以避免覆盖源文件。"
                };
            }
        }
        else
        {
            resolution = OutputPathResolver.Resolve(task.OutputPath, settings.FileExistsAction, task.InputPath);
        }
        if (resolution.Skip)
        {
            task.Status = TaskStatus.Skipped;
            task.CompletedAt = DateTime.UtcNow;
            task.Progress = 0;
            task.LogOutput = resolution.Note ?? "已跳过";
            task.OutputPath = resolution.OutputPath;
            Db.Tasks.Update(task);
            NotifyStatus(task.Id, TaskStatus.Running, TaskStatus.Skipped, resolution.Note);
            return;
        }

        task.OutputPath = resolution.OutputPath;
        task.TotalDuration = mediaInfo?.Duration ?? task.TotalDuration;
        task.InputFileSize = FileService.GetFileSize(task.InputPath);

        // 确保输出目录存在
        try
        {
            var directory = Path.GetDirectoryName(task.OutputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            TryMarkFailed(taskId, $"无法创建输出目录：{ex.Message}");
            return;
        }

        if (!OutputPathResolver.IsOutputPathWritable(task.OutputPath))
        {
            TryMarkFailed(taskId, $"输出目录不可写：{Path.GetDirectoryName(task.OutputPath)}");
            return;
        }

        EncoderProfile? encoder;
        try
        {
            encoder = await ResolveEncoderAsync(template, settings).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            TryMarkFailed(taskId, ex.Message);
            return;
        }

        var built = BuildCommand(template, task, mediaInfo, encoder, settings);

        task.ActualCommand = built.CommandLine;
        task.Status = TaskStatus.Running;
        task.StartedAt = DateTime.UtcNow;
        task.ErrorMessage = null;
        task.Progress = 0;
        task.LogOutput = string.Empty;
        Db.Tasks.Update(task);

        if (built.Warnings.Count > 0)
        {
            AppendLog(task.Id, "提示：" + string.Join("；", built.Warnings));
        }
        if (resolution.Note != null)
        {
            AppendLog(task.Id, resolution.Note);
        }

        NotifyStatus(task.Id, TaskStatus.Pending, TaskStatus.Running, null);

        var state = new RunningTaskState
        {
            Task = task,
            Template = template,
            Encoder = encoder,
            Overwrite = resolution.Overwrite,
            StartedAt = DateTime.UtcNow
        };
        _running[task.Id] = state;

        var cts = new CancellationTokenSource();
        _cancellations[task.Id] = cts;

        try
        {
            var result = await RunOnceAsync(task, state, built.Arguments, settings, cts.Token).ConfigureAwait(false);

            var hardwareDecodeEnabled = settings.PreferHardwareDecoding
                                        && !state.HardwareDecodeDisabled
                                        && mediaInfo is { HasVideo: true };

            // 每次尝试前重置，用于判断失败是否发生在初始化阶段
            state.ProducedAnyOutput = false;

            // 回退第 1 步：先关掉硬件解码重试。
            // 硬件解码最容易因为驱动/设备问题失败，而它带来的只是省 CPU；
            // 优先丢掉它、保留硬件编码（有实质性能收益）。
            if (!result.Success && !result.Cancelled && settings.AutoFallbackToSoftware &&
                hardwareDecodeEnabled && ShouldFallbackToSoftware(result, state.ProducedAnyOutput))
            {
                AppendLog(task.Id, "⚠️ 硬件解码失败，正在改用软件解码重试……");
                AppendLog(task.Id, "失败原因：" + FirstLine(result.Diagnostic));

                state.HardwareDecodeDisabled = true;
                var retryBuilt = BuildCommand(template, task, mediaInfo, encoder, settings, disableHardwareDecode: true);
                task.ActualCommand = retryBuilt.CommandLine;
                Db.Tasks.Update(task);

                result = await RunOnceAsync(task, state, retryBuilt.Arguments, settings, cts.Token).ConfigureAwait(false);
            }

            // 回退第 2 步：仍然失败且用的是硬件编码器时，改用软件编码
            if (!result.Success && !result.Cancelled && settings.AutoFallbackToSoftware &&
                encoder is { IsHardware: true } && ShouldFallbackToSoftware(result, state.ProducedAnyOutput))
            {
                var software = EncoderCatalog.SoftwareFallback(encoder.Codec);
                if (software != null && !string.Equals(software.Name, encoder.Name, StringComparison.Ordinal))
                {
                    AppendLog(task.Id, $"⚠️ 硬件编码器 {encoder.Name} 执行失败，正在改用软件编码 {software.Name} 重试……");
                    AppendLog(task.Id, "失败原因：" + FirstLine(result.Diagnostic));

                    var fallbackBuilt = BuildCommand(template, task, mediaInfo, software, settings, state.HardwareDecodeDisabled);
                    state.Encoder = software;
                    _running[task.Id] = state;
                    task.ActualCommand = fallbackBuilt.CommandLine;
                    Db.Tasks.Update(task);

                    state.ProducedAnyOutput = false;
                    result = await RunOnceAsync(task, state, fallbackBuilt.Arguments, settings, cts.Token).ConfigureAwait(false);
                }
            }

            FinalizeTask(task, result, settings);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "任务 {TaskId} 执行失败", task.Id);
            TryMarkFailed(task.Id, ex.Message);
        }
        finally
        {
            _running.TryRemove(task.Id, out _);
            _cancellations.TryRemove(task.Id, out _);
            _pendingLogs.TryRemove(task.Id, out _);
            _lastPersisted.TryRemove(task.Id, out _);
            try { cts.Dispose(); } catch { }
            FlushLogs(task.Id, force: true);
        }
    }

    private async Task<FFmpegRunResult> RunOnceAsync(
        ConversionTask task,
        RunningTaskState state,
        IReadOnlyList<string> arguments,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var result = await FFmpegService.RunAsync(new FFmpegRunRequest
        {
            TaskId = task.Id,
            Arguments = arguments,
            TotalDuration = task.TotalDuration,
            OutputPath = task.OutputPath,
            Overwrite = state.Overwrite,
            CleanupPartialOutput = true,
            OnProgress = progress =>
            {
                state.ProducedAnyOutput = true;
                task.Progress = progress.Percentage;
                task.CurrentTime = progress.CurrentTime;
                task.ProcessingSpeed = progress.Speed;
                task.EstimatedTimeRemaining = progress.Eta;

                NotifyProgress(task.Id, progress.Percentage, progress.Speed, progress.Eta);
                AppendLog(task.Id, progress.RawOutput, throttle: true);
            },
            OnLogLine = line => AppendLog(task.Id, line)
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// 判断失败是否属于「硬件编码器不可用」而不是素材/参数问题。
    /// 只要出现了典型的硬件初始化错误关键字，或者整个任务一帧都没有产出，就认为可以回退。
    /// </summary>
    private static bool ShouldFallbackToSoftware(FFmpegRunResult result, bool producedAnyOutput)
    {
        if (result.TimedOut) return false;

        // 一帧都没编出来 → 基本可以确定是硬件初始化失败，回退是安全的
        if (!producedAnyOutput) return true;

        // 已经产出过数据还失败，则只在报错明确指向硬件问题时回退
        var diagnostic = result.Diagnostic ?? string.Empty;
        string[] markers =
        [
            "Cannot load",
            "Device creation failed",
            "No capable devices found",
            "Error initializing",
            "Cannot open device",
            "Failed to initialise",
            "Failed to initialize",
            "No device available",
            "not available",
            "Function not implemented",
            "Operation not permitted",
            "Invalid argument",
            "Unrecognized option",
            "Option not found",
            "Error while opening encoder",
            "Could not open encoder",
            "Generic error in an external library",
            "OpenEncodeSessionEx failed",
            "No NVENC capable devices found",
            "Error creating an internal MFX session",
            "unsupported",
            "MFX_ERR",
        ];

        return markers.Any(m => diagnostic.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private void FinalizeTask(ConversionTask task, FFmpegRunResult result, AppSettings settings)
    {
        var oldStatus = task.Status;

        if (result.Cancelled)
        {
            task.Status = TaskStatus.Cancelled;
            task.ErrorMessage = "用户取消";
            AppendLog(task.Id, "任务已被取消。");
        }
        else if (result.Success)
        {
            task.Status = TaskStatus.Completed;
            task.Progress = 100;
            task.ErrorMessage = null;
        }
        else
        {
            task.Status = TaskStatus.Failed;
            task.ErrorMessage = result.TimedOut
                ? "执行超时"
                : $"FFmpeg 退出码 {result.ExitCode}";
            AppendLog(task.Id, "❌ 转换失败。FFmpeg 输出：");
            AppendLog(task.Id, result.Diagnostic);
        }

        task.CompletedAt = DateTime.UtcNow;
        task.OutputFileSize = result.Success ? FileService.GetFileSize(task.OutputPath) : null;

        // 先把缓冲区里的日志落库，再重新读取一遍，
        // 否则用手里这个可能已经过期的对象 Update 会把刚落库的日志覆盖掉
        FlushLogs(task.Id, force: true);

        var persisted = Db.Tasks.FindById(task.Id);
        if (persisted != null)
        {
            persisted.Status = task.Status;
            persisted.Progress = task.Progress;
            persisted.CurrentTime = task.CurrentTime;
            persisted.EstimatedTimeRemaining = null;
            persisted.ProcessingSpeed = null;
            persisted.ErrorMessage = task.ErrorMessage;
            persisted.CompletedAt = task.CompletedAt;
            persisted.OutputPath = task.OutputPath;
            persisted.OutputFileSize = task.OutputFileSize;
            persisted.LogOutput = TruncateLog(persisted.LogOutput, settings.MaxLogLines);
            Db.Tasks.Update(persisted);
        }
        else
        {
            task.LogOutput = TruncateLog(task.LogOutput, settings.MaxLogLines);
            Db.Tasks.Update(task);
        }

        NotifyStatus(task.Id, oldStatus, task.Status, task.ErrorMessage);
        UpdateBatchStatus(task.BatchId);
    }

    // ────────────────────────────────────────────────────────────────
    // 命令构建
    // ────────────────────────────────────────────────────────────────

    private BuiltCommand BuildCommand(
        CommandTemplate template,
        ConversionTask task,
        MediaInfo? mediaInfo,
        EncoderProfile? encoder,
        AppSettings settings,
        bool disableHardwareDecode = false)
    {
        var parameters = template.Parameters.ToDictionary(p => p.Name, p => p.DefaultValue ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

        // 任务创建时若已确定参数（重试场景），以任务上的值为准
        if (task.TemplateParameters != null)
        {
            foreach (var (key, value) in task.TemplateParameters)
            {
                parameters[key] = value;
            }
        }

        var context = new CommandContext
        {
            InputPath = task.InputPath,
            OutputPath = task.OutputPath,
            Parameters = parameters,
            Encoder = encoder,
            Media = mediaInfo,
            AudioEncoder = ResolveAudioEncoder(template, encoder),
            Threads = 0,
            HardwareDecodeArgs = settings.PreferHardwareDecoding && !disableHardwareDecode
                ? BuildHardwareDecodeArgs(mediaInfo)
                : string.Empty,
            GlobalArguments = settings.GlobalFFmpegArguments
        };

        return FFmpegService.BuildCommand(template, context);
    }

    /// <summary>决定本次要用的视频编码器</summary>
    private async Task<EncoderProfile?> ResolveEncoderAsync(CommandTemplate template, AppSettings settings)
    {
        // 模板里没有 {encoder} 占位符，也没有声明硬件加速需求 → 使用模板自带的编码器
        var usesEncoderPlaceholder = template.CommandArgs.Contains("{encoder}", StringComparison.OrdinalIgnoreCase);
        var needsEncoder = usesEncoderPlaceholder ||
                           template.HardwareMode != HardwareAccelerationMode.None;

        if (!needsEncoder)
        {
            return null;
        }

        var codec = DetermineTargetCodec(template);
        var required = template.RequiredEncoder;
        var strict = template.HardwareMode == HardwareAccelerationMode.Required;

        var name = await HardwareDetection
            .GetRecommendedEncoderAsync(codec, required, strict)
            .ConfigureAwait(false);

        // 严格模式下没有可用编码器 → 抛出让上层给出明确错误。
        // 「模板写着 NVENC、实际用别的 GPU 跑」这种名不副实的结果比失败更糟：
        // 用户按模板名预期了输出特性，却拿到完全不同的东西。
        if (name is null)
        {
            var family = string.IsNullOrWhiteSpace(required)
                ? "指定的硬件编码器"
                : EncoderCatalog.FamilyDisplayName(EncoderCatalog.Find(required)?.Family ?? required);

            throw new InvalidOperationException(
                $"该模板要求使用 {family} 硬件编码，但本机不可用。" +
                "请改用「推荐」分类下会自动挑选编码器的模板，或在「设置 → 硬件加速能力」中确认本机支持的编码器。");
        }

        var profile = EncoderCatalog.Find(name)
                      ?? EncoderCatalog.SoftwareFallback(codec)
                      ?? EncoderCatalog.Find("libx264");

        // 检测结果显示该编码器只能用最简参数时，退化为不发送质量参数
        var detection = HardwareDetection.LastReport?.Encoders
            .FirstOrDefault(e => string.Equals(e.Name, profile?.Name, StringComparison.OrdinalIgnoreCase));

        if (detection is { SupportsQualityOptions: false } && profile != null)
        {
            profile = profile with { QualityArgs = null, PresetArgs = null };
        }

        return profile;
    }

    private static string DetermineTargetCodec(CommandTemplate template)
    {
        if (!string.IsNullOrWhiteSpace(template.TargetCodec))
        {
            return HardwareDetectionService.NormalizeCodec(template.TargetCodec);
        }

        // 从 RequiredEncoder 推断
        if (!string.IsNullOrWhiteSpace(template.RequiredEncoder))
        {
            var profile = EncoderCatalog.Find(template.RequiredEncoder);
            if (profile != null) return profile.Codec;
            var family = EncoderCatalog.ByFamily(template.RequiredEncoder).FirstOrDefault();
            if (family != null) return family.Codec;
        }

        // 从输出扩展名推断
        var codecFromExtension = HardwareDetectionService.NormalizeCodec(template.OutputExtension);
        if (codecFromExtension is "h264" or "hevc" or "av1" or "vp9") return codecFromExtension;

        // 从命令中的软件编码器推断
        foreach (var candidate in new[] { "libx265", "libx264", "libsvtav1", "libvpx-vp9", "libvpx" })
        {
            if (template.CommandArgs.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                var profile = EncoderCatalog.Find(candidate);
                if (profile != null) return profile.Codec;
            }
        }

        return template.OutputExtension.ToLowerInvariant() switch
        {
            "webm" => "vp9",
            "mkv" => "h264",
            "mov" => "h264",
            _ => "h264"
        };
    }

    private static string ResolveAudioEncoder(CommandTemplate template, EncoderProfile? videoEncoder)
    {
        foreach (var candidate in new[] { "libopus", "libmp3lame", "flac", "aac" })
        {
            if (template.CommandArgs.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return template.OutputExtension.ToLowerInvariant() switch
        {
            "webm" => "libopus",
            "mp3" => "libmp3lame",
            "flac" => "flac",
            "ogg" => "libopus",
            _ => "aac"
        };
    }

    private static string BuildHardwareDecodeArgs(MediaInfo? media)
    {
        if (media == null || !media.HasVideo) return string.Empty;
        // 只对常见编码使用硬件解码，且由用户显式开启
        var codec = media.VideoCodec?.ToLowerInvariant();
        var hwaccel = AppPaths.Platform switch
        {
            var p when p == System.Runtime.InteropServices.OSPlatform.OSX => "videotoolbox",
            var p when p == System.Runtime.InteropServices.OSPlatform.Windows => "d3d11va",
            _ => "vaapi"
        };
        return codec is "h264" or "hevc" or "av1" or "vp9" or "mpeg2video" or "vc1"
            ? $"-hwaccel {hwaccel}"
            : string.Empty;
    }

    // ────────────────────────────────────────────────────────────────
    // 取消
    // ────────────────────────────────────────────────────────────────

    /// <summary>请求取消任务。返回是否确实触发了取消。</summary>
    public async Task<bool> CancelAsync(ObjectId taskId, bool graceful)
    {
        var task = Db.Tasks.FindById(taskId);
        if (task == null) return false;

        if (task.Status == TaskStatus.Running)
        {
            if (_cancellations.TryGetValue(taskId, out var cts))
            {
                try
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // 任务刚好结束
                }
            }
            await FFmpegService.KillAsync(taskId, graceful).ConfigureAwait(false);

            if (graceful)
            {
                AppendLog(taskId, "已发送优雅退出指令（q），FFmpeg 会写完文件尾再退出。");
            }
            return true;
        }

        if (task.Status == TaskStatus.Pending)
        {
            task.Status = TaskStatus.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
            task.ErrorMessage = "用户取消";
            Db.Tasks.Update(task);
            NotifyStatus(taskId, TaskStatus.Pending, TaskStatus.Cancelled, "用户取消");
            UpdateBatchStatus(task.BatchId);
            return true;
        }

        return false;
    }

    /// <summary>取消整个批次的等待中任务</summary>
    public async Task CancelBatchAsync(ObjectId batchId)
    {
        var pending = Db.Tasks.Query()
            .Where(t => t.BatchId == batchId && (t.Status == TaskStatus.Pending || t.Status == TaskStatus.Running))
            .ToList();

        foreach (var task in pending)
        {
            await CancelAsync(task.Id, graceful: false).ConfigureAwait(false);
        }
        UpdateBatchStatus(batchId);
    }

    /// <summary>启动时把上次异常退出留下的「运行中」任务标记为失败</summary>
    public int RecoverOrphanedTasks()
    {
        try
        {
            var orphans = Db.Tasks.Query()
                .Where(t => t.Status == TaskStatus.Running)
                .ToList();

            foreach (var task in orphans)
            {
                task.Status = TaskStatus.Failed;
                task.ErrorMessage = "应用退出导致任务中断";
                task.CompletedAt = DateTime.UtcNow;
                task.EstimatedTimeRemaining = null;
                task.ProcessingSpeed = null;
                Db.Tasks.Update(task);
            }

            if (orphans.Count > 0) Logger.LogInformation("已清理 {Count} 个中断的任务", orphans.Count);
            return orphans.Count;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "清理中断任务失败");
            return 0;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 日志与状态持久化（节流）
    // ────────────────────────────────────────────────────────────────

    private void AppendLog(ObjectId taskId, string? line, bool throttle = false)
    {
        if (string.IsNullOrEmpty(line)) return;

        _pendingLogs.AddOrUpdate(taskId, line, (_, existing) => existing + Environment.NewLine + line);

        var now = DateTime.UtcNow;
        var shouldPersist = !throttle ||
                            !_lastPersisted.TryGetValue(taskId, out var last) ||
                            (now - last).TotalMilliseconds >= 700;

        if (shouldPersist) FlushLogs(taskId);

        // 无论是否落库，界面都能实时看到日志
        TaskLogAppended?.Invoke(this, new TaskLogEventArgs(taskId, line));
    }

    private void FlushLogs(ObjectId taskId, bool force = false)
    {
        if (!_pendingLogs.TryRemove(taskId, out var pending) || string.IsNullOrEmpty(pending)) return;

        try
        {
            var task = Db.Tasks.FindById(taskId);
            if (task == null) return;

            var settings = SettingsService.GetSettingsAsync().GetAwaiter().GetResult().Normalize();
            var combined = string.IsNullOrEmpty(task.LogOutput)
                ? pending
                : task.LogOutput + Environment.NewLine + pending;

            task.LogOutput = TruncateLog(combined, settings.MaxLogLines);
            Db.Tasks.Update(task);
            _lastPersisted[taskId] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "写入任务日志失败");
        }
    }

    /// <summary>只保留最后 N 行，避免长任务把数据库撑爆（原实现使用 += 拼接，长任务会 O(n²) 变慢）</summary>
    private static string TruncateLog(string? log, int maxLines)
    {
        if (string.IsNullOrEmpty(log)) return string.Empty;
        if (maxLines <= 0) return log;

        var lines = log.Split('\n');
        if (lines.Length <= maxLines) return log;

        var skipped = lines.Length - maxLines;
        return $"…（已省略早期 {skipped} 行日志）" + Environment.NewLine +
               string.Join('\n', lines.Skip(skipped));
    }

    private void TryMarkFailed(ObjectId taskId, string message)
    {
        try
        {
            var task = Db.Tasks.FindById(taskId);
            if (task == null) return;

            var oldStatus = task.Status;
            task.Status = TaskStatus.Failed;
            task.ErrorMessage = message;
            task.CompletedAt = DateTime.UtcNow;
            task.EstimatedTimeRemaining = null;
            task.ProcessingSpeed = null;
            Db.Tasks.Update(task);

            _running.TryRemove(taskId, out _);
            _cancellations.TryRemove(taskId, out _);
            NotifyStatus(taskId, oldStatus, TaskStatus.Failed, message);
            UpdateBatchStatus(task.BatchId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "标记任务失败状态时出错");
        }
    }

    private void UpdateBatchStatus(ObjectId? batchId)
    {
        if (batchId == null) return;

        try
        {
            var batch = Db.BatchTasks.FindById(batchId);
            if (batch == null) return;

            var tasks = Db.Tasks.Query().Where(t => t.BatchId == batchId).ToList();
            if (tasks.Count == 0) return;

            batch.TotalFiles = tasks.Count;
            batch.CompletedFiles = tasks.Count(t => t.Status == TaskStatus.Completed);
            batch.FailedFiles = tasks.Count(t => t.Status is TaskStatus.Failed or TaskStatus.Cancelled);

            var finished = tasks.All(t => t.Status is TaskStatus.Completed or TaskStatus.Failed
                or TaskStatus.Cancelled or TaskStatus.Skipped);

            if (finished)
            {
                batch.Status = batch.FailedFiles == 0 ? TaskStatus.Completed : TaskStatus.Failed;
                batch.CompletedAt ??= DateTime.UtcNow;
            }
            else if (tasks.Any(t => t.Status == TaskStatus.Running))
            {
                batch.Status = TaskStatus.Running;
            }

            Db.BatchTasks.Update(batch);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "更新批次状态失败");
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 事件
    // ────────────────────────────────────────────────────────────────

    private void NotifyProgress(ObjectId taskId, double progress, double? speed, double? eta) =>
        TaskProgressChanged?.Invoke(this, new TaskProgressEventArgs(taskId, progress, speed, eta));

    private void NotifyStatus(ObjectId taskId, TaskStatus oldStatus, TaskStatus newStatus, string? error) =>
        TaskStatusChanged?.Invoke(this, new TaskStatusEventArgs(taskId, oldStatus, newStatus, error));

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "未提供详细信息";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        return line.Length > 300 ? line[..300] : line;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        foreach (var cts in _cancellations.Values)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); } catch { }
        }
        try
        {
            await Task.WhenAny(Task.WhenAll(_workers), Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        }
        catch
        {
            // 关闭时的异常可以忽略
        }
    }
}

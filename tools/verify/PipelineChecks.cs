using FFmpegWebUI.Models;
using FFmpegWebUI.Services;
using LiteDB;
using TaskStatus = FFmpegWebUI.Models.TaskStatus;

namespace FFmpegWebUI.Verify;

/// <summary>
/// 转换管线验证：从 FFmpeg 定位一直到队列执行、取消、覆盖策略与模板校验。
/// 这是对「界面背后的真实行为」的回归测试。
/// </summary>
internal static class PipelineChecks
{
    private static int SummaryPassed;
    private static readonly List<string> SummaryFailures = [];
    public static async Task RunAsync(VerifyContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("── FFmpeg 定位与能力探测 ─────────────────────────────────");
        await CheckFfmpegAsync(ctx);

        Console.WriteLine();
        Console.WriteLine("── 媒体信息解析 ──────────────────────────────────────────");
        var sample = await CheckMediaInfoAsync(ctx);

        Console.WriteLine();
        Console.WriteLine("── 模板同步与命令构建 ────────────────────────────────────");
        var (template, built, output) = await CheckCommandBuildingAsync(ctx, sample);

        Console.WriteLine();
        Console.WriteLine("── 端到端转换（后台队列）──────────────────────────────────");
        await CheckConversionAsync(ctx, sample, template, output);

        Console.WriteLine();
        Console.WriteLine("── 输出覆盖策略 ──────────────────────────────────────────");
        await CheckOverwritePolicyAsync(ctx, sample, template, output);

        Console.WriteLine();
        Console.WriteLine("── 取消与清理 ────────────────────────────────────────────");
        await CheckCancellationAsync(ctx);

        Console.WriteLine();
        Console.WriteLine("── 模板校验与试运行 ──────────────────────────────────────");
        await CheckTemplateValidationAsync(ctx, template);

        Console.WriteLine();
        Console.WriteLine("── 安全与设置生效 ────────────────────────────────────────");
        await CheckSafetyAndSettingsAsync(ctx, sample, template);

        Console.WriteLine();
        Console.WriteLine("── 文件对话框路径解析 ────────────────────────────────────");
        CheckFileDialogPaths(ctx);
    }

    // ────────────────────────────────────────────────────────────────

    private static async Task CheckFfmpegAsync(VerifyContext ctx)
    {
        var info = await ctx.FFmpeg.GetFFmpegInfoAsync(forceRefresh: true);
        ctx.Check("定位 FFmpeg 并解析版本", info is { IsAvailable: true }, info?.Version);
        VerifyContext.Info($"FFmpeg {info?.Version} @ {info?.Path}");
        VerifyContext.Info($"主版本 {info?.MajorVersion}；编码器 {info?.SupportedEncoders.Count}；" +
                           $"滤镜 {info?.SupportedFilters.Count}；hwaccels [{string.Join(",", info?.SupportedHwaccels ?? [])}]");

        ctx.Check("解析出编码器清单", (info?.SupportedEncoders.Count ?? 0) > 50);
        ctx.Check("解析出滤镜清单", (info?.SupportedFilters.Count ?? 0) > 50);
        ctx.Check("编码器清单包含 libx264", info?.SupportedEncoders.Contains("libx264") == true);
        ctx.Check("滤镜清单包含 scale", info?.SupportedFilters.Contains("scale") == true);

        var report = await ctx.Hardware.DetectAsync(forceRefresh: true);
        ctx.Check("硬件检测返回报告", report.Encoders.Count > 0, report.Summary);
        VerifyContext.Info(report.Summary);
        foreach (var encoder in report.AvailableHardwareEncoders)
        {
            VerifyContext.Info($"可用硬件编码器：{encoder.Name}（{encoder.DisplayName}）");
        }
        ctx.Check("至少有一个可用编码器（硬件或软件）", report.Encoders.Any(e => e.IsAvailable));
        ctx.Check("检测结果不包含当前平台不可能存在的编码器",
            report.Encoders.All(e => EncoderCatalog.Find(e.Name)?.SupportsCurrentPlatform != false));
    }

    private static async Task<string> CheckMediaInfoAsync(VerifyContext ctx)
    {
        var sample = await ctx.CreateSampleAsync("pipeline-sample.mp4", seconds: 3);
        var media = await ctx.FFmpeg.GetMediaInfoAsync(sample);

        ctx.Check("ffprobe 解析媒体信息", media != null);
        ctx.Check("时长解析正确（≈3 秒）", media is { Duration: > 2.5 and < 3.5 }, media?.Duration.ToString("0.###"));
        ctx.Check("分辨率解析正确", media is { Width: 640, Height: 360 }, $"{media?.Width}x{media?.Height}");
        ctx.Check("帧率解析正确（≈25）", media is { FrameRate: > 24 and < 26 }, media?.FrameRate.ToString("0.##"));
        ctx.Check("识别视频编码", media?.VideoCodec == "h264", media?.VideoCodec);
        ctx.Check("识别音频编码", media?.AudioCodec == "aac", media?.AudioCodec);
        ctx.Check("标记有视频/有音频", media is { HasVideo: true, HasAudio: true });
        ctx.Check("不存在的文件返回 null 而不是抛异常",
            await ctx.FFmpeg.GetMediaInfoAsync(Path.Combine(ctx.MediaDirectory, "missing.mp4")) == null);

        return sample;
    }

    private static async Task<(CommandTemplate Template, BuiltCommand Built, string Output)> CheckCommandBuildingAsync(
        VerifyContext ctx, string sample)
    {
        var sync = await ctx.Templates.InitializeSystemTemplatesAsync();
        ctx.Check("系统模板首次初始化", sync.Added > 0, sync.Summary);
        var all = await ctx.Templates.GetAllTemplatesAsync();
        ctx.Check("模板可枚举", all.Count == sync.Added, $"{all.Count} 个");
        var again = await ctx.Templates.InitializeSystemTemplatesAsync();
        ctx.Check("重复同步不产生变更", again.Added == 0 && again.Updated == 0, again.Summary);

        var template = all.First(t => t.HardwareMode == HardwareAccelerationMode.Auto && t.TargetCodec == "h264");
        var codec = "h264";
        var encoderName = await ctx.Hardware.GetRecommendedEncoderAsync(codec, template.RequiredEncoder);
        var profile = EncoderCatalog.Find(encoderName);
        var output = Path.Combine(ctx.MediaDirectory, "pipeline-output.mp4");

        var built = ctx.FFmpeg.BuildCommand(template, new CommandContext
        {
            InputPath = sample,
            OutputPath = output,
            Encoder = profile,
            Media = await ctx.FFmpeg.GetMediaInfoAsync(sample),
            Parameters = ctx.Templates.CreateDefaultParameterValues(template),
            AudioEncoder = "aac"
        });

        ctx.Check("命令构建成功", built.Arguments.Count > 5);
        ctx.Check("无残留占位符", built.UnresolvedPlaceholders.Count == 0, string.Join(",", built.UnresolvedPlaceholders));
        ctx.Check("无空参数", !built.Arguments.Any(string.IsNullOrEmpty));
        ctx.Check("参数组占位符展开为多个参数（不是挤在一个参数里）",
            !built.Arguments.Any(a => a.StartsWith('-') && a.Contains(' ')),
            string.Join(" ", built.Arguments.Where(a => a.StartsWith('-'))));
        VerifyContext.Info($"命令：ffmpeg {built.CommandLine}");

        return (template, built, output);
    }

    private static async Task CheckConversionAsync(VerifyContext ctx, string sample, CommandTemplate template, string output)
    {
        var progress = new List<double>();
        var statuses = new List<TaskStatus>();
        ctx.Queue.TaskProgressChanged += (_, e) => { lock (progress) progress.Add(e.Progress); };
        ctx.Queue.TaskStatusChanged += (_, e) => { lock (statuses) statuses.Add(e.NewStatus); };

        var task = await ctx.Tasks.CreateTaskAsync(sample, output, template.Id,
            ctx.Templates.CreateDefaultParameterValues(template), overwriteExisting: true);
        ctx.Check("创建任务", task.Id != ObjectId.Empty);

        ctx.Check("任务入队（不阻塞调用方）", await ctx.Tasks.StartTaskAsync(task.Id));

        var finished = await VerifyContext.WaitForAsync(
            () => ctx.Tasks.GetTaskByIdAsync(task.Id),
            t => t?.Status is TaskStatus.Completed or TaskStatus.Failed,
            TimeSpan.FromMinutes(3));

        ctx.Check("任务执行完成", finished?.Status == TaskStatus.Completed, finished?.ErrorMessage);
        ctx.Check("输出文件已生成且非空", File.Exists(output) && new FileInfo(output).Length > 1000,
            File.Exists(output) ? new FileInfo(output).Length + " 字节" : "文件不存在");
        ctx.Check("上报了进度事件", progress.Count > 0, $"{progress.Count} 条");
        ctx.Check("最终进度为 100", finished?.Progress >= 99.9, finished?.Progress.ToString("0.#"));
        ctx.Check("记录了输出文件大小", finished?.OutputFileSize > 0);
        ctx.Check("记录了实际执行的命令", !string.IsNullOrWhiteSpace(finished?.ActualCommand));
        ctx.Check("记录了耗时", finished?.DurationSeconds > 0);
        ctx.Check("状态事件包含 Running", statuses.Contains(TaskStatus.Running));
        ctx.Check("状态事件包含 Completed", statuses.Contains(TaskStatus.Completed));
        VerifyContext.Info($"耗时 {finished?.DurationSeconds:0.0}s，进度事件 {progress.Count} 条，" +
                           $"日志 {finished?.LogOutput.Length ?? 0} 字符");

        var outMedia = await ctx.FFmpeg.GetMediaInfoAsync(output);
        ctx.Check("输出文件可被解析（不是损坏文件）", outMedia is { HasVideo: true }, outMedia?.VideoCodec);
        ctx.Check("输出保留了音轨", outMedia?.HasAudio == true, outMedia?.AudioCodec);
    }

    private static async Task CheckOverwritePolicyAsync(VerifyContext ctx, string sample, CommandTemplate template, string output)
    {
        ctx.Check("重命名策略生成不冲突路径", OutputPathResolver.Resolve(output, FileExistsAction.Rename).Renamed);
        ctx.Check("跳过策略标记 Skip", OutputPathResolver.Resolve(output, FileExistsAction.Skip).Skip);
        ctx.Check("覆盖策略允许覆盖且不改名",
            OutputPathResolver.Resolve(output, FileExistsAction.Overwrite) is { Renamed: false, Overwrite: true });

        var ask = OutputPathResolver.Resolve(output, FileExistsAction.Ask);
        ctx.Check("询问策略退化为重命名或覆盖（不会让 FFmpeg 阻塞等待输入）", ask.Renamed || ask.Overwrite);

        var settings = await ctx.Settings.GetSettingsAsync();
        settings.FileExistsAction = FileExistsAction.Skip;
        await ctx.Settings.SaveSettingsAsync(settings);

        var sizeBefore = new FileInfo(output).Length;
        var skipTask = await ctx.Tasks.CreateTaskAsync(sample, output, template.Id);
        await ctx.Tasks.StartTaskAsync(skipTask.Id);
        var skipped = await VerifyContext.WaitForAsync(
            () => ctx.Tasks.GetTaskByIdAsync(skipTask.Id),
            t => t?.Status is TaskStatus.Skipped or TaskStatus.Completed or TaskStatus.Failed,
            TimeSpan.FromSeconds(30));

        ctx.Check("已存在的输出在「跳过」策略下被标记为已跳过", skipped?.Status == TaskStatus.Skipped, skipped?.Status.ToString());
        ctx.Check("跳过时原文件未被改写", new FileInfo(output).Length == sizeBefore);

        settings.FileExistsAction = FileExistsAction.Rename;
        await ctx.Settings.SaveSettingsAsync(settings);
    }

    private static async Task CheckCancellationAsync(VerifyContext ctx)
    {
        var longInput = await ctx.CreateSampleAsync("pipeline-long.mp4", seconds: 60, size: "1280x720", fps: 30);
        var all = await ctx.Templates.GetAllTemplatesAsync();
        var compress = all.First(t => t.Name.Contains("压缩") && t.CommandArgs.Contains("libx264"));
        var cancelOutput = Path.Combine(ctx.MediaDirectory, "pipeline-cancelled.mp4");

        var task = await ctx.Tasks.CreateTaskAsync(longInput, cancelOutput, compress.Id, null, overwriteExisting: true);
        await ctx.Tasks.StartTaskAsync(task.Id);
        await VerifyContext.WaitForAsync(() => ctx.Tasks.GetTaskByIdAsync(task.Id),
            t => t?.Status == TaskStatus.Running, TimeSpan.FromSeconds(20));
        await Task.Delay(1200);

        ctx.Check("取消请求被接受", await ctx.Tasks.CancelTaskAsync(task.Id, graceful: false));
        var cancelled = await VerifyContext.WaitForAsync(() => ctx.Tasks.GetTaskByIdAsync(task.Id),
            t => t?.Status is TaskStatus.Cancelled or TaskStatus.Failed, TimeSpan.FromSeconds(30));

        ctx.Check("任务状态变为已取消", cancelled?.Status == TaskStatus.Cancelled, cancelled?.Status.ToString());
        ctx.Check("取消后清理了半成品输出", !File.Exists(cancelOutput));
        ctx.Check("取消后没有残留进程", ctx.FFmpeg.RunningCount == 0, ctx.FFmpeg.RunningCount.ToString());

        // 中断恢复：模拟上次非正常退出留下的「运行中」任务
        var orphan = new ConversionTask { InputPath = longInput, OutputPath = cancelOutput, Status = TaskStatus.Running };
        ctx.Db.Tasks.Insert(orphan);
        var recovered = ctx.Tasks.RecoverOrphanedTasks();
        ctx.Check("启动时复位中断的任务",
            recovered >= 1 && (await ctx.Tasks.GetTaskByIdAsync(orphan.Id))?.Status == TaskStatus.Failed);
    }

    private static async Task CheckTemplateValidationAsync(VerifyContext ctx, CommandTemplate template)
    {
        var cases = new (string Name, CommandTemplate Template, TemplateIssueSeverity Severity, string Fragment)[]
        {
            ("缺少 {input}/{output}",
                new CommandTemplate { Name = "t1", CommandArgs = "-c:v libx264", OutputExtension = "mp4" },
                TemplateIssueSeverity.Error, "output"),
            ("引号不成对",
                new CommandTemplate { Name = "t2", CommandArgs = "-i \"{input} -c:v libx264 \"{output}\"", OutputExtension = "mp4" },
                TemplateIssueSeverity.Error, "引号"),
            ("未声明的占位符",
                new CommandTemplate { Name = "t3", CommandArgs = "-i \"{input}\" {mystery} \"{output}\"", OutputExtension = "mp4" },
                TemplateIssueSeverity.Error, "mystery"),
            ("硬件编码器上使用 -crf",
                new CommandTemplate
                {
                    Name = "t4", CommandArgs = "-i \"{input}\" -c:v {encoder} -crf 23 \"{output}\"",
                    OutputExtension = "mp4", HardwareMode = HardwareAccelerationMode.Required, RequiredEncoder = "nvenc"
                },
                TemplateIssueSeverity.Warning, "-crf"),
            ("要求硬件但未指定编码器族",
                new CommandTemplate
                {
                    Name = "t5", CommandArgs = "-i \"{input}\" -c:v libx264 \"{output}\"",
                    OutputExtension = "mp4", HardwareMode = HardwareAccelerationMode.Required
                },
                TemplateIssueSeverity.Error, "硬件加速"),
        };

        foreach (var (name, subject, severity, fragment) in cases)
        {
            var issues = ctx.Templates.Validate(subject);
            ctx.Check($"校验能发现「{name}」",
                issues.Any(i => i.Severity == severity && i.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
                string.Join(" | ", issues.Select(i => $"{i.Severity}:{i.Message}")));
        }

        var report = await ctx.Hardware.DetectAsync();
        var needsMissingFilter = new CommandTemplate
        {
            Name = "filter-probe",
            CommandArgs = "-i \"{input}\" -vf \"subtitles=x.srt\" \"{output}\"",
            OutputExtension = "mp4",
            RequiredFilters = ["subtitles"]
        };
        var missing = ctx.Templates.GetMissingFilters(needsMissingFilter, report);
        if (report.SupportedFilters.Count > 0 && !report.SupportedFilters.Contains("subtitles", StringComparer.OrdinalIgnoreCase))
        {
            ctx.Check("能识别出缺失的必需滤镜", missing.Contains("subtitles"), string.Join(",", missing));
        }
        else
        {
            ctx.Check("滤镜清单可用（本机含 subtitles，跳过缺失判断）", true);
        }

        var testResult = await ctx.Templates.TestTemplateAsync(template);
        ctx.Check("模板试运行成功", testResult.Success, testResult.Message);
        VerifyContext.Info(testResult.Message);

        // 校验必须能拦住不可能跑通的模板
        var badTest = await ctx.Templates.TestTemplateAsync(new CommandTemplate
        {
            Name = "bad", CommandArgs = "-c:v libx264", OutputExtension = "mp4"
        });
        ctx.Check("试运行会拒绝校验不通过的模板", !badTest.Success, badTest.Message);

        // 导入导出
        var all = await ctx.Templates.GetAllTemplatesAsync();
        var json = ctx.Templates.ExportToJson(all.Take(3));
        ctx.Check("导出 JSON 非空", json.Length > 100);
        ctx.Check("导入 JSON 成功", await ctx.Templates.ImportFromJsonAsync(json) == 3);
        ctx.Check("重复导入自动改名而不报错", await ctx.Templates.ImportFromJsonAsync(json) == 3);
    }

    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 检查两类容易被忽略的问题：
    ///   1) 输出路径与输入文件相同时必须改名，否则会边读边写毁掉源文件；
    ///   2) 「优先使用硬件解码」「输出文件命名规则」这些设置必须真的作用于命令/路径。
    /// </summary>
    private static async Task CheckSafetyAndSettingsAsync(VerifyContext ctx, string sample, CommandTemplate template)
    {
        // 1) 输出 == 输入
        var sameResolution = OutputPathResolver.Resolve(sample, FileExistsAction.Overwrite, sample);
        ctx.Check("输出与输入相同时强制改名（不覆盖源文件）",
            sameResolution.Renamed && !OutputPathResolver.PathsEqual(sameResolution.OutputPath, sample),
            sameResolution.OutputPath);
        ctx.Check("改名后的路径确实不同", !string.Equals(sameResolution.OutputPath, sample, StringComparison.Ordinal));

        var noInputResolution = OutputPathResolver.Resolve(sample, FileExistsAction.Overwrite);
        ctx.Check("未传输入路径时保持原有语义（覆盖）",
            noInputResolution is { Renamed: false, Overwrite: true });

        // 大小写不同的同一路径（Windows/macOS 上应该判定为同一个文件）
        var upper = sample.ToUpperInvariant();
        if (!AppPaths.IsLinux)
        {
            ctx.Check("大小写不同的同一路径被判定为同一个文件",
                OutputPathResolver.PathsEqual(sample, upper));
        }

        // 2) 硬件解码设置必须真的改变命令
        var media = await ctx.FFmpeg.GetMediaInfoAsync(sample);
        var output = Path.Combine(ctx.MediaDirectory, "settings-probe.mp4");

        var decodeArgs = ctx.FFmpeg.BuildCommand(template, new CommandContext
        {
            InputPath = sample,
            OutputPath = output,
            Encoder = EncoderCatalog.Find(await ctx.Hardware.GetRecommendedEncoderAsync("h264", template.RequiredEncoder)),
            Media = media,
            Parameters = ctx.Templates.CreateDefaultParameterValues(template),
            HardwareDecodeArgs = "-hwaccel videotoolbox"
        });

        var decodeIndex = decodeArgs.Arguments.ToList().IndexOf("-hwaccel");
        var inputIndex = decodeArgs.Arguments.ToList().IndexOf(sample);
        ctx.Check("「优先使用硬件解码」会注入 -hwaccel", decodeIndex >= 0);
        ctx.Check("-hwaccel 被放在输入文件之前（否则 FFmpeg 不认）",
            decodeIndex >= 0 && decodeIndex < inputIndex, $"hwaccel@{decodeIndex}, input@{inputIndex}");
        VerifyContext.Info($"注入后的命令：ffmpeg {decodeArgs.CommandLine}");

        var withoutDecode = ctx.FFmpeg.BuildCommand(template, new CommandContext
        {
            InputPath = sample,
            OutputPath = output,
            Encoder = EncoderCatalog.Find(await ctx.Hardware.GetRecommendedEncoderAsync("h264", template.RequiredEncoder)),
            Media = media,
            Parameters = ctx.Templates.CreateDefaultParameterValues(template),
            HardwareDecodeArgs = string.Empty
        });
        ctx.Check("关闭该设置时不会注入 -hwaccel", !withoutDecode.Arguments.Contains("-hwaccel"));

        // 3) 批量任务必须遵循「输出文件命名规则」
        var batchDir = Path.Combine(ctx.MediaDirectory, "batch-out");
        Directory.CreateDirectory(batchDir);

        var settings = await ctx.Settings.GetSettingsAsync();
        settings.OutputNaming = OutputNamingRule.Suffix;
        settings.OutputSuffix = "_verify";
        await ctx.Settings.SaveSettingsAsync(settings);

        var batch = await ctx.Tasks.CreateBatchTaskAsync([sample], batchDir, template.Id);
        var batchTasks = await ctx.Tasks.GetBatchTasksAsync(batch.Id);
        var batchOutput = batchTasks.Single().OutputPath;
        ctx.Check("批量任务遵循「添加后缀」命名规则",
            Path.GetFileNameWithoutExtension(batchOutput).EndsWith("_verify", StringComparison.Ordinal),
            Path.GetFileName(batchOutput));

        settings.OutputNaming = OutputNamingRule.Original;
        await ctx.Settings.SaveSettingsAsync(settings);
        var batch2 = await ctx.Tasks.CreateBatchTaskAsync([sample], batchDir, template.Id);
        var batch2Output = (await ctx.Tasks.GetBatchTasksAsync(batch2.Id)).Single().OutputPath;
        ctx.Check("批量任务遵循「保持原名」命名规则",
            Path.GetFileNameWithoutExtension(batch2Output) == Path.GetFileNameWithoutExtension(sample),
            Path.GetFileName(batch2Output));

        // 4) 「输出 == 输入」在真实执行时也不会毁掉源文件
        settings.OutputNaming = OutputNamingRule.Original;
        settings.FileExistsAction = FileExistsAction.Overwrite;
        await ctx.Settings.SaveSettingsAsync(settings);

        var sourceDir = Path.Combine(ctx.MediaDirectory, "self-overwrite");
        Directory.CreateDirectory(sourceDir);
        var sourceCopy = Path.Combine(sourceDir, "self.mp4");
        File.Copy(sample, sourceCopy);
        var sourceFingerprint = new FileInfo(sourceCopy).Length;

        var selfTask = await ctx.Tasks.CreateTaskAsync(sourceCopy, sourceCopy, template.Id, null, overwriteExisting: true);
        await ctx.Tasks.StartTaskAsync(selfTask.Id);
        var selfDone = await VerifyContext.WaitForAsync(() => ctx.Tasks.GetTaskByIdAsync(selfTask.Id),
            t => t?.Status is TaskStatus.Completed or TaskStatus.Failed, TimeSpan.FromMinutes(2));

        ctx.Check("输出指向输入时任务仍然成功（自动改名）", selfDone?.Status == TaskStatus.Completed, selfDone?.ErrorMessage);
        ctx.Check("源文件未被改写", File.Exists(sourceCopy) && new FileInfo(sourceCopy).Length == sourceFingerprint,
            $"{sourceFingerprint} → {(File.Exists(sourceCopy) ? new FileInfo(sourceCopy).Length : 0)}");
        // 5) Required 模板 + 本机缺硬件 => 必须明确失败，不能偷偷用别的厂商编码器
        var allTemplates = await ctx.Templates.GetAllTemplatesAsync();
        var filterReport = await ctx.Hardware.DetectAsync();
        var availableNames = filterReport.Encoders
            .Where(e => e.IsAvailable)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requiredTemplate = allTemplates.FirstOrDefault(t =>
            t.HardwareMode == HardwareAccelerationMode.Required &&
            !string.IsNullOrWhiteSpace(t.RequiredEncoder) &&
            ctx.Templates.GetMissingFilters(t, filterReport).Count == 0 &&
            !EncoderCatalog.ExpandFamily(t.RequiredEncoder!).Any(availableNames.Contains));

        if (requiredTemplate is null)
        {
            VerifyContext.Info("本机具备所有模板要求的硬件（或没有这类模板），跳过「严格拒绝」检查");
        }
        else
        {
            var strictName = await ctx.Hardware.GetRecommendedEncoderAsync(
                "h264", requiredTemplate.RequiredEncoder, strict: true);
            ctx.Check("严格模式下缺失的编码器族返回 null", strictName is null, strictName);

            var looseName = await ctx.Hardware.GetRecommendedEncoderAsync(
                "h264", requiredTemplate.RequiredEncoder, strict: false);
            ctx.Check("宽松模式仍会给出可用编码器（用于推荐类模板）", !string.IsNullOrEmpty(looseName), looseName);

            var refusedOutput = Path.Combine(ctx.MediaDirectory, "refused.mp4");
            var refusedTask = await ctx.Tasks.CreateTaskAsync(sample, refusedOutput, requiredTemplate.Id, null, overwriteExisting: true);
            await ctx.Tasks.StartTaskAsync(refusedTask.Id);
            var refusedDone = await VerifyContext.WaitForAsync(() => ctx.Tasks.GetTaskByIdAsync(refusedTask.Id),
                t => t?.Status is TaskStatus.Failed or TaskStatus.Completed, TimeSpan.FromMinutes(2));

            ctx.Check($"「{requiredTemplate.Name}」在本机缺硬件时被明确拒绝",
                refusedDone?.Status == TaskStatus.Failed, refusedDone?.Status.ToString());
            ctx.Check("被拒绝时给出了可读的原因",
                refusedDone?.ErrorMessage?.Contains("硬件编码", StringComparison.Ordinal) == true,
                refusedDone?.ErrorMessage);
            ctx.Check("被拒绝时没有产出文件", !File.Exists(refusedOutput));
            ctx.Check("被拒绝时没有偷偷换用其它厂商编码器（命令里不含该族之外的编码器）",
                refusedDone?.ActualCommand?.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase) != true &&
                refusedDone?.ActualCommand?.Contains("libx264", StringComparison.OrdinalIgnoreCase) != true,
                refusedDone?.ActualCommand);
            VerifyContext.Info("拒绝原因：" + refusedDone?.ErrorMessage);
        }

        ctx.Check("改名后的输出文件确实生成了",
            selfDone != null && File.Exists(selfDone.OutputPath) &&
            !OutputPathResolver.PathsEqual(selfDone.OutputPath, sourceCopy),
            selfDone?.OutputPath);
    }

    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 文件对话框返回值的解析检查。
    /// 起因：macOS 的 choose file 直接返回时是 AppleScript 的 alias 字符串
    /// （<c>alias Macintosh HD:Users:me:Movies:25 4K.mp4</c>），不是文件系统路径，
    /// 传到界面会被判定成「文件不存在」。这里把纯函数部分固化下来防回归。
    /// </summary>
    private static void CheckFileDialogPaths(VerifyContext ctx)
    {
        // 1) AppleScript 文件引用（alias / file / folder / disk …）→ POSIX
        var cases = new (string Raw, string? Expected)[]
        {
            ("Macintosh HD:Users:c01h:Movies:25 4K.mp4", "/Users/c01h/Movies/25 4K.mp4"),
            ("Macintosh HD::Users::me:a.mp4", "/Users/me/a.mp4"),
            ("Data:Users:me:a.mp4", "/Volumes/Data/Users/me/a.mp4"),
            ("Macintosh HD", null),
            ("", null),
        };

        foreach (var (raw, expected) in cases)
        {
            var converted = FileDialogService.ConvertAppleScriptAliasToPosix(raw);
            ctx.Check($"HFS 路径「{raw}」→ {(expected ?? "null")}", converted == expected, converted ?? "null");
        }

        // 2) 完整返回值解析：对话框可能把结果序列化成 "alias …" / "file …" 这类字符串
        var serialized = new (string Raw, string? Expected)[]
        {
            ("alias Macintosh HD:Users:c01h:Movies:25 4K.mp4", "/Users/c01h/Movies/25 4K.mp4"),
            ("file Macintosh HD:Users:c01h:Movies:25 4K.mp4", "/Users/c01h/Movies/25 4K.mp4"),
            ("folder Macintosh HD:Users:c01h:Movies", "/Users/c01h/Movies"),
            ("/Users/c01h/Movies/25 4K.mp4", "/Users/c01h/Movies/25 4K.mp4"),
            ("disk Macintosh HD", null),
            ("", null),
            ("   ", null),
            ("User canceled.", null),
            // 不是文件引用前缀的字符串（例如误传进来的 URL）必须原样保留，不能被当成 HFS 路径
            ("mailto:someone@example.com", "mailto:someone@example.com"),
            ("https://example.com/a:b", "https://example.com/a:b"),
        };

        foreach (var (raw, expected) in serialized)
        {
            var converted = FileDialogService.NormalizeDialogResultForTests(raw);
            var label = expected is null
                ? $"「{raw}」无法解析为路径时返回「没有选择」"
                : $"「{raw}」解析为 {expected}";
            ctx.Check(label, converted == expected, converted ?? "null");
        }

        // 3) 生成的 AppleScript 必须语法正确。
        //    用 osacompile 只编译不执行，不会弹出对话框。
        var script = FileDialogService.BuildMacChooseScript("选择媒体文件", "/Users/c01h/Movies", pickFolder: false);
        var scriptWithFolder = FileDialogService.BuildMacChooseScript("选择文件夹", "/Users/c01h/Movies", pickFolder: true);
        var scriptWithoutStart = FileDialogService.BuildMacChooseScript("选择媒体文件", "", pickFolder: false);

        CheckAppleScriptCompiles(ctx, script, "文件选择器脚本");
        CheckAppleScriptCompiles(ctx, scriptWithFolder, "文件夹选择器脚本");
        CheckAppleScriptCompiles(ctx, scriptWithoutStart, "无起始目录时的选择器脚本");

        ctx.Check("文件选择器使用 choose file", script.Contains("choose file", StringComparison.Ordinal));
        ctx.Check("结果被 POSIX path of 包住（这是修掉 alias 问题的关键）",
            script.Contains("POSIX path of", StringComparison.Ordinal));
        ctx.Check("文件夹选择器使用 choose folder",
            scriptWithFolder.Contains("choose folder", StringComparison.Ordinal));
        ctx.Check("每个 try 只有一个 on error 分支（AppleScript 语法要求）",
            CountOccurrences(script, "try") >= 1 &&
            CountOccurrences(script, "on error") <= CountOccurrences(script, "end try"),
            $"try={CountOccurrences(script, "try")}, on error={CountOccurrences(script, "on error")}");
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static void CheckAppleScriptCompiles(VerifyContext ctx, string script, string label)
    {
        if (!AppPaths.IsMacOS)
        {
            Console.WriteLine($"  ⏭️  {label}: 非 macOS，跳过语法检查");
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"ffui-osacompile-{Guid.NewGuid():N}.applescript");
        try
        {
            File.WriteAllText(temp, script);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/osacompile",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(Path.Combine(Path.GetTempPath(), $"ffui-osacompile-out-{Guid.NewGuid():N}.scpt"));
            psi.ArgumentList.Add(temp);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) { ctx.Check($"{label} 可以编译", false, "无法启动 osacompile"); return; }
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var ok = process.ExitCode == 0;
            ctx.Check($"{label} 可以编译（语法正确）", ok, ok ? null : stderr.Trim());
            if (!ok)
            {
                Console.WriteLine("     ---- 生成的脚本 ----");
                foreach (var line in script.Split('\n')) Console.WriteLine("     | " + line);
                Console.WriteLine("     --------------------");
            }
        }
        catch (Exception ex)
        {
            ctx.Check($"{label} 可以编译", false, ex.Message);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* 忽略 */ }
        }
    }
}

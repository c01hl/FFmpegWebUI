using System.Text.Json;
using System.Text.Json.Serialization;
using FFmpegWebUI.Data;
using FFmpegWebUI.Models;
using LiteDB;

namespace FFmpegWebUI.Services;

/// <summary>模板管理服务实现</summary>
public sealed class TemplateService : ITemplateService
{
    private readonly IFFmpegService _ffmpegService;
    private readonly IFileService _fileService;
    private readonly IHardwareDetectionService _hardwareDetection;
    private readonly ISettingsService _settingsService;

    public TemplateService(
        ILiteDbContext db,
        IFFmpegService ffmpegService,
        IFileService fileService,
        IHardwareDetectionService hardwareDetection,
        ISettingsService settingsService)
    {
        Db = db;
        _ffmpegService = ffmpegService;
        _fileService = fileService;
        _hardwareDetection = hardwareDetection;
        _settingsService = settingsService;
    }

    private ILiteDbContext Db { get; }

    public int PresetVersion => SystemTemplatePresets.Version;

    // ────────────────────────────────────────────────────────────────
    // 查询
    // ────────────────────────────────────────────────────────────────

    public Task<List<CommandTemplate>> GetAllTemplatesAsync(bool includeSystem = true)
    {
        var query = Db.Templates.Query();
        if (!includeSystem)
        {
            query = query.Where(t => t.Type == TemplateType.User);
        }

        var templates = query.ToList()
            .OrderByDescending(t => t.IsRecommended)
            .ThenBy(t => CategoryRank(t.Category))
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.Name, StringComparer.CurrentCulture)
            .ToList();

        return Task.FromResult(templates);
    }

    public Task<List<CommandTemplate>> GetTemplatesByCategoryAsync(string category)
    {
        var templates = Db.Templates
            .Query()
            .Where(t => t.Category == category)
            .ToList()
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name, StringComparer.CurrentCulture)
            .ToList();

        return Task.FromResult(templates);
    }

    public Task<CommandTemplate?> GetTemplateByIdAsync(ObjectId id) =>
        // LiteDB 的 FindById 未标注可空性，这里显式声明返回类型
        Task.FromResult<CommandTemplate?>(Db.Templates.FindById(id));

    public Task<List<string>> GetCategoriesAsync()
    {
        var categories = Db.Templates.Query().ToList()
            .Select(t => t.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.CurrentCulture)
            .ToList();

        var ordered = categories
            .OrderBy(CategoryRank)
            .ThenBy(c => c, StringComparer.CurrentCulture)
            .ToList();

        return Task.FromResult(ordered);
    }

    /// <summary>按预设顺序给分类排名，未知分类排到最后</summary>
    private static int CategoryRank(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return int.MaxValue - 1;
        var index = Array.IndexOf(SystemTemplatePresets.CategoryOrder, category);
        return index < 0 ? int.MaxValue - 2 : index;
    }

    // ────────────────────────────────────────────────────────────────
    // 增删改
    // ────────────────────────────────────────────────────────────────

    public Task<CommandTemplate> CreateTemplateAsync(CommandTemplate template)
    {
        template.Id = ObjectId.NewObjectId();
        template.Type = TemplateType.User;
        template.CreatedAt = DateTime.UtcNow;
        template.UpdatedAt = DateTime.UtcNow;
        template.Name = EnsureUniqueName(template.Name, template.Id);
        NormalizeTemplate(template);
        Db.Templates.Upsert(template);
        return Task.FromResult(template);
    }

    public Task<bool> UpdateTemplateAsync(CommandTemplate template)
    {
        var existing = Db.Templates.FindById(template.Id);
        if (existing == null) return Task.FromResult(false);
        if (existing.Type == TemplateType.System) return Task.FromResult(false);

        template.Type = TemplateType.User;
        template.CreatedAt = existing.CreatedAt;
        template.UpdatedAt = DateTime.UtcNow;
        template.Name = EnsureUniqueName(template.Name, template.Id);
        NormalizeTemplate(template);

        return Task.FromResult(Db.Templates.Update(template));
    }

    public Task<bool> DeleteTemplateAsync(ObjectId id)
    {
        var template = Db.Templates.FindById(id);
        if (template == null || template.Type == TemplateType.System)
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(Db.Templates.Delete(id));
    }

    public Task<CommandTemplate> CopyTemplateAsync(ObjectId sourceId, string? newName = null)
    {
        var source = Db.Templates.FindById(sourceId)
            ?? throw new ArgumentException("源模板不存在", nameof(sourceId));

        var copy = new CommandTemplate
        {
            Id = ObjectId.NewObjectId(),
            Name = EnsureUniqueName(newName ?? $"{source.Name} (副本)", null),
            Description = source.Description,
            CommandArgs = source.CommandArgs,
            Type = TemplateType.User,
            Category = source.Category,
            SupportedInputFormats = [.. source.SupportedInputFormats],
            OutputExtension = source.OutputExtension,
            TargetCodec = source.TargetCodec,
            RequiresHardwareAcceleration = source.RequiresHardwareAcceleration,
            HardwareMode = source.HardwareMode,
            RequiredEncoder = source.RequiredEncoder,
            Parameters = CloneParameters(source.Parameters),
            Tags = [.. source.Tags],
            IsRecommended = false,
            PresetVersion = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SortOrder = source.SortOrder + 1
        };

        Db.Templates.Insert(copy);
        return Task.FromResult(copy);
    }

    public Task<CommandTemplate> CreateSystemTemplateAsync(CommandTemplate template)
    {
        template.Id = ObjectId.NewObjectId();
        template.Type = TemplateType.System;
        template.CreatedAt = DateTime.UtcNow;
        template.UpdatedAt = DateTime.UtcNow;
        template.PresetVersion = PresetVersion;
        template.Name = EnsureUniqueName(template.Name, template.Id);
        NormalizeTemplate(template);
        Db.Templates.Upsert(template);
        return Task.FromResult(template);
    }

    public Task<bool> UpdateSystemTemplateAsync(CommandTemplate template)
    {
        var existing = Db.Templates.FindById(template.Id);
        if (existing == null) return Task.FromResult(false);

        template.Type = existing.Type;
        template.CreatedAt = existing.CreatedAt;
        template.UpdatedAt = DateTime.UtcNow;
        if (template.Type == TemplateType.System) template.PresetVersion = PresetVersion;
        template.Name = EnsureUniqueName(template.Name, template.Id);
        NormalizeTemplate(template);

        return Task.FromResult(Db.Templates.Update(template));
    }

    public Task<bool> DeleteSystemTemplateAsync(ObjectId id)
    {
        var template = Db.Templates.FindById(id);
        if (template == null) return Task.FromResult(false);
        return Task.FromResult(Db.Templates.Delete(id));
    }

    // ────────────────────────────────────────────────────────────────
    // 系统模板同步
    // ────────────────────────────────────────────────────────────────

    public Task<SystemTemplateSyncResult> InitializeSystemTemplatesAsync()
    {
        var presets = SystemTemplatePresets.CreateAll();
        var existing = Db.Templates.Query().Where(t => t.Type == TemplateType.System).ToList();
        var byName = existing
            .GroupBy(t => t.Name, StringComparer.CurrentCulture)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.CurrentCulture);

        var added = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var preset in presets)
        {
            if (byName.TryGetValue(preset.Name, out var current))
            {
                if (current.PresetVersion >= preset.PresetVersion)
                {
                    unchanged++;
                    continue;
                }

                // 保留原来的 Id，这样已有任务/批次的引用不会失效
                preset.Id = current.Id;
                preset.CreatedAt = current.CreatedAt;
                preset.UpdatedAt = DateTime.UtcNow;
                Db.Templates.Update(preset);
                updated++;
            }
            else
            {
                Db.Templates.Insert(preset);
                added++;
            }
        }

        return Task.FromResult(new SystemTemplateSyncResult(added, updated, unchanged, PresetVersion));
    }

    public async Task ResetSystemTemplatesAsync()
    {
        var systemIds = Db.Templates.Query()
            .Where(t => t.Type == TemplateType.System)
            .Select(t => t.Id)
            .ToList();

        foreach (var id in systemIds)
        {
            Db.Templates.Delete(id);
        }

        var presets = SystemTemplatePresets.CreateAll();
        Db.Templates.InsertBulk(presets);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ────────────────────────────────────────────────────────────────
    // 参数
    // ────────────────────────────────────────────────────────────────

    public Dictionary<string, string> CreateDefaultParameterValues(CommandTemplate template)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in template.Parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameter.Name))
            {
                values[parameter.Name] = parameter.DefaultValue ?? string.Empty;
            }
        }
        return values;
    }

    // ────────────────────────────────────────────────────────────────
    // 校验
    // ────────────────────────────────────────────────────────────────

    public IReadOnlyList<TemplateIssue> Validate(CommandTemplate template)
    {
        var issues = new List<TemplateIssue>();

        if (string.IsNullOrWhiteSpace(template.Name))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error, "模板名称不能为空", nameof(CommandTemplate.Name)));
        }
        if (string.IsNullOrWhiteSpace(template.CommandArgs))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error, "命令参数不能为空", nameof(CommandTemplate.CommandArgs)));
            return issues;
        }
        if (string.IsNullOrWhiteSpace(template.OutputExtension))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error, "必须指定输出扩展名，否则无法生成输出文件名", nameof(CommandTemplate.OutputExtension)));
        }

        var placeholders = CommandLineBuilder.ExtractPlaceholders(template.CommandArgs);
        var declared = template.Parameters
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!placeholders.Any(p => p.Equals("input", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error,
                "命令中缺少 {input} 占位符，FFmpeg 将无法读取输入文件", nameof(CommandTemplate.CommandArgs)));
        }
        var hasOutput = placeholders.Any(p => p.Equals("output", StringComparison.OrdinalIgnoreCase));
        var hasOutputPattern = placeholders.Any(p => p.Equals("output_pattern", StringComparison.OrdinalIgnoreCase));
        if (!hasOutput && !hasOutputPattern)
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error,
                "命令中缺少 {output}（或用于图片序列的 {output_pattern}）占位符，转换结果没有输出目标",
                nameof(CommandTemplate.CommandArgs)));
        }

        foreach (var placeholder in placeholders)
        {
            if (CommandLineBuilder.BuiltInPlaceholders.Contains(placeholder)) continue;
            if (declared.Contains(placeholder)) continue;
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error,
                $"占位符 {{{placeholder}}} 没有对应取值：请在上方『参数』中声明它，或改用内置占位符",
                nameof(CommandTemplate.Parameters)));
        }

        // 引号必须成对，否则参数会被错误拆分
        var quoteCount = template.CommandArgs.Count(c => c == '"');
        if (quoteCount % 2 != 0)
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error,
                "双引号数量为奇数，命令可能被错误拆分。含空格的路径请写成 \"{input}\"", nameof(CommandTemplate.CommandArgs)));
        }

        // 硬件编码器不应使用 -crf / -preset 这类软件编码器专属选项（FFmpeg 只会警告并忽略）
        var isHardwareTemplate = template.HardwareMode == HardwareAccelerationMode.Required ||
                                 (!string.IsNullOrWhiteSpace(template.RequiredEncoder) &&
                                  !EncoderCatalog.ByFamily("software").Any(p =>
                                      string.Equals(p.Name, template.RequiredEncoder, StringComparison.OrdinalIgnoreCase)));
        if (isHardwareTemplate && template.RequiredEncoder is { Length: > 0 } required)
        {
            var profiles = EncoderCatalog.ExpandFamily(required)
                .Select(EncoderCatalog.Find)
                .Where(p => p is { IsHardware: true })
                .ToList();

            if (profiles.Count > 0)
            {
                var usesSoftwareOnlyOptions = template.CommandArgs.Contains("-crf", StringComparison.OrdinalIgnoreCase);
                if (usesSoftwareOnlyOptions)
                {
                    issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning,
                        "硬件编码器不支持 -crf，该选项会被 FFmpeg 忽略。建议改用 {quality_args} 占位符，" +
                        "它会自动展开成当前编码器正确的质量参数。", nameof(CommandTemplate.CommandArgs)));
                }
            }
            else if (EncoderCatalog.Find(required) == null)
            {
                issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning,
                    $"无法识别的编码器「{required}」。可用族名：nvenc / qsv / amf / videotoolbox / vaapi / vulkan，" +
                    "或具体编码器名如 h264_nvenc。", nameof(CommandTemplate.RequiredEncoder)));
            }
        }

        if (template.HardwareMode == HardwareAccelerationMode.Required && string.IsNullOrWhiteSpace(template.RequiredEncoder))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error,
                "模板要求必须使用硬件加速，但没有指定编码器族。这样在任何机器上都会失败。",
                nameof(CommandTemplate.RequiredEncoder)));
        }

        // 硬件模板必须使用 {encoder}，否则会被硬编码成某个厂商的编码器
        if (template.HardwareMode != HardwareAccelerationMode.None &&
            !template.CommandArgs.Contains("{encoder}", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning,
                "硬件加速模板建议使用 {encoder} 占位符，这样在本机没有该厂商硬件时会自动回退到可用的编码器。",
                nameof(CommandTemplate.CommandArgs)));
        }

        if (template.CommandArgs.Contains("-c copy", StringComparison.OrdinalIgnoreCase) ||
            template.CommandArgs.Contains("-c:v copy", StringComparison.OrdinalIgnoreCase) ||
            template.CommandArgs.Contains("-codec copy", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Info,
                "「复制流」模式下画面不会被重新编码。如果源文件的编码格式与目标容器不兼容，转换会失败——" +
                "这正是「快速换容器」模板的已知限制。"));
            if (template.CommandArgs.Contains("-vf", StringComparison.OrdinalIgnoreCase) ||
                template.CommandArgs.Contains("-filter_complex", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new TemplateIssue(TemplateIssueSeverity.Error,
                    "「-c copy」不能与滤镜同时使用：不重新编码就无法应用滤镜。", nameof(CommandTemplate.CommandArgs)));
            }
        }

        foreach (var parameter in template.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning, "存在一个没有名字的参数，它不会被使用。",
                    nameof(CommandTemplate.Parameters)));
                continue;
            }
            if (CommandLineBuilder.BuiltInPlaceholders.Contains(parameter.Name) &&
                parameter.Name is not ("quality" or "q" or "crf"))
            {
                issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning,
                    $"参数名「{parameter.Name}」与内置占位符同名，内置值可能被覆盖。建议改名。",
                    nameof(CommandTemplate.Parameters)));
            }
            if (parameter.Kind == TemplateParameterKind.Select && parameter.Options.Count == 0)
            {
                issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning,
                    $"参数「{parameter.Label ?? parameter.Name}」是下拉选项但没有配置任何选项。",
                    nameof(CommandTemplate.Parameters)));
            }
        }

        if (!placeholders.Any(p => string.Equals(p, "encoder", StringComparison.OrdinalIgnoreCase)) &&
            template.CommandArgs.Contains("{quality_args}", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning,
                "使用了 {quality_args} 但没有 {encoder}：质量参数将按默认编码器展开。",
                nameof(CommandTemplate.CommandArgs)));
        }

        if (template.RequiredFilters.Count > 0)
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Info,
                $"该模板依赖以下 FFmpeg 滤镜：{string.Join("、", template.RequiredFilters)}。" +
                "如果当前 FFmpeg 构建没有编译它们，模板会显示为不可用。",
                nameof(CommandTemplate.RequiredFilters)));
        }

        return issues;
    }

    public IReadOnlyList<string> GetMissingFilters(CommandTemplate template, HardwareDetectionReport? report)
    {
        if (template.RequiredFilters.Count == 0) return [];
        // 报告里没有滤镜清单（例如尚未完成检测）时不做判断，避免误报不可用
        if (report == null || report.SupportedFilters.Count == 0) return [];

        return template.RequiredFilters
            .Where(f => !report.HasFilter(f))
            .ToList();
    }

    // ────────────────────────────────────────────────────────────────
    // 试运行
    // ────────────────────────────────────────────────────────────────

    public async Task<TemplateTestResult> TestTemplateAsync(
        CommandTemplate template,
        IReadOnlyDictionary<string, string>? parameterValues = null)
    {
        var issues = Validate(template);
        var errors = issues.Where(i => i.Severity == TemplateIssueSeverity.Error).ToList();

        var filterReport = await _hardwareDetection.DetectAsync().ConfigureAwait(false);
        var missingFilters = GetMissingFilters(template, filterReport);
        if (missingFilters.Count > 0)
        {
            return new TemplateTestResult(false,
                $"当前 FFmpeg 构建缺少该模板需要的滤镜：{string.Join("、", missingFilters)}。" +
                "请换用其它模板，或改用带完整滤镜支持的 FFmpeg 构建（如 gyan.dev / Homebrew 的完整版）。",
                template.CommandArgs, string.Empty, TimeSpan.Zero);
        }
        if (errors.Count > 0)
        {
            return new TemplateTestResult(false,
                "模板校验未通过：" + string.Join("；", errors.Select(e => e.Message)),
                template.CommandArgs, string.Empty, TimeSpan.Zero);
        }

        var settings = (await _settingsService.GetSettingsAsync().ConfigureAwait(false)).Normalize();
        var info = await _ffmpegService.GetFFmpegInfoAsync().ConfigureAwait(false);
        if (info is null || !info.IsAvailable)
        {
            return new TemplateTestResult(false, "FFmpeg 不可用，无法试运行。请先在设置中指定 FFmpeg 路径。",
                template.CommandArgs, string.Empty, TimeSpan.Zero);
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), $"ffmpegwebui-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);

        var extension = string.IsNullOrWhiteSpace(template.OutputExtension) ? "mp4" : template.OutputExtension.TrimStart('.');
        var inputPath = Path.Combine(workDirectory, "sample.mp4");
        var outputPath = Path.Combine(workDirectory, $"result.{extension}");

        try
        {
            // 生成一段 3 秒、带音轨的测试素材
            var generated = await GenerateSampleAsync(inputPath).ConfigureAwait(false);
            if (!generated)
            {
                return new TemplateTestResult(false, "无法生成测试素材，请检查 FFmpeg 是否完整。",
                    template.CommandArgs, string.Empty, TimeSpan.Zero);
            }

            var parameters = CreateDefaultParameterValues(template);
            if (parameterValues != null)
            {
                foreach (var (key, value) in parameterValues)
                {
                    parameters[key] = value ?? string.Empty;
                }
            }

            // 试运行时把需要用户提供的路径参数替换成测试素材，保证流程可跑通
            SubstituteRequiredPaths(template, parameters, inputPath, workDirectory);

            var media = await _ffmpegService.GetMediaInfoAsync(inputPath).ConfigureAwait(false);
            var encoder = await ResolveEncoderForTestAsync(template).ConfigureAwait(false);

            // 模板要求某厂商硬件但本机没有：直接给出可读结论，不假装能跑
            if (encoder is null)
            {
                var family = EncoderCatalog.FamilyDisplayName(
                    EncoderCatalog.Find(template.RequiredEncoder ?? "")?.Family ?? template.RequiredEncoder ?? "");
                return new TemplateTestResult(false,
                    $"❌ 该模板要求使用 {family} 硬件编码，但本机没有可用的对应编码器。" +
                    "请改用「推荐」分类下会自动挑选编码器的模板。",
                    template.CommandArgs, string.Empty, TimeSpan.Zero);
            }

            var context = new CommandContext
            {
                InputPath = inputPath,
                OutputPath = outputPath,
                Parameters = parameters,
                Encoder = encoder,
                Media = media,
                AudioEncoder = "aac",
                GlobalArguments = string.Empty
            };

            var built = _ffmpegService.BuildCommand(template, context);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var run = await _ffmpegService.RunAsync(new FFmpegRunRequest
            {
                TaskId = ObjectId.NewObjectId(),
                Arguments = built.Arguments,
                TotalDuration = media?.Duration ?? 0,
                OutputPath = outputPath,
                Overwrite = true,
                Timeout = TimeSpan.FromMinutes(3),
                CleanupPartialOutput = true
            }).ConfigureAwait(false);
            stopwatch.Stop();

            var outputSize = _fileService.GetFileSize(outputPath);
            var commandText = "ffmpeg -hide_banner -y " + built.CommandLine;

            if (run.Success && outputSize > 0)
            {
                var suffix = encoder?.IsHardware == true
                    ? $"（使用硬件编码器 {encoder.Name}）"
                    : encoder != null ? $"（使用编码器 {encoder.Name}）" : string.Empty;
                return new TemplateTestResult(true,
                    $"✅ 模板试运行成功{suffix}，输出 {_fileService.FormatFileSize(outputSize)}。",
                    commandText, run.Diagnostic, stopwatch.Elapsed);
            }

            var reason = run.Cancelled ? "试运行被取消"
                : run.TimedOut ? "试运行超时（超过 3 分钟）"
                : $"FFmpeg 退出码 {run.ExitCode}";
            return new TemplateTestResult(false, $"❌ 模板试运行失败：{reason}", commandText, run.Diagnostic, stopwatch.Elapsed);
        }
        finally
        {
            try { Directory.Delete(workDirectory, recursive: true); } catch { }
        }
    }

    private async Task<bool> GenerateSampleAsync(string path)
    {
        var run = await _ffmpegService.RunAsync(new FFmpegRunRequest
        {
            TaskId = ObjectId.NewObjectId(),
            Arguments =
            [
                "-f", "lavfi", "-i", "testsrc2=duration=3:size=640x360:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", path
            ],
            TotalDuration = 3,
            Overwrite = true,
            Timeout = TimeSpan.FromSeconds(60)
        }).ConfigureAwait(false);
        return run.Success && File.Exists(path);
    }

    /// <summary>把只用于真实业务的路径参数替换成测试素材，让包含水印/字幕的模板也能试跑</summary>
    private static void SubstituteRequiredPaths(CommandTemplate template, Dictionary<string, string> parameters, string samplePath, string workDirectory)
    {
        foreach (var parameter in template.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name)) continue;

            var unresolved = !parameters.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value);
            var looksLikePath = parameter.Name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                                parameter.Name.Contains("file", StringComparison.OrdinalIgnoreCase) ||
                                parameter.Name.Contains("watermark", StringComparison.OrdinalIgnoreCase) ||
                                parameter.Name.Contains("subtitle", StringComparison.OrdinalIgnoreCase) ||
                                parameter.Name.Contains("image", StringComparison.OrdinalIgnoreCase);

            if (!looksLikePath) continue;
            if (!unresolved && File.Exists(AppPaths.ToAbsolute(value!))) continue;

            parameters[parameter.Name] = samplePath;
            _ = workDirectory;
        }
    }

    private async Task<EncoderProfile?> ResolveEncoderForTestAsync(CommandTemplate template)
    {
        if (template.HardwareMode == HardwareAccelerationMode.None &&
            !template.CommandArgs.Contains("{encoder}", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var codec = string.IsNullOrWhiteSpace(template.TargetCodec)
            ? HardwareDetectionService.NormalizeCodec(template.OutputExtension)
            : HardwareDetectionService.NormalizeCodec(template.TargetCodec);

        if (codec is not ("h264" or "hevc" or "av1" or "vp9" or "vp8"))
        {
            codec = "h264";
        }

        var strict = template.HardwareMode == HardwareAccelerationMode.Required;
        var name = await _hardwareDetection
            .GetRecommendedEncoderAsync(codec, template.RequiredEncoder, strict)
            .ConfigureAwait(false);

        return name is null ? null : EncoderCatalog.Find(name) ?? EncoderCatalog.SoftwareFallback(codec);
    }

    // ────────────────────────────────────────────────────────────────
    // 导入 / 导出
    // ────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoderUnsafeRelaxed,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly System.Text.Encodings.Web.JavaScriptEncoder JavaScriptEncoderUnsafeRelaxed =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public string ExportToJson(IEnumerable<CommandTemplate> templates)
    {
        var payload = new TemplateExportFile
        {
            ExportedAt = DateTime.UtcNow,
            AppVersion = AppInfo.Version,
            PresetVersion = PresetVersion,
            Templates = templates.Select(t => new TemplateExportItem
            {
                Name = t.Name,
                Description = t.Description,
                CommandArgs = t.CommandArgs,
                Category = t.Category,
                SupportedInputFormats = t.SupportedInputFormats,
                OutputExtension = t.OutputExtension,
                TargetCodec = t.TargetCodec,
                HardwareMode = t.HardwareMode,
                RequiredEncoder = t.RequiredEncoder,
                Parameters = t.Parameters,
                Tags = t.Tags,
                SortOrder = t.SortOrder
            }).ToList()
        };

        return System.Text.Json.JsonSerializer.Serialize(payload, JsonOptions);
    }

    public Task<int> ImportFromJsonAsync(string json, bool asSystemTemplate = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return Task.FromResult(0);

        TemplateExportFile? payload;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<TemplateExportFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"模板文件不是有效的 JSON：{ex.Message}", ex);
        }

        if (payload?.Templates == null || payload.Templates.Count == 0) return Task.FromResult(0);

        var imported = 0;
        foreach (var item in payload.Templates)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.CommandArgs)) continue;

            var template = new CommandTemplate
            {
                Name = item.Name,
                Description = item.Description ?? string.Empty,
                CommandArgs = item.CommandArgs,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "导入" : item.Category,
                SupportedInputFormats = item.SupportedInputFormats ?? [],
                OutputExtension = item.OutputExtension ?? string.Empty,
                TargetCodec = item.TargetCodec ?? string.Empty,
                HardwareMode = item.HardwareMode,
                RequiresHardwareAcceleration = item.HardwareMode == HardwareAccelerationMode.Required,
                RequiredEncoder = item.RequiredEncoder,
                Parameters = CloneParameters(item.Parameters ?? []),
                Tags = item.Tags ?? [],
                SortOrder = item.SortOrder,
                Type = asSystemTemplate ? TemplateType.System : TemplateType.User,
                PresetVersion = asSystemTemplate ? PresetVersion : 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            NormalizeTemplate(template);
            template.Name = EnsureUniqueName(template.Name, null);
            Db.Templates.Upsert(template);
            imported++;
        }

        return Task.FromResult(imported);
    }

    private sealed class TemplateExportFile
    {
        public string Format { get; set; } = "FFmpegWebUI.Templates";
        public int SchemaVersion { get; set; } = 1;
        public DateTime ExportedAt { get; set; }
        public string AppVersion { get; set; } = string.Empty;
        public int PresetVersion { get; set; }
        public List<TemplateExportItem> Templates { get; set; } = [];
    }

    private sealed class TemplateExportItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string CommandArgs { get; set; } = string.Empty;
        public string? Category { get; set; }
        public List<string>? SupportedInputFormats { get; set; }
        public string? OutputExtension { get; set; }
        public string? TargetCodec { get; set; }
        public HardwareAccelerationMode HardwareMode { get; set; }
        public string? RequiredEncoder { get; set; }
        public List<TemplateParameter>? Parameters { get; set; }
        public List<string>? Tags { get; set; }
        public int SortOrder { get; set; }
    }

    // ────────────────────────────────────────────────────────────────
    // 工具
    // ────────────────────────────────────────────────────────────────

    private static void NormalizeTemplate(CommandTemplate template)
    {
        template.Name = (template.Name ?? string.Empty).Trim();
        template.Description = (template.Description ?? string.Empty).Trim();
        template.CommandArgs = (template.CommandArgs ?? string.Empty).Trim();
        template.Category = string.IsNullOrWhiteSpace(template.Category) ? "未分类" : template.Category.Trim();
        template.OutputExtension = (template.OutputExtension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        template.TargetCodec = (template.TargetCodec ?? string.Empty).Trim();
        template.RequiredEncoder = string.IsNullOrWhiteSpace(template.RequiredEncoder) ? null : template.RequiredEncoder.Trim();

        // 保持「是否需要硬件加速」与策略一致
        template.RequiresHardwareAcceleration = template.HardwareMode == HardwareAccelerationMode.Required;

        if (template.HardwareMode != HardwareAccelerationMode.None && template.RequiredEncoder == null &&
            template.HardwareMode == HardwareAccelerationMode.Required)
        {
            // 交给校验给出明确错误，这里不做静默修正
        }

        template.SupportedInputFormats = template.SupportedInputFormats
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim().TrimStart('.').ToLowerInvariant())
            .Distinct()
            .ToList();

        template.Tags = template.Tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.CurrentCulture)
            .ToList();

        foreach (var parameter in template.Parameters)
        {
            parameter.Name = (parameter.Name ?? string.Empty).Trim().Trim('{', '}');
            parameter.Label = string.IsNullOrWhiteSpace(parameter.Label) ? parameter.Name : parameter.Label.Trim();
            parameter.DefaultValue ??= string.Empty;
            foreach (var option in parameter.Options)
            {
                option.Label = string.IsNullOrWhiteSpace(option.Label) ? option.Value : option.Label;
            }
        }
    }

    private string EnsureUniqueName(string? name, ObjectId? excludeId)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "未命名模板" : name.Trim();
        var candidate = baseName;
        var counter = 1;

        while (true)
        {
            var conflict = Db.Templates.Query()
                .Where(t => t.Name == candidate)
                .ToList()
                .FirstOrDefault(t => excludeId == null || t.Id != excludeId);

            if (conflict == null) return candidate;
            counter++;
            candidate = $"{baseName} ({counter})";
        }
    }

    private static List<TemplateParameter> CloneParameters(List<TemplateParameter> source) =>
        source.Select(p => new TemplateParameter
        {
            Name = p.Name,
            Label = p.Label,
            Description = p.Description,
            Kind = p.Kind,
            DefaultValue = p.DefaultValue,
            Options = p.Options.Select(o => new TemplateParameterOption(o.Value, o.Label, o.Description)).ToList(),
            Min = p.Min,
            Max = p.Max,
            TrueValue = p.TrueValue,
            FalseValue = p.FalseValue
        }).ToList();
}

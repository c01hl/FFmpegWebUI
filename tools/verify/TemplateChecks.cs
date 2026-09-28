using FFmpegWebUI.Models;
using FFmpegWebUI.Services;

namespace FFmpegWebUI.Verify;

/// <summary>
/// 内置模板验证：把每个系统模板都用真实 FFmpeg 跑一遍。
///
/// 这一层是必要的，因为「占位符能不能正确展开」「参数在某个编码器上是否合法」
/// 只有实际执行才能确认。曾经就是靠它发现了参数组占位符被当成单个参数、
/// 以及参数名与内置占位符冲突导致输出路径异常这类问题。
/// </summary>
internal static class TemplateChecks
{
    public static async Task RunAsync(VerifyContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("── 内置模板实测（每个模板都会真实执行一次）──────────────────");

        // 所有模板都以这段素材作为输入
        var sample = await ctx.CreateSampleAsync("template-sample.mp4", seconds: 3);
        var available = await ctx.GetAvailableEncoderNamesAsync();
        var filters = await ctx.GetAvailableFilterNamesAsync();
        var templates = SystemTemplatePresets.CreateAll();

        Console.WriteLine($"     模板总数 {templates.Count}，本机编码器 {available.Count} 个，滤镜 {filters.Count} 个");

        var failed = new List<string>();
        var skipped = new List<string>();
        var refused = new List<string>();
        var executed = 0;

        foreach (var template in templates)
        {
            var extension = string.IsNullOrWhiteSpace(template.OutputExtension)
                ? "mp4"
                : template.OutputExtension.TrimStart('.');
            var output = Path.Combine(ctx.MediaDirectory, $"out-{executed}.{extension}");

            // 模板声明的必需滤镜在本机不存在时，跳过并记录（这不是模板的缺陷）
            var missing = ctx.Templates.GetMissingFilters(template, await ctx.Hardware.DetectAsync());
            if (missing.Count > 0)
            {
                skipped.Add($"{template.Name}（缺少滤镜 {string.Join("、", missing)}）");
                continue;
            }

            var codec = DetermineCodec(template);
            var encoder = ResolveEncoder(template, codec, available);

            // 模板声明「必须用某厂商硬件」而本机没有时，运行时应当拒绝执行而不是
            // 偷偷换成别的厂商编码器。这里把它记为「正确拒绝」，不算失败也不算通过。
            if (encoder is null && template.HardwareMode == HardwareAccelerationMode.Required)
            {
                var family = EncoderCatalog.FamilyDisplayName(
                    EncoderCatalog.Find(template.RequiredEncoder ?? "")?.Family ?? template.RequiredEncoder ?? "");
                refused.Add($"{template.Name}（本机没有 {family}，已正确拒绝执行）");
                continue;
            }

            var parameters = ctx.Templates.CreateDefaultParameterValues(template);
            FillPathParameters(template, parameters, ctx.MediaDirectory, sample);

            var built = ctx.FFmpeg.BuildCommand(template, new CommandContext
            {
                InputPath = sample,
                OutputPath = output,
                Parameters = parameters,
                Encoder = encoder,
                AudioEncoder = "aac"
            });

            // 1) 命令里不应残留任何占位符，也不应出现空参数
            var leftover = built.Arguments.Where(a => a.Contains('{') && a.Contains('}')).ToList();
            if (leftover.Count > 0)
            {
                failed.Add($"{template.Name}: 残留占位符 {string.Join(" ", leftover)}");
                continue;
            }
            if (built.Arguments.Any(string.IsNullOrEmpty))
            {
                failed.Add($"{template.Name}: 生成了空参数，FFmpeg 会因此报错");
                continue;
            }

            // 2) 模板自身不应有致命校验错误
            var errors = ctx.Templates.Validate(template)
                .Where(i => i.Severity == TemplateIssueSeverity.Error)
                .ToList();
            if (errors.Count > 0)
            {
                failed.Add($"{template.Name}: {string.Join("；", errors.Select(e => e.Message))}");
                continue;
            }

            // 3) 实际执行
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-nostdin" };
            args.AddRange(built.Arguments);

            var ok = await RunAsync(ctx.RequestedFfmpegPath, args, TimeSpan.FromMinutes(3));
            var produced = HasOutput(output);
            executed++;

            if (ok && produced)
            {
                Console.WriteLine($"     ✅ {template.Name}{(encoder?.IsHardware == true ? $" [硬件 {encoder.Name}]" : "")}");
            }
            else
            {
                failed.Add($"{template.Name}: ffmpeg 执行失败");
                Console.WriteLine($"     ❌ {template.Name}");
                Console.WriteLine($"        ffmpeg {built.CommandLine}");
            }

            CleanupOutput(output);
        }

        Console.WriteLine();
        ctx.Check($"全部内置模板可执行（已执行 {executed} 个）", failed.Count == 0,
            failed.Count == 0 ? null : string.Join("; ", failed.Take(5)));

        if (skipped.Count > 0)
        {
            Console.WriteLine($"     ⏭️  跳过 {skipped.Count} 个（当前 FFmpeg 构建缺少所需滤镜，界面会标记为不可用）：");
            foreach (var item in skipped) Console.WriteLine($"        · {item}");
        }

        if (refused.Count > 0)
        {
            Console.WriteLine($"     🚫 正确拒绝 {refused.Count} 个（本机缺少该厂商硬件，界面同样会标记为不可用）：");
            foreach (var item in refused) Console.WriteLine($"        · {item}");
        }
    }

    private static string DetermineCodec(CommandTemplate template)
    {
        if (!string.IsNullOrWhiteSpace(template.TargetCodec))
        {
            return HardwareDetectionService.NormalizeCodec(template.TargetCodec);
        }
        if (!string.IsNullOrWhiteSpace(template.RequiredEncoder))
        {
            var profile = EncoderCatalog.Find(template.RequiredEncoder);
            if (profile != null) return profile.Codec;
            var family = EncoderCatalog.ByFamily(template.RequiredEncoder).FirstOrDefault();
            if (family != null) return family.Codec;
        }
        var fromExtension = HardwareDetectionService.NormalizeCodec(template.OutputExtension);
        return fromExtension is "h264" or "hevc" or "av1" or "vp9" ? fromExtension : "h264";
    }

    /// <summary>
    /// 模拟运行时的编码器选择。
    /// HardwareMode.Required 时严格匹配编码器族：本机没有就返回 null（运行时也会拒绝执行）。
    /// 只有 Auto / 显式软编码模板才会回退到其它编码器。
    /// </summary>
    private static EncoderProfile? ResolveEncoder(CommandTemplate template, string codec, HashSet<string> available)
    {
        var usesEncoder = template.CommandArgs.Contains("{encoder}", StringComparison.OrdinalIgnoreCase)
                          || template.HardwareMode != HardwareAccelerationMode.None;
        if (!usesEncoder) return null;

        if (!string.IsNullOrWhiteSpace(template.RequiredEncoder))
        {
            foreach (var name in EncoderCatalog.ExpandFamily(template.RequiredEncoder))
            {
                var profile = EncoderCatalog.Find(name);
                if (profile != null && available.Contains(name) && profile.Codec == codec) return profile;
            }

            // 严格要求：没有该族的编码器就不选别的
            if (template.HardwareMode == HardwareAccelerationMode.Required) return null;
        }

        foreach (var candidate in EncoderCatalog.ByCodec(codec))
        {
            if (available.Contains(candidate.Name)) return candidate;
        }

        return EncoderCatalog.SoftwareFallback(codec);
    }

    /// <summary>把需要用户提供文件的参数替换成测试素材，让水印/字幕类模板也能跑通</summary>
    private static void FillPathParameters(
        CommandTemplate template, Dictionary<string, string> parameters, string mediaDirectory, string fallbackFile)
    {
        foreach (var parameter in template.Parameters)
        {
            var looksLikePath = parameter.Name.Contains("path", StringComparison.OrdinalIgnoreCase)
                                || parameter.Name.Contains("file", StringComparison.OrdinalIgnoreCase)
                                || parameter.Name.Contains("watermark", StringComparison.OrdinalIgnoreCase)
                                || parameter.Name.Contains("subtitle", StringComparison.OrdinalIgnoreCase)
                                || parameter.Name.Contains("image", StringComparison.OrdinalIgnoreCase);
            if (!looksLikePath) continue;

            if (parameter.Name.Contains("subtitle", StringComparison.OrdinalIgnoreCase))
            {
                var srt = Path.Combine(mediaDirectory, "verify-subtitle.srt");
                File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:02,000\n验证字幕\n");
                parameters[parameter.Name] = srt;
            }
            else
            {
                parameters[parameter.Name] = fallbackFile;
            }
        }
    }

    private static bool HasOutput(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0) return true;

        // 图片序列输出的是 out_0001.jpg 这样的多个文件
        var directory = Path.GetDirectoryName(path);
        if (directory is null) return false;
        var pattern = Path.GetFileNameWithoutExtension(path) + "*" + Path.GetExtension(path);
        return Directory.Exists(directory) &&
               Directory.EnumerateFiles(directory, pattern).Any(f => new FileInfo(f).Length > 0);
    }

    private static void CleanupOutput(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path);
            if (directory is null) return;
            var pattern = Path.GetFileNameWithoutExtension(path) + "*" + Path.GetExtension(path);
            foreach (var extra in Directory.EnumerateFiles(directory, pattern))
            {
                try { File.Delete(extra); } catch { /* 忽略 */ }
            }
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private static async Task<bool> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        try { process.Start(); } catch { return false; }
        try { process.StandardInput.Close(); } catch { /* 已关闭 */ }

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            return false;
        }

        var stderr = await SafeRead(stderrTask);
        _ = await SafeRead(stdoutTask);
        if (process.ExitCode != 0) VerifyContext.Info(stderr.Trim());
        return process.ExitCode == 0;
    }

    private static async Task<string> SafeRead(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { return string.Empty; }
    }
}

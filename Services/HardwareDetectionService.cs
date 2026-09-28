using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FFmpegWebUI.Data;
using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>
/// 硬件编码器检测：
/// 仅检测当前平台可能存在的编码器，并用「真实参数试跑」确认可用性。
/// 每个编码器会依次尝试「完整参数 / 最简参数」两档，只要能跑通就认为可用，
/// 因此不同 FFmpeg 构建之间选项名差异不会导致误判。
/// </summary>
public sealed class HardwareDetectionService(
    ILiteDbContext db,
    ISettingsService settingsService,
    IFFmpegService ffmpegService,
    IFFmpegLocator locator) : IHardwareDetectionService
{
    private const string CacheCollectionName = "encoders";

    private readonly SemaphoreSlim _detectionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _probeCache = new(StringComparer.Ordinal);
    private HardwareDetectionReport? _lastReport;

    public HardwareDetectionReport? LastReport => _lastReport;

    public DateTime? LastDetectionTime => _lastReport?.DetectedAt;

    public EncoderProfile? ResolveProfile(string encoderName) => EncoderCatalog.Find(encoderName);

    // ────────────────────────────────────────────────────────────────

    public async Task<HardwareDetectionReport> DetectAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // 先看内存缓存
        if (!forceRefresh && _lastReport != null && (DateTime.UtcNow - _lastReport.DetectedAt).TotalMinutes < 30)
        {
            return _lastReport;
        }

        await _detectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _lastReport != null && (DateTime.UtcNow - _lastReport.DetectedAt).TotalMinutes < 30)
            {
                return _lastReport;
            }

            var info = await ffmpegService.GetFFmpegInfoAsync(forceRefresh).ConfigureAwait(false);
            var settings = (await settingsService.GetSettingsAsync().ConfigureAwait(false)).Normalize();
            var paths = locator.Resolve();

            if (info is null || !info.IsAvailable)
            {
                _lastReport = new HardwareDetectionReport
                {
                    DetectedAt = DateTime.UtcNow,
                    Encoders = BuildUnavailableCatalog("未找到可用的 FFmpeg，请先在设置中指定 FFmpeg 路径。" + paths.Hint),
                    FFmpegVersion = "未检测到",
                    FFmpegPath = paths.FFmpeg,
                    Platform = AppPaths.PlatformDisplayName,
                    Summary = "未检测到 FFmpeg，无法进行硬件加速检测。"
                };
                return _lastReport;
            }

            var version = info.Version;
            // 数据库缓存：仅当 FFmpeg 版本与可执行文件路径都没变时才复用
            if (!forceRefresh)
            {
                var cached = TryLoadFromDatabase(version, info.Path);
                if (cached != null)
                {
                    _lastReport = cached;
                    return cached;
                }
            }

            var buildEncoders = info.SupportedEncoders.ToHashSet(StringComparer.Ordinal);
            var results = new ConcurrentBag<HardwareEncoder>();

            // 只探测当前平台可能存在的编码器；软件编码器只检查是否被编译进来
            var candidates = EncoderCatalog.Profiles
                .Where(p => p.SupportsCurrentPlatform)
                .ToList();

            var hardwareCandidates = candidates
                .Where(p => p.IsHardware && buildEncoders.Contains(p.Name))
                .ToList();

            var probes = new List<Task>();
            using var probeGate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

            foreach (var profile in candidates)
            {
                if (!profile.IsHardware)
                {
                    // 软件编码器：只要被编译进来就可用（无需试跑，避免浪费时间）
                    results.Add(CreateEntry(profile, buildEncoders.Contains(profile.Name),
                        buildEncoders.Contains(profile.Name) ? null : "该编码器不在当前 FFmpeg 构建中",
                        missingFromBuild: !buildEncoders.Contains(profile.Name),
                        supportsQuality: true,
                        version));
                    continue;
                }

                if (!buildEncoders.Contains(profile.Name))
                {
                    results.Add(CreateEntry(profile, false, "该编码器不在当前 FFmpeg 构建中（FFmpeg 未启用对应硬件加速）",
                        missingFromBuild: true, supportsQuality: true, version));
                    continue;
                }

                probes.Add(Task.Run(async () =>
                {
                    await probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var probe = await ProbeEncoderAsync(profile, cancellationToken).ConfigureAwait(false);
                        results.Add(CreateEntry(profile, probe.Available, probe.Reason,
                            missingFromBuild: false, supportsQuality: probe.SupportsQualityOptions, version));
                    }
                    finally
                    {
                        probeGate.Release();
                    }
                }, cancellationToken));
            }

            try
            {
                await Task.WhenAll(probes).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消时返回已完成的部分结果
            }

            var ordered = results
                .OrderBy(e => e.Type == EncoderType.Software ? 1 : 0)
                .ThenBy(e => e.Family)
                .ThenBy(e => e.Codec)
                .ThenBy(e => e.Name)
                .ToList();

            var availableHardware = ordered.Count(e => e.IsAvailable && e.IsHardware);
            var report = new HardwareDetectionReport
            {
                Encoders = ordered,
                DetectedAt = DateTime.UtcNow,
                FFmpegVersion = version,
                FFmpegPath = info.Path,
                Platform = AppPaths.PlatformDisplayName,
                Hwaccels = info.SupportedHwaccels,
                SupportedFilters = info.SupportedFilters,
                Summary = availableHardware > 0
                    ? $"检测到 {availableHardware} 个可用硬件编码器（{string.Join("、", ordered.Where(e => e.IsAvailable && e.IsHardware).Select(e => e.DisplayName).Distinct().Take(4))}）"
                    : "未检测到可用的硬件编码器，将使用软件编码。"
            };

            SaveToDatabase(report);
            _lastReport = report;
            return report;
        }
        finally
        {
            _detectionGate.Release();
        }
    }

    public async Task<bool> IsEncoderAvailableAsync(string encoderName)
    {
        var report = await DetectAsync().ConfigureAwait(false);
        return report.Encoders.Any(e => e.IsAvailable &&
            string.Equals(e.Name, encoderName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string?> GetRecommendedEncoderAsync(string codec, string? requiredEncoder = null, bool strict = false)
    {
        var report = await DetectAsync().ConfigureAwait(false);
        var settings = (await settingsService.GetSettingsAsync().ConfigureAwait(false)).Normalize();
        var normalizedCodec = NormalizeCodec(codec);

        var available = report.Encoders.Where(e => e.IsAvailable).ToList();

        // 1) 模板/调用方指定了必需编码器（族名或具体名）
        if (!string.IsNullOrWhiteSpace(requiredEncoder))
        {
            var members = EncoderCatalog.ExpandFamily(requiredEncoder);
            foreach (var member in members)
            {
                var hit = available.FirstOrDefault(e =>
                    string.Equals(e.Name, member, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(normalizedCodec) || string.Equals(e.Codec, normalizedCodec, StringComparison.OrdinalIgnoreCase)));
                if (hit != null) return hit.Name;
            }

            // 严格模式下：该族没有可用成员就直接返回 null，由调用方给出明确错误。
            // 宽松模式（默认）继续走自动选择，用于「有硬件就用、没有就软件」的场景。
            if (strict) return null;
        }

        // 2) 用户在设置中指定的首选编码器
        if (!string.IsNullOrWhiteSpace(settings.PreferredHardwareEncoder))
        {
            var hit = available.FirstOrDefault(e =>
                string.Equals(e.Name, settings.PreferredHardwareEncoder, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(normalizedCodec) || string.Equals(e.Codec, normalizedCodec, StringComparison.OrdinalIgnoreCase)));
            if (hit != null) return hit.Name;
        }

        // 3) 自动选择：优先硬件，其次软件
        var candidates = EncoderCatalog.ByCodec(normalizedCodec)
            .Where(p => p.SupportsCurrentPlatform)
            .ToList();

        if (settings.PreferHardwareAcceleration)
        {
            foreach (var candidate in candidates.Where(c => c.IsHardware))
            {
                if (available.Any(e => string.Equals(e.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate.Name;
                }
            }
        }

        foreach (var candidate in candidates.Where(c => !c.IsHardware))
        {
            if (available.Any(e => string.Equals(e.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate.Name;
            }
        }

        // 4) 兜底
        return normalizedCodec switch
        {
            "hevc" => available.Any(e => e.Name == "libx265") ? "libx265" : "libx264",
            _ => "libx264"
        };
    }

    /// <summary>把各种写法归一到编码格式标识</summary>
    public static string NormalizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return string.Empty;
        return codec.Trim().ToLowerInvariant() switch
        {
            "avc" or "h264" or "x264" or "avc1" => "h264",
            "h265" or "hevc" or "x265" or "hvc1" or "hev1" => "hevc",
            "av1" or "av01" => "av1",
            "vp9" or "vp09" => "vp9",
            "vp8" => "vp8",
            "prores" => "prores",
            "aac" => "aac",
            "mp3" => "mp3",
            "opus" => "opus",
            "flac" => "flac",
            var other => other
        };
    }

    // ────────────────────────────────────────────────────────────────
    // 试跑探测
    // ────────────────────────────────────────────────────────────────

    private sealed record ProbeOutcome(bool Available, bool SupportsQualityOptions, string? Reason);

    private async Task<ProbeOutcome> ProbeEncoderAsync(EncoderProfile profile, CancellationToken cancellationToken)
    {
        if (_probeCache.TryGetValue(profile.Name, out var cached))
        {
            return cached
                ? new ProbeOutcome(true, true, null)
                : new ProbeOutcome(false, false, "试跑失败（缓存结果）");
        }

        // 第 1 档：带质量参数与预设（模板实际会用的参数）
        var fullOutput = ProbeOutputPath(profile.Name, "full");
        var full = BuildProbeArguments(profile, includeQuality: true, fullOutput);
        var fullResult = await ExecuteProbeAsync(full, cancellationToken).ConfigureAwait(false);
        if (fullResult.Success)
        {
            _probeCache[profile.Name] = true;
            return new ProbeOutcome(true, true, null);
        }

        // 第 2 档：仅指定编码器（兼容参数名发生变化的 FFmpeg 构建）
        var minimalOutput = ProbeOutputPath(profile.Name, "min");
        var minimal = BuildProbeArguments(profile, includeQuality: false, minimalOutput);
        var minimalResult = await ExecuteProbeAsync(minimal, cancellationToken).ConfigureAwait(false);
        if (minimalResult.Success)
        {
            _probeCache[profile.Name] = true;
            return new ProbeOutcome(true, false,
                "可用，但当前 FFmpeg 版本不接受该编码器的质量参数，将使用编码器默认质量");
        }

        _probeCache[profile.Name] = false;
        var reason = SummarizeFailure(fullResult.Diagnostic);
        return new ProbeOutcome(false, false, reason);
    }

    private static string ProbeOutputPath(string encoderName, string stage)
    {
        var safe = string.Concat(encoderName.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-'));
        return Path.Combine(Path.GetTempPath(), $"ffmpegwebui-probe-{safe}-{stage}-{Guid.NewGuid():N}.raw");
    }

    private static List<string> BuildProbeArguments(EncoderProfile profile, bool includeQuality, string outputPath)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };

        // 设备初始化（VAAPI 等）
        if (!string.IsNullOrWhiteSpace(profile.DeviceArgs))
        {
            args.AddRange(CommandLineBuilder.Tokenize(profile.DeviceArgs));
        }

        args.AddRange(["-f", "lavfi", "-i", "testsrc2=duration=0.3:size=320x240:rate=25"]);

        // 显存上传滤镜
        if (!string.IsNullOrWhiteSpace(profile.UploadFilter))
        {
            args.AddRange(["-vf", profile.UploadFilter]);
        }

        args.AddRange(["-c:v", profile.Name]);

        if (includeQuality)
        {
            var quality = profile.DefaultQuality.ToString();
            if (!string.IsNullOrWhiteSpace(profile.QualityArgs))
            {
                args.AddRange(CommandLineBuilder.Tokenize(profile.QualityArgs.Replace("{q}", quality)));
            }
            if (!string.IsNullOrWhiteSpace(profile.PresetArgs))
            {
                args.AddRange(CommandLineBuilder.Tokenize(profile.PresetArgs));
            }
        }

        // 用 rawvideo 落盘：既能拿到真实的退出码，又能通过文件大小确认确实编出了帧
        args.AddRange(["-frames:v", "3", "-f", "rawvideo", "-y", outputPath]);
        return args;
    }

    private sealed record ProbeRunResult(bool Success, string Diagnostic);

    private async Task<ProbeRunResult> ExecuteProbeAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var paths = locator.Resolve();
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.FFmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        // 探针输出文件（args 最后一项），用完即删
        var outputPath = arguments.Count > 0 ? arguments[^1] : null;
        long outputSize = 0;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProbeRunResult(false, $"无法启动 FFmpeg：{ex.Message}");
        }

        // 关闭 stdin：即使某个选项触发交互提问也不会永久挂起
        try { process.StandardInput.Close(); } catch { }

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }

        var stderr = string.Empty;
        var stdout = string.Empty;
        try
        {
            await Task.WhenAny(Task.WhenAll(stderrTask, stdoutTask), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            if (stderrTask.IsCompletedSuccessfully) stderr = stderrTask.Result;
            if (stdoutTask.IsCompletedSuccessfully) stdout = stdoutTask.Result;
        }
        catch
        {
            // 忽略读取异常
        }

        if (outputPath != null)
        {
            try
            {
                if (File.Exists(outputPath)) outputSize = new FileInfo(outputPath).Length;
            }
            catch
            {
                // 忽略
            }
            try { File.Delete(outputPath); } catch { }
        }

        if (timedOut)
        {
            return new ProbeRunResult(false, "试跑超时（15 秒内未完成），通常是驱动未安装或设备被占用。");
        }

        var diagnostic = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        var exitCode = -1;
        try { exitCode = process.ExitCode; } catch { }

        if (exitCode != 0)
        {
            return new ProbeRunResult(false, diagnostic);
        }

        if (outputSize <= 0)
        {
            return new ProbeRunResult(false, "试跑没有产生任何编码输出，编码器可能无法真正工作。" + Environment.NewLine + diagnostic);
        }

        return new ProbeRunResult(true, diagnostic);
    }

    private static readonly string[] FatalMarkers =
    [
        "Cannot load",
        "Device creation failed",
        "No capable devices found",
        "Error initializing",
        "not available",
        "Cannot open device",
        "Failed to create",
        "Unrecognized option",
        "Option not found",
        "Invalid argument",
        "No device available",
        "OpenCL error",
        "Error creating",
    ];

    private static string SummarizeFailure(string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic)) return "试跑失败：FFmpeg 未给出更多信息";

        var lines = diagnostic
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var picked = lines.Where(l => FatalMarkers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)))
                          .Take(2)
                          .ToList();
        if (picked.Count == 0) picked = lines.TakeLast(2).ToList();

        var text = string.Join(" ", picked);
        return text.Length > 300 ? text[..300] + "…" : (text.Length == 0 ? "试跑失败" : text);
    }

    // ────────────────────────────────────────────────────────────────
    // 缓存
    // ────────────────────────────────────────────────────────────────

    private static HardwareEncoder CreateEntry(
        EncoderProfile profile, bool available, string? reason, bool missingFromBuild, bool supportsQuality, string version) =>
        new()
        {
            Name = profile.Name,
            DisplayName = profile.DisplayName,
            Type = profile.Type,
            Family = profile.Family,
            Codec = profile.Codec,
            IsAvailable = available,
            SupportsQualityOptions = supportsQuality,
            SupportedCodecs = [profile.Codec],
            LastCheckedAt = DateTime.UtcNow,
            UnavailableReason = available ? null : reason,
            MissingFromBuild = missingFromBuild,
            FFmpegVersion = version
        };

    private static List<HardwareEncoder> BuildUnavailableCatalog(string reason) =>
        EncoderCatalog.Profiles
            .Where(p => p.SupportsCurrentPlatform && !p.IsHardware)
            .Select(p => CreateEntry(p, false, reason, missingFromBuild: true, supportsQuality: true, version: "未检测到"))
            .ToList();

    private HardwareDetectionReport? TryLoadFromDatabase(string version, string ffmpegPath)
    {
        try
        {
            var cached = db.Encoders.FindAll().ToList();
            if (cached.Count == 0) return null;

            // 版本或路径变化后必须重新检测
            if (cached.Any(e => !string.Equals(e.FFmpegVersion, version, StringComparison.Ordinal))) return null;
            if (cached.All(e => !e.IsAvailable)) return null;

            // 超过 24 小时的结果也不再复用
            var newest = cached.Max(e => e.LastCheckedAt);
            if ((DateTime.UtcNow - newest).TotalHours > 24) return null;

            return new HardwareDetectionReport
            {
                Encoders = cached,
                DetectedAt = newest,
                FFmpegVersion = version,
                FFmpegPath = ffmpegPath,
                Platform = AppPaths.PlatformDisplayName,
                Summary = "使用上次检测结果"
            };
        }
        catch
        {
            return null;
        }
    }

    private void SaveToDatabase(HardwareDetectionReport report)
    {
        try
        {
            db.Encoders.DeleteAll();
            db.Encoders.InsertBulk(report.Encoders);
        }
        catch
        {
            // 缓存写入失败不应影响检测结果
        }
    }
}

using FFmpegWebUI.Models;
using LiteDB;

namespace FFmpegWebUI.Services;

/// <summary>FFmpeg 执行服务接口</summary>
public interface IFFmpegService
{
    /// <summary>检测 FFmpeg 是否可用（结果带缓存）</summary>
    Task<FFmpegInfo?> GetFFmpegInfoAsync(bool forceRefresh = false);

    /// <summary>获取媒体文件信息（基于 ffprobe JSON 输出）</summary>
    Task<MediaInfo?> GetMediaInfoAsync(string filePath);

    /// <summary>执行 FFmpeg 进程</summary>
    Task<FFmpegRunResult> RunAsync(FFmpegRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>向正在执行的任务发送输入（仅用于优雅退出等交互命令）</summary>
    Task<bool> SendInputAsync(ObjectId taskId, string input);

    /// <summary>终止指定任务的进程</summary>
    Task KillAsync(ObjectId taskId, bool graceful);

    /// <summary>当前正在运行的 ffmpeg 进程数量</summary>
    int RunningCount { get; }

    /// <summary>解析出的可执行文件路径</summary>
    FFmpegPaths ResolvedPaths { get; }

    /// <summary>构建 FFmpeg 命令行</summary>
    BuiltCommand BuildCommand(CommandTemplate template, CommandContext context);
}

/// <summary>一次 FFmpeg 执行请求</summary>
public sealed record FFmpegRunRequest
{
    /// <summary>关联的任务 ID（用于进程登记）</summary>
    public required ObjectId TaskId { get; init; }

    /// <summary>FFmpeg 参数（不含可执行文件名）</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>输入文件总时长，用于计算百分比</summary>
    public double TotalDuration { get; init; }

    /// <summary>本次运行的输出文件路径（失败时清理）</summary>
    public string? OutputPath { get; init; }

    /// <summary>是否覆盖已存在的输出文件（false 时加 -n，FFmpeg 不会阻塞询问）</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>进度回调（可能在后台线程触发）</summary>
    public Action<ConversionProgress>? OnProgress { get; init; }

    /// <summary>日志回调（逐行，可能在后台线程触发）</summary>
    public Action<string>? OnLogLine { get; init; }

    /// <summary>超时（null 表示不限制）</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>失败时是否删除残留的半成品输出文件</summary>
    public bool CleanupPartialOutput { get; init; } = true;
}

/// <summary>FFmpeg 执行结果</summary>
public sealed record FFmpegRunResult
{
    /// <summary>退出码</summary>
    public required int ExitCode { get; init; }

    /// <summary>是否成功（退出码 0，或被用户优雅中止的 255）</summary>
    public bool Success { get; init; }

    /// <summary>是否被取消</summary>
    public bool Cancelled { get; init; }

    /// <summary>是否超时</summary>
    public bool TimedOut { get; init; }

    /// <summary>是否因「输出文件已存在」被跳过</summary>
    public bool SkippedExistingFile { get; init; }

    /// <summary>stderr 尾部（用于诊断）</summary>
    public string Diagnostic { get; init; } = string.Empty;

    /// <summary>实际耗时</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>FFmpeg 执行服务实现（单例，进程登记表在应用范围内共享）</summary>
public sealed partial class FFmpegService : IFFmpegService, IDisposable
{
    private readonly Dictionary<ObjectId, RunningProcess> _running = [];
    private readonly Lock _gate = new();
    private FFmpegInfo? _cachedInfo;
    private DateTime _infoCachedAt = DateTime.MinValue;

    private readonly ISettingsService _settingsService;
    private readonly IFFmpegLocator _locator;

    public FFmpegService(ISettingsService settingsService, IFFmpegLocator locator)
    {
        _settingsService = settingsService;
        _locator = locator;

        // 用户在设置里换了 FFmpeg 路径后，能力缓存必须立刻失效，
        // 否则界面会一直显示旧版本号 / 旧的编码器列表。
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _cachedInfo = null;
            _infoCachedAt = DateTime.MinValue;
        }
    }

    public void Dispose() => _settingsService.SettingsChanged -= OnSettingsChanged;

    /// <summary>正在运行的 ffmpeg 进程句柄</summary>
    private sealed class RunningProcess
    {
        public required ProcessHandle Handle { get; init; }
        public required ObjectId TaskId { get; init; }
    }

    /// <summary>抽象的进程句柄，便于封装终止/写入逻辑</summary>
    public sealed class ProcessHandle
    {
        public required System.Diagnostics.Process Process { get; init; }
        public required string CommandLine { get; init; }
        public volatile bool Cancelled;
    }

    public FFmpegPaths ResolvedPaths => _locator.Resolve();

    public int RunningCount
    {
        get
        {
            lock (_gate) return _running.Count;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 版本 / 能力探测
    // ────────────────────────────────────────────────────────────────

    public async Task<FFmpegInfo?> GetFFmpegInfoAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedInfo != null && (DateTime.UtcNow - _infoCachedAt).TotalMinutes < 30)
        {
            return _cachedInfo;
        }

        var paths = _locator.Resolve();
        var version = await RunCaptureAsync(paths.FFmpeg, ["-hide_banner", "-version"]).ConfigureAwait(false);
        if (version.ExitCode != 0 || string.IsNullOrWhiteSpace(version.StdOut))
        {
            return null;
        }

        var info = ParseVersionOutput(version.StdOut, paths);

        // 能力清单
        var formatsTask = RunCaptureAsync(paths.FFmpeg, ["-hide_banner", "-formats"]);
        var codecsTask = RunCaptureAsync(paths.FFmpeg, ["-hide_banner", "-codecs"]);
        var encodersTask = RunCaptureAsync(paths.FFmpeg, ["-hide_banner", "-encoders"]);
        var hwaccelsTask = RunCaptureAsync(paths.FFmpeg, ["-hide_banner", "-hwaccels"]);
        var filtersTask = RunCaptureAsync(paths.FFmpeg, ["-hide_banner", "-filters"]);
        await Task.WhenAll(formatsTask, codecsTask, encodersTask, hwaccelsTask, filtersTask).ConfigureAwait(false);

        info = info with
        {
            SupportedFormats = ParseListingTable(formatsTask.Result.StdOut, Flags: 3),
            SupportedCodecs = ParseListingTable(codecsTask.Result.StdOut, Flags: 6),
            SupportedEncoders = ParseListingTable(encodersTask.Result.StdOut, Flags: 6),
            SupportedHwaccels = ParseHwaccels(hwaccelsTask.Result.StdOut),
            SupportedFilters = ParseFilters(filtersTask.Result.StdOut)
        };

        _cachedInfo = info;
        _infoCachedAt = DateTime.UtcNow;
        return info;
    }

    private static FFmpegInfo ParseVersionOutput(string output, FFmpegPaths paths)
    {
        var version = "Unknown";
        var configuration = string.Empty;
        var major = 0;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            {
                var rest = line["ffmpeg version".Length..].Trim();
                // 形如 "n7.1.1" / "9.0.2" / "N-118527-g..." / "6.1.1-3ubuntu5"
                var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Unknown";
                version = token;
                var digits = new string(token.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var parsed)) major = parsed;
            }
            else if (line.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase))
            {
                configuration = line["configuration:".Length..].Trim();
            }
        }

        return new FFmpegInfo(
            version,
            paths.FFmpeg,
            configuration,
            [],
            [],
            [],
            [],
            [])
        {
            MajorVersion = major,
            IsAutoDetected = paths.IsAutoDetected
        };
    }

    /// <summary>
    /// 解析 `-formats` / `-codecs` / `-encoders` 的表格输出。
    /// 表格行形如：<c> DEV.L. h264 ...</c> 或 <c> V....D h264_qsv ...</c>，
    /// flags 表示名称前标志位的最大长度。
    /// </summary>
    private static List<string> ParseListingTable(string output, int Flags)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 2) continue;

            // 跳过标题、说明和分隔行
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            if (!char.IsUpper(trimmed[0])) continue;
            if (trimmed.StartsWith("Encoders:", StringComparison.Ordinal) ||
                trimmed.StartsWith("Decoders:", StringComparison.Ordinal) ||
                trimmed.StartsWith("Codecs:", StringComparison.Ordinal) ||
                trimmed.StartsWith("File formats:", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("---", StringComparison.Ordinal)) continue;

            // 标志位段：以空格结束，后接名称
            var flagEnd = trimmed.IndexOf(' ');
            if (flagEnd <= 0 || flagEnd > Flags) continue;

            var flags = trimmed[..flagEnd];
            if (flags[0] is not ('V' or 'A' or 'S' or 'D' or 'E' or 'T')) continue;

            var remainder = trimmed[(flagEnd + 1)..].TrimStart();
            if (remainder.Length == 0) continue;

            var nameEnd = remainder.IndexOf(' ');
            var name = (nameEnd < 0 ? remainder : remainder[..nameEnd]).Trim();
            if (name.Length == 0 || name[0] == '=') continue;
            if (!name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ',' or '@')) continue;

            result.Add(name);
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> ParseHwaccels(string output)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Hardware acceleration", StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')) continue;
            result.Add(line);
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 解析 <c>-filters</c> 输出。表格行形如：
    /// <c> TS aap   AA->A   Apply Affine Projection…</c>
    /// 前 3 个字符是标志位，随后是滤镜名，再往后是 <c>输入->输出</c> 签名。
    /// 用签名（第 3 列）来定位滤镜名，比按列宽截取更耐受格式变化。
    /// </summary>
    private static List<string> ParseFilters(string output)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Filters:", StringComparison.Ordinal)) continue;
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var name = parts[1];
            if (!name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.')) continue;

            if (IsFilterSignature(parts[2]) || parts.Skip(2).Take(2).Any(IsFilterSignature))
            {
                result.Add(name);
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>判断一个 token 是否是滤镜的「输入->输出」签名</summary>
    private static bool IsFilterSignature(string token)
    {
        var arrow = token.IndexOf("->", StringComparison.Ordinal);
        if (arrow <= 0 || arrow + 2 >= token.Length) return false;
        return token.Length <= 8;
    }

    // ────────────────────────────────────────────────────────────────
    // 媒体信息
    // ────────────────────────────────────────────────────────────────

    public async Task<MediaInfo?> GetMediaInfoAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        var paths = _locator.Resolve();
        var args = new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            filePath
        };

        var result = await RunCaptureAsync(paths.FFprobe, args).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut)) return null;

        return ParseMediaInfo(filePath, result.StdOut);
    }

    /// <summary>解析 ffprobe 的 JSON 输出（结构化解析，不再依赖正则）</summary>
    internal static MediaInfo? ParseMediaInfo(string filePath, string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;

            var streams = new List<MediaStreamInfo>();
            if (root.TryGetProperty("streams", out var streamArray) && streamArray.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var s in streamArray.EnumerateArray())
                {
                    var codecType = GetString(s, "codec_type") ?? "unknown";
                    var stream = new MediaStreamInfo(
                        Index: GetInt(s, "index"),
                        CodecType: codecType,
                        CodecName: GetString(s, "codec_name"),
                        CodecLongName: GetString(s, "codec_long_name"),
                        Width: GetInt(s, "width"),
                        Height: GetInt(s, "height"),
                        FrameRate: ParseFraction(GetString(s, "r_frame_rate")),
                        Duration: GetDouble(s, "duration"),
                        Channels: GetInt(s, "channels"),
                        SampleRate: GetInt(s, "sample_rate"),
                        BitRate: GetLong(s, "bit_rate"),
                        Rotation: GetRotation(s),
                        PixelFormat: GetString(s, "pix_fmt"));
                    streams.Add(stream);
                }
            }

            var video = streams.FirstOrDefault(s => s.CodecType == "video");
            var audio = streams.FirstOrDefault(s => s.CodecType == "audio");
            var subtitleCount = streams.Count(s => s.CodecType == "subtitle");

            // 时长：优先 format.duration，回退到视频流时长
            double duration = 0;
            long bitRate = 0;
            string formatName = "unknown";
            if (root.TryGetProperty("format", out var format) && format.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                duration = GetDouble(format, "duration");
                bitRate = GetLong(format, "bit_rate");
                formatName = GetString(format, "format_name") ?? "unknown";
            }
            if (duration <= 0) duration = video?.Duration ?? audio?.Duration ?? 0;

            long fileSize = 0;
            try { fileSize = new FileInfo(filePath).Length; } catch { }

            return new MediaInfo(
                filePath,
                duration,
                formatName,
                video?.CodecName,
                audio?.CodecName,
                video?.Width ?? 0,
                video?.Height ?? 0,
                video?.FrameRate ?? 0,
                fileSize)
            {
                FormatDisplay = FormatDisplayName(formatName),
                BitRate = bitRate,
                AudioChannels = audio?.Channels ?? 0,
                AudioSampleRate = audio?.SampleRate ?? 0,
                Rotation = video?.Rotation ?? 0,
                PixelFormat = video?.PixelFormat,
                HasVideo = video != null,
                HasAudio = audio != null,
                SubtitleStreamCount = subtitleCount,
                Streams = streams
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string FormatDisplayName(string formatName)
    {
        var first = formatName.Split(',')[0].Trim();
        return first.ToLowerInvariant() switch
        {
            "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2" => "MP4 / MOV",
            "matroska" or "webm" => formatName.Contains("webm", StringComparison.OrdinalIgnoreCase) ? "WebM" : "Matroska (MKV)",
            "avi" => "AVI",
            "flv" => "FLV",
            "asf" => "ASF / WMV",
            "mpeg" => "MPEG",
            "mpegts" => "MPEG-TS",
            "ogg" => "OGG",
            "wav" => "WAV",
            "flac" => "FLAC",
            "mp3" => "MP3",
            "aac" => "AAC",
            "image2" => "图片序列",
            "gif" => "GIF",
            _ => first.ToUpperInvariant()
        };
    }

    private static string? GetString(System.Text.Json.JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString(),
            System.Text.Json.JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static int GetInt(System.Text.Json.JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => value.TryGetInt32(out var i) ? i : 0,
            System.Text.Json.JsonValueKind.String => int.TryParse(value.GetString(), out var s) ? s : 0,
            _ => 0
        };
    }

    private static long GetLong(System.Text.Json.JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => value.TryGetInt64(out var l) ? l : 0,
            System.Text.Json.JsonValueKind.String => long.TryParse(value.GetString(), out var s) ? s : 0,
            _ => 0
        };
    }

    private static double GetDouble(System.Text.Json.JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => value.TryGetDouble(out var d) ? d : 0,
            System.Text.Json.JsonValueKind.String => double.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0
        };
    }

    /// <summary>解析 "25/1" 形式的分式帧率</summary>
    internal static double ParseFraction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var den) &&
            den != 0)
        {
            return num / den;
        }
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var single)
            ? single
            : 0;
    }

    private static int GetRotation(System.Text.Json.JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var sideData) ||
            sideData.ValueKind != System.Text.Json.JsonValueKind.Array) return 0;

        foreach (var item in sideData.EnumerateArray())
        {
            if (item.TryGetProperty("rotation", out var rotation))
            {
                var value = rotation.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number => rotation.TryGetInt32(out var i) ? i : 0,
                    System.Text.Json.JsonValueKind.String => int.TryParse(rotation.GetString(), out var s) ? s : 0,
                    _ => 0
                };
                return ((value % 360) + 360) % 360;
            }
        }
        return 0;
    }

    // ────────────────────────────────────────────────────────────────
    // 命令构建
    // ────────────────────────────────────────────────────────────────

    public BuiltCommand BuildCommand(CommandTemplate template, CommandContext context)
    {
        return CommandLineBuilder.Build(template, context);
    }

    // ────────────────────────────────────────────────────────────────
    // 执行
    // ────────────────────────────────────────────────────────────────

    public async Task<FFmpegRunResult> RunAsync(FFmpegRunRequest request, CancellationToken cancellationToken = default)
    {
        var paths = _locator.Resolve();
        var settings = (await _settingsService.GetSettingsAsync().ConfigureAwait(false)).Normalize();

        var args = new List<string> { "-hide_banner" };
        // 明确指定覆盖策略，避免 FFmpeg 在「文件已存在」时读取 stdin 造成永久挂起
        args.Add(request.Overwrite ? "-y" : "-n");
        // 让进度以稳定的机器可读格式输出到 stderr
        args.Add("-progress");
        args.Add("pipe:2");
        args.AddRange(request.Arguments);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = paths.FFmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        var commandLine = paths.FFmpeg + " " + CommandLineBuilder.RenderCommandLine(args);

        var handle = new ProcessHandle { Process = process, CommandLine = commandLine };
        lock (_gate) _running[request.TaskId] = new RunningProcess { Handle = handle, TaskId = request.TaskId };

        var stderrTail = new BoundedLineBuffer(200);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = false;
        var timedOut = false;
        var skippedExisting = false;

        // 进度聚合：ffmpeg 会连续输出多行 key=value，直到 progress=continue/end
        var progressState = new ProgressAccumulator();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            lock (_gate) _running.Remove(request.TaskId);
            process.Dispose();
            return new FFmpegRunResult
            {
                ExitCode = -1,
                Success = false,
                Diagnostic = $"无法启动 FFmpeg（{paths.FFmpeg}）：{ex.Message}",
                Duration = stopwatch.Elapsed
            };
        }

        using var registration = cancellationToken.Register(() =>
        {
            cancelled = true;
            handle.Cancelled = true;
            TryGracefulStop(process);
        });

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                    if (line == null) break;
                    OnStderrLine(line);
                }
            }
            catch
            {
                // 进程被终止时读取会抛异常，属于预期情况
            }
        }, CancellationToken.None);

        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Contains("already exists. Overwrite?", StringComparison.OrdinalIgnoreCase))
                    {
                        skippedExisting = true;
                    }
                }
            }
            catch
            {
                // 同上
            }
        }, CancellationToken.None);

        // 关闭 stdin 内容写入通道但仍保持管道开启（用于发送 'q'）
        try { await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false); } catch { }

        void OnStderrLine(string line)
        {
            stderrTail.Add(line);

            if (line.Contains("already exists. Overwrite?", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Not overwriting - exiting", StringComparison.OrdinalIgnoreCase))
            {
                skippedExisting = true;
            }

            if (progressState.TryConsume(line, request.TotalDuration, out var progress))
            {
                request.OnProgress?.Invoke(progress);
            }
            else
            {
                request.OnLogLine?.Invoke(line);
            }
        }

        try
        {
            if (request.Timeout.HasValue)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(request.Timeout.Value);
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    timedOut = !cancellationToken.IsCancellationRequested;
                    TryKillProcess(process);
                }
            }
            else
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
        }
        finally
        {
            // 确保读取线程结束，避免句柄泄漏
            try { await Task.WhenAny(Task.WhenAll(stderrTask, stdoutTask), Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false); } catch { }
            stopwatch.Stop();
            lock (_gate) _running.Remove(request.TaskId);
        }

        var exitCode = -1;
        try { exitCode = process.ExitCode; } catch { }

        var diagnostic = stderrTail.ToString();

        // FFmpeg 在「文件已存在」时若未加 -y/-n 才会阻塞；我们已经显式处理，
        // 这里兼容个别编码器/协议仍会返回错误的情况。
        if (skippedExisting && exitCode != 0)
        {
            diagnostic = "输出文件已存在，且当前策略为「不覆盖」。请启用覆盖或使用自动重命名。" + Environment.NewLine + diagnostic;
        }

        var success = !cancelled && !timedOut && exitCode == 0;

        if (cancelled && exitCode == 0)
        {
            success = false;
        }

        // 失败/取消时清理半成品，避免用户误用损坏文件
        if (!success && request.CleanupPartialOutput && !skippedExisting && !string.IsNullOrEmpty(request.OutputPath))
        {
            TryDeletePartialOutput(request.OutputPath);
        }

        try { process.Dispose(); } catch { }

        return new FFmpegRunResult
        {
            ExitCode = exitCode,
            Success = success,
            Cancelled = cancelled,
            TimedOut = timedOut,
            SkippedExistingFile = skippedExisting,
            Diagnostic = Truncate(diagnostic, 8000),
            Duration = stopwatch.Elapsed
        };
    }

    public async Task<bool> SendInputAsync(ObjectId taskId, string input)
    {
        ProcessHandle? handle;
        lock (_gate)
        {
            handle = _running.TryGetValue(taskId, out var running) ? running.Handle : null;
        }
        if (handle == null) return false;

        try
        {
            if (handle.Process.HasExited) return false;
            await handle.Process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            await handle.Process.StandardInput.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task KillAsync(ObjectId taskId, bool graceful)
    {
        ProcessHandle? handle;
        lock (_gate)
        {
            handle = _running.TryGetValue(taskId, out var running) ? running.Handle : null;
        }

        if (handle == null) return Task.CompletedTask;

        handle.Cancelled = true;
        if (graceful)
        {
            TryGracefulStop(handle.Process);
        }
        else
        {
            TryKillProcess(handle.Process);
        }
        return Task.CompletedTask;
    }

    /// <summary>先尝试发送 'q' 让 FFmpeg 写完文件头再退出，超时后强杀</summary>
    private static void TryGracefulStop(System.Diagnostics.Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.StandardInput.Write("q");
            process.StandardInput.Flush();
        }
        catch
        {
            // 管道可能已关闭
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
            }
            catch
            {
                // 进程已释放
            }
        });
    }

    private static void TryKillProcess(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已退出或无权终止
        }
    }

    private static void TryDeletePartialOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
            {
                var info = new FileInfo(outputPath);
                // 只在文件很小（明显是失败留下的残片）或者启用了覆盖策略时删除
                if (info.Length < 1024 * 1024 * 64)
                {
                    File.Delete(outputPath);
                }
            }
        }
        catch
        {
            // 忽略：清理失败不应影响任务结果
        }
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return "…（已截断前部输出）\n" + text[^max..];
    }

    private static async Task<CaptureResult> RunCaptureAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CaptureResult(-1, string.Empty, ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return new CaptureResult(-1, string.Empty, "执行超时");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        // 部分 ffmpeg 构建把 -formats/-encoders 输出写到 stderr
        if (string.IsNullOrWhiteSpace(stdout) && !string.IsNullOrWhiteSpace(stderr))
        {
            stdout = stderr;
        }

        return new CaptureResult(process.ExitCode, stdout, stderr);
    }

    private readonly record struct CaptureResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>有界行缓冲：只保留最近 N 行，避免长任务日志无限增长</summary>
    public sealed class BoundedLineBuffer(int capacity)
    {
        private readonly Queue<string> _lines = new(capacity);
        private readonly Lock _gate = new();

        public void Add(string line)
        {
            lock (_gate)
            {
                if (_lines.Count >= capacity) _lines.Dequeue();
                _lines.Enqueue(line);
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return string.Join(Environment.NewLine, _lines);
            }
        }
    }

    /// <summary>
    /// 解析 <c>-progress</c> 的 key=value 输出。
    /// FFmpeg 每个统计周期输出一「块」键值对，以 <c>progress=continue|end</c> 结束；
    /// 用块内数据计算百分比，比解析 <c>-stats</c> 的人读文本更稳。
    /// </summary>
    private sealed class ProgressAccumulator
    {
        private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);

        /// <summary>
        /// 尝试消费一行。返回 true 表示这一行是一次完整进度块的收尾，<paramref name="progress"/> 为算好的进度；
        /// 返回 false 表示这是普通日志行。
        /// </summary>
        public bool TryConsume(string line, double totalDuration, out ConversionProgress progress)
        {
            progress = null!;
            if (string.IsNullOrWhiteSpace(line)) return false;

            var equals = line.IndexOf('=');
            if (equals <= 0) return false;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();

            if (!IsKnownKey(key)) return false;

            if (key != "progress")
            {
                _pending[key] = value;
                return false;
            }

            var currentTime = ReadSeconds(_pending);
            var speed = ReadSpeed(_pending);
            var frame = _pending.TryGetValue("frame", out var frameText) ? frameText : string.Empty;
            var bitrate = _pending.TryGetValue("bitrate", out var bitrateText) ? bitrateText : string.Empty;
            _pending.Clear();

            var isEnd = string.Equals(value, "end", StringComparison.OrdinalIgnoreCase);

            // 块收尾时（progress=end）用总时长兜底，避免最后一帧差一点点到不了 100%
            if (isEnd && totalDuration > 0) currentTime = totalDuration;

            var percentage = totalDuration > 0
                ? Math.Clamp(currentTime / totalDuration * 100.0, 0, 100)
                : 0;

            double? eta = null;
            if (speed is > 0.01 && totalDuration > 0)
            {
                var remaining = totalDuration - currentTime;
                if (remaining > 0) eta = remaining / speed.Value;
            }

            var raw = string.Join("  ", new[]
            {
                string.IsNullOrEmpty(frame) ? null : $"frame={frame}",
                string.IsNullOrEmpty(bitrate) ? null : $"bitrate={bitrate}",
                speed.HasValue ? $"speed={speed.Value:0.##}x" : null,
                $"time={TimeSpan.FromSeconds(currentTime):hh\\:mm\\:ss\\.ff}"
            }.Where(s => s != null));

            progress = new ConversionProgress(percentage, currentTime, totalDuration, speed, eta, raw);
            return true;
        }

        private static double ReadSeconds(Dictionary<string, string> values)
        {
            // out_time_us 是微秒，最精确；部分 FFmpeg 版本只给 out_time_ms / out_time
            if (values.TryGetValue("out_time_us", out var micro) &&
                long.TryParse(micro, out var microValue) && microValue > 0)
            {
                return microValue / 1_000_000.0;
            }
            if (values.TryGetValue("out_time_ms", out var milli) &&
                long.TryParse(milli, out var milliValue) && milliValue > 0)
            {
                return milliValue / 1_000_000.0;
            }
            if (values.TryGetValue("out_time", out var text) && TimeSpan.TryParse(text, out var span))
            {
                return span.TotalSeconds;
            }
            return 0;
        }

        private static double? ReadSpeed(Dictionary<string, string> values)
        {
            if (!values.TryGetValue("speed", out var raw)) return null;
            var cleaned = raw.Trim().TrimEnd('x', 'X');
            if (string.Equals(cleaned, "N/A", StringComparison.OrdinalIgnoreCase)) return null;
            return double.TryParse(cleaned,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed) ? speed : null;
        }

        private static bool IsKnownKey(string key) => key is
            "frame" or "fps" or "bitrate" or "total_size" or "out_time_us" or
            "out_time_ms" or "out_time" or "dup_frames" or "drop_frames" or "speed" or "progress";
    }
}

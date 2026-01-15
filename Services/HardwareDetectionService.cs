using System.Diagnostics;
using System.Text.RegularExpressions;
using FFmpegWebUI.Data;
using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>硬件检测服务实现</summary>
public partial class HardwareDetectionService(
    ILiteDbContext db,
    ISettingsService settingsService) : IHardwareDetectionService
{
    private DateTime? _lastDetectionTime;

    public DateTime? LastDetectionTime => _lastDetectionTime;

    public async Task<List<HardwareEncoder>> DetectEncodersAsync(bool forceRefresh = false)
    {
        // 如果不强制刷新，先检查缓存
        if (!forceRefresh)
        {
            var cached = db.Encoders.FindAll().ToList();
            if (cached.Count > 0 && _lastDetectionTime.HasValue &&
                (DateTime.UtcNow - _lastDetectionTime.Value).TotalMinutes < 30)
            {
                return cached;
            }
        }
        
        // 强制刷新时清空缓存
        if (forceRefresh)
        {
            db.Encoders.DeleteAll();
            _lastDetectionTime = null;
        }

        var encoders = new List<HardwareEncoder>();
        var settings = await settingsService.GetSettingsAsync();
        var ffmpegPath = string.IsNullOrEmpty(settings.FFmpegPath) ? "ffmpeg" : settings.FFmpegPath;

        // 获取所有编码器
        var allEncoders = await GetAllEncodersAsync(ffmpegPath);

        // 定义硬件编码器映射
        var hardwareEncoders = new Dictionary<string, (string DisplayName, EncoderType Type)>
        {
            // NVIDIA NVENC
            ["h264_nvenc"] = ("NVIDIA NVENC H.264", EncoderType.Nvenc),
            ["hevc_nvenc"] = ("NVIDIA NVENC H.265/HEVC", EncoderType.Nvenc),
            ["av1_nvenc"] = ("NVIDIA NVENC AV1", EncoderType.Nvenc),

            // Intel QSV
            ["h264_qsv"] = ("Intel QuickSync H.264", EncoderType.Qsv),
            ["hevc_qsv"] = ("Intel QuickSync H.265/HEVC", EncoderType.Qsv),
            ["av1_qsv"] = ("Intel QuickSync AV1", EncoderType.Qsv),

            // AMD AMF
            ["h264_amf"] = ("AMD AMF H.264", EncoderType.Amf),
            ["hevc_amf"] = ("AMD AMF H.265/HEVC", EncoderType.Amf),

            // Apple VideoToolbox
            ["h264_videotoolbox"] = ("Apple VideoToolbox H.264", EncoderType.VideoToolbox),
            ["hevc_videotoolbox"] = ("Apple VideoToolbox H.265/HEVC", EncoderType.VideoToolbox),

            // 软件编码器
            ["libx264"] = ("软件编码 H.264 (x264)", EncoderType.Software),
            ["libx265"] = ("软件编码 H.265 (x265)", EncoderType.Software),
            ["libvpx-vp9"] = ("软件编码 VP9 (libvpx)", EncoderType.Software),
            ["libaom-av1"] = ("软件编码 AV1 (libaom)", EncoderType.Software),
        };

        foreach (var (encoderName, (displayName, type)) in hardwareEncoders)
        {
            var isInList = allEncoders.Contains(encoderName);
            var isAvailable = isInList && (type == EncoderType.Software || await TestEncoderAsync(ffmpegPath, encoderName));
            var unavailableReason = !isInList ? "编码器不在 FFmpeg 编译中"
                : !isAvailable ? "硬件加速不可用或驱动未安装"
                : null;

            var encoder = new HardwareEncoder
            {
                Name = encoderName,
                DisplayName = displayName,
                Type = type,
                IsAvailable = isAvailable,
                SupportedCodecs = GetCodecsForEncoder(encoderName),
                LastCheckedAt = DateTime.UtcNow,
                UnavailableReason = unavailableReason
            };

            encoders.Add(encoder);
        }

        // 更新数据库缓存
        db.Encoders.DeleteAll();
        db.Encoders.InsertBulk(encoders);
        _lastDetectionTime = DateTime.UtcNow;

        return encoders;
    }

    public async Task<bool> IsEncoderAvailableAsync(string encoderName)
    {
        var encoders = await DetectEncodersAsync();
        return encoders.Any(e => e.Name == encoderName && e.IsAvailable);
    }

    public async Task<string> GetRecommendedEncoderAsync(string codec)
    {
        var encoders = await DetectEncodersAsync();
        var settings = await settingsService.GetSettingsAsync();

        // 根据编码格式和设置选择推荐编码器
        var candidates = codec.ToLower() switch
        {
            "h264" or "avc" => new[] { "h264_nvenc", "h264_qsv", "h264_amf", "h264_videotoolbox", "libx264" },
            "h265" or "hevc" => new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_videotoolbox", "libx265" },
            "av1" => new[] { "av1_nvenc", "av1_qsv", "libaom-av1" },
            "vp9" => new[] { "libvpx-vp9" },
            _ => new[] { "libx264" }
        };

        // 如果偏好硬件加速，优先选择硬件编码器
        if (settings.PreferHardwareAcceleration)
        {
            foreach (var candidate in candidates)
            {
                var encoder = encoders.FirstOrDefault(e => e.Name == candidate && e.IsAvailable);
                if (encoder != null && encoder.Type != EncoderType.Software)
                {
                    return encoder.Name;
                }
            }
        }

        // 返回第一个可用的编码器
        foreach (var candidate in candidates)
        {
            if (encoders.Any(e => e.Name == candidate && e.IsAvailable))
            {
                return candidate;
            }
        }

        // 默认返回 libx264
        return "libx264";
    }

    private async Task<HashSet<string>> GetAllEncodersAsync(string ffmpegPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-encoders -hide_banner",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var encoders = new HashSet<string>();
            foreach (Match match in EncoderRegex().Matches(output))
            {
                encoders.Add(match.Groups[1].Value);
            }

            return encoders;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<bool> TestEncoderAsync(string ffmpegPath, string encoderName)
    {
        try
        {
            // 尝试使用编码器编码一帧空视频来测试可用性
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f lavfi -i nullsrc=s=256x256:d=0.1 -c:v {encoderName} -frames:v 1 -f null - -y",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // 异步读取输出以避免死锁
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // 设置超时 (10秒)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return false;
            }

            // 等待输出读取完成
            await Task.WhenAll(stdoutTask, stderrTask);

            var stderr = await stderrTask;
            
            // 严格检查：只有 exit code 为 0 且输出中包含 "frame=" 才算成功
            // 同时检查是否有硬件错误信息
            if (process.ExitCode != 0)
            {
                return false;
            }
            
            // 检查是否有硬件初始化失败的错误信息
            var hasHardwareError = stderr.Contains("Cannot load") ||
                                   stderr.Contains("Failed to") ||
                                   stderr.Contains("No capable devices found") ||
                                   stderr.Contains("Error initializing") ||
                                   stderr.Contains("Device creation failed") ||
                                   stderr.Contains("not available") ||
                                   stderr.Contains("Cannot open") ||
                                   stderr.Contains("hwaccel") && stderr.Contains("failed");
            
            if (hasHardwareError)
            {
                return false;
            }
            
            // 确认至少编码了一帧
            return stderr.Contains("frame=");
        }
        catch
        {
            return false;
        }
    }

    private static List<string> GetCodecsForEncoder(string encoderName)
    {
        return encoderName switch
        {
            var n when n.Contains("h264") || n.Contains("x264") => ["h264", "avc"],
            var n when n.Contains("hevc") || n.Contains("h265") || n.Contains("x265") => ["hevc", "h265"],
            var n when n.Contains("av1") => ["av1"],
            var n when n.Contains("vp9") => ["vp9"],
            _ => []
        };
    }

    // 匹配 FFmpeg 视频编码器行，格式如: " V..... h264_qsv  ..."
    // V 后面跟着 6 个字符（标志位），然后是空格和编码器名称
    [GeneratedRegex(@"^\s*V[\.\w]{5}\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex EncoderRegex();
}

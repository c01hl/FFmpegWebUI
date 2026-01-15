using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FFmpegWebUI.Models;
using LiteDB;

namespace FFmpegWebUI.Services;

/// <summary>FFmpeg 执行服务实现</summary>
public partial class FFmpegService(ISettingsService settingsService) : IFFmpegService
{
    private readonly ConcurrentDictionary<ObjectId, Process> _runningProcesses = new();

    public async Task<FFmpegInfo?> GetFFmpegInfoAsync()
    {
        try
        {
            var settings = await settingsService.GetSettingsAsync();
            var ffmpegPath = string.IsNullOrEmpty(settings.FFmpegPath) ? "ffmpeg" : settings.FFmpegPath;

            using var process = CreateProcess(ffmpegPath, "-version");
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0) return null;

            // 解析版本信息
            var versionMatch = VersionRegex().Match(output);
            var version = versionMatch.Success ? versionMatch.Groups[1].Value : "Unknown";

            // 获取支持的格式和编解码器
            var formats = await GetSupportedFormatsAsync(ffmpegPath);
            var codecs = await GetSupportedCodecsAsync(ffmpegPath);

            return new FFmpegInfo(version, ffmpegPath, formats, codecs);
        }
        catch
        {
            return null;
        }
    }

    public async Task<MediaInfo?> GetMediaInfoAsync(string filePath)
    {
        try
        {
            var settings = await settingsService.GetSettingsAsync();
            var ffprobePath = string.IsNullOrEmpty(settings.FFprobePath) ? "ffprobe" : settings.FFprobePath;

            var args = $"-v error -select_streams v:0 -show_entries format=duration,format_name:stream=codec_name,width,height,r_frame_rate -of json \"{filePath}\"";
            using var process = CreateProcess(ffprobePath, args);
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0) return null;

            // 简化解析 - 使用正则提取关键信息
            var durationMatch = DurationRegex().Match(output);
            var duration = durationMatch.Success ? double.Parse(durationMatch.Groups[1].Value) : 0;

            var formatMatch = FormatRegex().Match(output);
            var format = formatMatch.Success ? formatMatch.Groups[1].Value : "unknown";

            var codecMatch = CodecRegex().Match(output);
            var videoCodec = codecMatch.Success ? codecMatch.Groups[1].Value : null;

            var widthMatch = WidthRegex().Match(output);
            var width = widthMatch.Success ? int.Parse(widthMatch.Groups[1].Value) : 0;

            var heightMatch = HeightRegex().Match(output);
            var height = heightMatch.Success ? int.Parse(heightMatch.Groups[1].Value) : 0;

            var fileInfo = new FileInfo(filePath);

            return new MediaInfo(
                filePath,
                duration,
                format,
                videoCodec,
                null, // audioCodec
                width,
                height,
                0, // frameRate
                fileInfo.Length
            );
        }
        catch
        {
            return null;
        }
    }

    public async Task ExecuteConversionAsync(
        ConversionTask task,
        Action<ConversionProgress> onProgress,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetSettingsAsync();
        var ffmpegPath = string.IsNullOrEmpty(settings.FFmpegPath) ? "ffmpeg" : settings.FFmpegPath;

        using var process = CreateProcess(ffmpegPath, task.ActualCommand);
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var progress = ParseProgress(e.Data, task.TotalDuration);
            if (progress != null)
            {
                onProgress(progress);
            }
        };

        _runningProcesses[task.Id] = process;

        try
        {
            process.Start();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode}");
            }
        }
        finally
        {
            _runningProcesses.TryRemove(task.Id, out _);
        }
    }

    public Task CancelTaskAsync(ObjectId taskId)
    {
        if (_runningProcesses.TryRemove(taskId, out var process))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略进程已结束的异常
            }
        }
        return Task.CompletedTask;
    }

    public string BuildCommand(
        CommandTemplate template,
        string inputPath,
        string outputPath,
        Dictionary<string, string>? parameters = null)
    {
        var command = template.CommandArgs
            .Replace("{input}", inputPath)
            .Replace("{output}", outputPath);

        if (parameters != null)
        {
            foreach (var (key, value) in parameters)
            {
                command = command.Replace($"{{{key}}}", value);
            }
        }

        // 设置默认编码器
        command = command.Replace("{encoder}", "libx264");
        command = command.Replace("{audio_encoder}", "aac");
        command = command.Replace("{crf}", "23");

        return command;
    }

    private static Process CreateProcess(string fileName, string arguments)
    {
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
    }

    private static ConversionProgress? ParseProgress(string line, double totalDuration)
    {
        // 解析 FFmpeg 进度输出: time=00:01:23.45
        var timeMatch = TimeRegex().Match(line);
        if (!timeMatch.Success) return null;

        var hours = int.Parse(timeMatch.Groups[1].Value);
        var minutes = int.Parse(timeMatch.Groups[2].Value);
        var seconds = double.Parse(timeMatch.Groups[3].Value);
        var currentTime = hours * 3600 + minutes * 60 + seconds;

        var percentage = totalDuration > 0 ? (currentTime / totalDuration) * 100 : 0;

        // 解析速度
        double? speed = null;
        var speedMatch = SpeedRegex().Match(line);
        if (speedMatch.Success)
        {
            speed = double.Parse(speedMatch.Groups[1].Value);
        }

        // 计算预估剩余时间
        double? eta = null;
        if (speed.HasValue && speed.Value > 0 && totalDuration > 0)
        {
            var remaining = totalDuration - currentTime;
            eta = remaining / speed.Value;
        }

        return new ConversionProgress(
            Math.Min(percentage, 100),
            currentTime,
            totalDuration,
            speed,
            eta,
            line
        );
    }

    private async Task<List<string>> GetSupportedFormatsAsync(string ffmpegPath)
    {
        try
        {
            using var process = CreateProcess(ffmpegPath, "-formats -hide_banner");
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return FormatLineRegex().Matches(output)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<string>> GetSupportedCodecsAsync(string ffmpegPath)
    {
        try
        {
            using var process = CreateProcess(ffmpegPath, "-codecs -hide_banner");
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return CodecLineRegex().Matches(output)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    [GeneratedRegex(@"ffmpeg version (\S+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"""duration"":\s*""?([\d.]+)""?")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"""format_name"":\s*""([^""]+)""")]
    private static partial Regex FormatRegex();

    [GeneratedRegex(@"""codec_name"":\s*""([^""]+)""")]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"""width"":\s*(\d+)")]
    private static partial Regex WidthRegex();

    [GeneratedRegex(@"""height"":\s*(\d+)")]
    private static partial Regex HeightRegex();

    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"speed=\s*([\d.]+)x")]
    private static partial Regex SpeedRegex();

    [GeneratedRegex(@"^\s*[DE.]+\s+(\w+)")]
    private static partial Regex FormatLineRegex();

    [GeneratedRegex(@"^\s*[DEVASIL.]+\s+(\w+)")]
    private static partial Regex CodecLineRegex();
}

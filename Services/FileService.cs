using System.Text.RegularExpressions;

namespace FFmpegWebUI.Services;

/// <summary>文件服务实现</summary>
public sealed partial class FileService : IFileService
{
    private static readonly HashSet<string> DefaultMediaExtensions =
    [
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg", ".3gp", ".ogv",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".aiff",
        ".gif", ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".webp"
    ];

    public IReadOnlyCollection<string> SupportedMediaExtensions => DefaultMediaExtensions;

    public bool IsFileAccessible(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var expanded = AppPaths.ToAbsolute(path);
            return File.Exists(expanded) && new FileInfo(expanded).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsDirectoryWritable(string path) => OutputPathResolver.IsOutputPathWritable(Path.Combine(path, "probe.tmp"));

    public long GetAvailableDiskSpace(string path)
    {
        try
        {
            var target = AppPaths.ToAbsolute(path);
            // DriveInfo 在部分平台上需要已存在的路径，尽量向上找到一个存在的目录
            while (!string.IsNullOrEmpty(target) && !Directory.Exists(target))
            {
                var parent = Path.GetDirectoryName(target);
                if (string.IsNullOrEmpty(parent) || parent == target) break;
                target = parent;
            }

            var root = Path.GetPathRoot(target);
            if (string.IsNullOrEmpty(root)) return 0;

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    public string GenerateOutputPath(string inputPath, string outputDirectory, string extension, string? suffix = null)
    {
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var ext = NormalizeExtension(extension);
        var outputFileName = string.IsNullOrEmpty(suffix)
            ? $"{fileName}.{ext}"
            : $"{fileName}{suffix}.{ext}";
        return Path.Combine(outputDirectory, outputFileName);
    }

    public string GenerateUniqueOutputPath(string inputPath, string outputDirectory, string extension, string? suffix = null)
    {
        var candidate = GenerateOutputPath(inputPath, outputDirectory, extension, suffix);
        if (!File.Exists(candidate)) return candidate;

        var directory = Path.GetDirectoryName(candidate) ?? outputDirectory;
        var name = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);

        for (var i = 1; i < 10000; i++)
        {
            var next = Path.Combine(directory, $"{name} ({i}){ext}");
            if (!File.Exists(next)) return next;
        }

        return Path.Combine(directory, $"{name}_{DateTime.Now:yyyyMMddHHmmss}{ext}");
    }

    public string FormatFileName(string template, string inputPath, string extension)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return Path.GetFileNameWithoutExtension(inputPath);
        }

        var now = DateTime.Now;
        var baseFileName = Path.GetFileNameWithoutExtension(inputPath);
        var inputExt = Path.GetExtension(inputPath).TrimStart('.');
        var inputDir = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var inputDirName = string.IsNullOrEmpty(inputDir) ? string.Empty : new DirectoryInfo(inputDir).Name;
        var outputExt = NormalizeExtension(extension);

        var result = template;
        result = result.Replace("{filename}", baseFileName, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{ext}", inputExt, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{outputext}", outputExt, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{dir}", inputDirName, StringComparison.OrdinalIgnoreCase);

        result = result.Replace("{datetime}", now.ToString("yyyyMMdd_HHmmss"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{date}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{time}", now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{year}", now.ToString("yyyy"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{month}", now.ToString("MM"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{day}", now.ToString("dd"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{hour}", now.ToString("HH"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{minute}", now.ToString("mm"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{second}", now.ToString("ss"), StringComparison.OrdinalIgnoreCase);

        result = NowFormatRegex().Replace(result, m =>
        {
            try
            {
                return now.ToString(m.Groups[1].Value);
            }
            catch
            {
                return m.Value;
            }
        });

        result = RandomLengthRegex().Replace(result, m =>
        {
            if (!int.TryParse(m.Groups[1].Value, out var length)) return Guid.NewGuid().ToString("N")[..8];
            length = Math.Clamp(length, 1, 32);
            return Guid.NewGuid().ToString("N")[..length];
        });
        result = result.Replace("{random}", Guid.NewGuid().ToString("N")[..8], StringComparison.OrdinalIgnoreCase);

        result = CounterRegex().Replace(result, m =>
        {
            if (!int.TryParse(m.Groups[1].Value, out var digits)) return "0";
            digits = Math.Clamp(digits, 1, 10);
            var counter = now.Ticks % (long)Math.Pow(10, digits);
            return counter.ToString().PadLeft(digits, '0');
        });

        return SanitizeFileName(result);
    }

    /// <summary>去掉文件系统不允许的字符，并处理 Windows 保留名</summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        var result = builder.ToString().Trim().TrimEnd('.', ' ');

        // Windows 保留设备名
        if (AppPaths.IsWindows)
        {
            var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
                "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (reserved.Contains(result, StringComparer.OrdinalIgnoreCase)) result = "_" + result;
        }

        return string.IsNullOrEmpty(result) ? "output" : result;
    }

    public List<string> ScanMediaFiles(string directory, List<string>? extensions = null, bool recursive = false)
    {
        var targetExtensions = extensions is { Count: > 0 }
            ? extensions.Select(e => e.StartsWith('.') ? e : $".{e}").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : DefaultMediaExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 未指定扩展名时按「所有媒体格式」处理
        if (extensions is { Count: 0 }) targetExtensions = DefaultMediaExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(directory)) return [];

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateFiles(directory, "*.*", searchOption)
                .Where(f => targetExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public long GetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void EnsureDirectoryExists(string path)
    {
        try
        {
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch
        {
            // 目录创建失败时交由后续的可写性检查给出明确提示
        }
    }

    public string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] sizes = ["B", "KB", "MB", "GB", "TB", "PB"];
        double length = bytes;
        var order = 0;
        while (length >= 1024 && order < sizes.Length - 1)
        {
            order++;
            length /= 1024;
        }
        return $"{length:0.##} {sizes[order]}";
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "mp4";
        return extension.Trim().TrimStart('.');
    }

    [GeneratedRegex(@"\{now:([^}]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex NowFormatRegex();

    [GeneratedRegex(@"\{random:(\d+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex RandomLengthRegex();

    [GeneratedRegex(@"\{counter:(\d+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex CounterRegex();
}

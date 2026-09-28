using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>输出路径解析结果</summary>
public sealed record OutputPathResolution
{
    /// <summary>最终使用的输出路径</summary>
    public required string OutputPath { get; init; }

    /// <summary>是否需要覆盖已有文件（决定 FFmpeg 加 -y 还是 -n）</summary>
    public required bool Overwrite { get; init; }

    /// <summary>是否应当跳过本次转换（输出已存在且策略为跳过）</summary>
    public required bool Skip { get; init; }

    /// <summary>相对用户请求的路径是否发生了自动重命名</summary>
    public required bool Renamed { get; init; }

    /// <summary>给用户看的说明</summary>
    public string? Note { get; init; }
}

/// <summary>输出路径与冲突策略解析</summary>
public static class OutputPathResolver
{
    /// <summary>同一目录下自动重命名时最多尝试的序号</summary>
    private const int MaxRenameAttempts = 9999;

    /// <summary>
    /// 依据「文件已存在时的处理方式」设置，把请求的输出路径解析成实际可用的路径。
    /// 关键是：任何分支都不会让 FFmpeg 去询问用户，从根本上消除卡死。
    /// </summary>
    /// <param name="requestedPath">期望的输出路径</param>
    /// <param name="action">文件已存在时的处理方式</param>
    /// <param name="inputPath">
    /// 输入文件路径。传入后会额外检查「输出就是输入」的情况：
    /// 这时 FFmpeg 会一边读一边写同一个文件，结果必然是损坏的源文件，
    /// 因此无论策略如何都强制改名。
    /// </param>
    public static OutputPathResolution Resolve(string requestedPath, FileExistsAction action, string? inputPath = null)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return new OutputPathResolution
            {
                OutputPath = requestedPath,
                Overwrite = true,
                Skip = false,
                Renamed = false
            };
        }

        var absolute = AppPaths.ToAbsolute(requestedPath);

        // 输出 == 输入：必须改名，否则会破坏源文件
        if (!string.IsNullOrWhiteSpace(inputPath) && PathsEqual(absolute, inputPath))
        {
            var renamedFromInput = Rename(absolute);
            return renamedFromInput with
            {
                Note = "输出路径与输入文件相同，已自动改名以避免覆盖源文件。"
            };
        }

        var exists = SafeExists(absolute);

        if (!exists)
        {
            return new OutputPathResolution
            {
                OutputPath = absolute,
                Overwrite = true,
                Skip = false,
                Renamed = false
            };
        }

        switch (action)
        {
            case FileExistsAction.Skip:
                return new OutputPathResolution
                {
                    OutputPath = absolute,
                    Overwrite = false,
                    Skip = true,
                    Renamed = false,
                    Note = "输出文件已存在，按设置跳过本次转换。"
                };

            case FileExistsAction.Overwrite:
                return new OutputPathResolution
                {
                    OutputPath = absolute,
                    Overwrite = true,
                    Skip = false,
                    Renamed = false,
                    Note = "输出文件已存在，将按设置覆盖。"
                };

            case FileExistsAction.Rename:
                return Rename(absolute);

            // Ask：调用方已经确认过；未确认的批量场景按最安全的「重命名」处理
            default:
                var renamed = Rename(absolute);
                return renamed with
                {
                    Note = "输出文件已存在，已自动重命名以避免覆盖原文件。"
                };
        }
    }

    /// <summary>生成不冲突的路径：name.ext → name (1).ext → name (2).ext …</summary>
    public static OutputPathResolution Rename(string absolutePath)
    {
        var directory = Path.GetDirectoryName(absolutePath);
        var name = Path.GetFileNameWithoutExtension(absolutePath);
        var extension = Path.GetExtension(absolutePath);

        if (string.IsNullOrEmpty(directory)) return new OutputPathResolution
        {
            OutputPath = absolutePath,
            Overwrite = true,
            Skip = false,
            Renamed = false
        };

        for (var i = 1; i <= MaxRenameAttempts; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!SafeExists(candidate))
            {
                return new OutputPathResolution
                {
                    OutputPath = candidate,
                    Overwrite = true,
                    Skip = false,
                    Renamed = true,
                    Note = $"目标文件已存在，已自动重命名为 {Path.GetFileName(candidate)}。"
                };
            }
        }

        // 极端情况下退化到时间戳，保证一定能拿到一个可用路径
        var stamped = Path.Combine(directory, $"{name}_{DateTime.Now:yyyyMMddHHmmss}{extension}");
        return new OutputPathResolution
        {
            OutputPath = stamped,
            Overwrite = true,
            Skip = false,
            Renamed = true,
            Note = $"目标文件已存在，已自动重命名为 {Path.GetFileName(stamped)}。"
        };
    }

    /// <summary>检查所给路径是否可写（输出目录可创建、可写入）</summary>
    public static bool IsOutputPathWritable(string outputPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(AppPaths.ToAbsolute(outputPath));
            if (string.IsNullOrEmpty(directory)) return false;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, $".ffmpegwebui-write-test-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断两个路径是否指向同一个文件。
    /// Windows 与 macOS 的默认文件系统大小写不敏感，Linux 通常敏感。
    /// </summary>
    public static bool PathsEqual(string left, string right)
    {
        try
        {
            var comparison = AppPaths.IsLinux
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                comparison);
        }
        catch
        {
            return false;
        }
    }
}

using FFmpegWebUI.Data;
using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>解析后的 FFmpeg 可执行文件路径</summary>
public record FFmpegPaths(string FFmpeg, string FFprobe, bool IsAutoDetected)
{
    /// <summary>是否成功解析到实际存在的 ffmpeg</summary>
    public bool FFmpegResolved { get; init; }

    /// <summary>是否成功解析到实际存在的 ffprobe</summary>
    public bool FFprobeResolved { get; init; }

    /// <summary>解析说明，便于界面提示用户</summary>
    public string Hint { get; init; } = string.Empty;
}

/// <summary>FFmpeg 可执行文件定位器</summary>
public interface IFFmpegLocator
{
    /// <summary>解析 ffmpeg / ffprobe 路径（带缓存，设置变更后调用 Invalidate）</summary>
    FFmpegPaths Resolve();

    /// <summary>清除缓存</summary>
    void Invalidate();
}

/// <summary>
/// 按「用户配置 → 随应用内置 → 系统 PATH → 平台常见安装位置」的顺序定位 FFmpeg。
/// 内置一份候选清单是为了兼容从图形界面/双击启动、PATH 不完整的场景。
/// </summary>
public sealed class FFmpegLocator(ILiteDbContext db) : IFFmpegLocator
{
    private FFmpegPaths? _cached;
    private readonly Lock _gate = new();

    public FFmpegPaths Resolve()
    {
        lock (_gate)
        {
            if (_cached != null) return _cached;
            _cached = ResolveCore();
            return _cached;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private FFmpegPaths ResolveCore()
    {
        // 直接读数据库而不是注入 ISettingsService：
        // SettingsService 需要依赖 IFFmpegLocator 来在保存后清缓存，
        // 如果这里再反向依赖它就会形成循环依赖（DI 会直接启动失败）。
        // 读数据库拿到的是「已保存」的值，正好也是运行期应该使用的值。
        AppSettings settings;
        try
        {
            settings = (db.Settings.FindAll().FirstOrDefault() ?? new AppSettings()).Normalize();
        }
        catch
        {
            settings = new AppSettings();
        }

        // 1) 用户显式配置
        if (!string.IsNullOrWhiteSpace(settings.FFmpegPath))
        {
            var configured = AppPaths.ToAbsolute(settings.FFmpegPath);
            if (File.Exists(configured) || IsOnPath(configured))
            {
                var probe = string.IsNullOrWhiteSpace(settings.FFprobePath)
                    ? FindSiblingProbe(configured) ?? ProbeCandidates().FirstOrDefault(File.Exists) ?? ProbeName()
                    : AppPaths.ToAbsolute(settings.FFprobePath);
                return new FFmpegPaths(configured, probe, IsAutoDetected: false)
                {
                    FFmpegResolved = true,
                    FFprobeResolved = File.Exists(probe) || IsOnPath(probe),
                    Hint = "使用设置中指定的路径"
                };
            }
        }

        // 2) 显式配置的 ffprobe（ffmpeg 走自动探测）
        string? configuredProbe = null;
        if (!string.IsNullOrWhiteSpace(settings.FFprobePath))
        {
            var probe = AppPaths.ToAbsolute(settings.FFprobePath);
            if (File.Exists(probe)) configuredProbe = probe;
        }

        // 3) 自动探测
        var ffmpeg = FindExecutable("ffmpeg");
        var ffprobe = configuredProbe ?? FindExecutable("ffprobe");

        var resolvedFfmpeg = ffmpeg ?? "ffmpeg";
        var resolvedFfprobe = ffprobe ?? "ffprobe";

        return new FFmpegPaths(resolvedFfmpeg, resolvedFfprobe, IsAutoDetected: true)
        {
            FFmpegResolved = ffmpeg != null,
            FFprobeResolved = ffprobe != null,
            Hint = ffmpeg != null ? "自动探测到 FFmpeg" : "未找到 FFmpeg，请在设置中指定路径"
        };
    }

    /// <summary>在 内置目录 / PATH / 平台常见位置 中搜索可执行文件</summary>
    private static string? FindExecutable(string name)
    {
        // 随应用分发的内置 FFmpeg
        var baseDir = AppContext.BaseDirectory;
        foreach (var relative in BundledCandidates(name))
        {
            var candidate = Path.Combine(baseDir, relative);
            if (File.Exists(candidate)) return candidate;
        }

        // 系统 PATH
        var onPath = FindOnPath(name);
        if (onPath != null) return onPath;

        // 平台常见安装位置
        foreach (var candidate in WellKnownCandidates(name))
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static IEnumerable<string> BundledCandidates(string name)
    {
        var exe = name + AppPaths.ExeSuffix;
        yield return exe;
        yield return Path.Combine("ffmpeg", exe);
        yield return Path.Combine("tools", exe);
        yield return Path.Combine("tools", "ffmpeg", exe);
        yield return Path.Combine("runtimes", "ffmpeg", exe);
    }

    private static IEnumerable<string> WellKnownCandidates(string name)
    {
        var exe = name + AppPaths.ExeSuffix;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>();

        if (AppPaths.IsWindows)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            list.AddRange([
                @"C:\ffmpeg\bin\" + exe,
                @"C:\ffmpeg\" + exe,
                Path.Combine(programFiles, "ffmpeg", "bin", exe),
                Path.Combine(programFilesX86, "ffmpeg", "bin", exe),
                Path.Combine(localAppData, "Microsoft", "WinGet", "Links", exe),
                @"C:\ProgramData\chocolatey\bin\" + exe,
                Path.Combine(localAppData, "Microsoft", "WindowsApps", exe),
                Path.Combine(appData, "ffmpeg", "bin", exe),
            ]);
            if (!string.IsNullOrEmpty(home))
            {
                list.Add(Path.Combine(home, "scoop", "shims", exe));
                list.Add(Path.Combine(home, "scoop", "apps", "ffmpeg", "current", "bin", exe));
            }
        }
        else if (AppPaths.IsMacOS)
        {
            list.AddRange([
                "/opt/homebrew/bin/" + exe,
                "/usr/local/bin/" + exe,
                "/opt/local/bin/" + exe,
                "/usr/bin/" + exe,
                "/nix/var/nix/profiles/default/bin/" + exe,
                "/run/current-system/sw/bin/" + exe,
            ]);
            if (!string.IsNullOrEmpty(home))
            {
                list.Add(Path.Combine(home, ".local", "bin", exe));
                list.Add(Path.Combine(home, "bin", exe));
            }
        }
        else
        {
            // Linux：优先 jellyfin-ffmpeg，它对 QSV/VAAPI 的支持更完整
            list.AddRange([
                "/usr/lib/jellyfin-ffmpeg/" + exe,
                "/usr/local/bin/" + exe,
                "/usr/bin/" + exe,
                "/bin/" + exe,
                "/snap/bin/" + exe,
                "/var/lib/flatpak/exports/bin/" + exe,
                "/nix/var/nix/profiles/default/bin/" + exe,
                "/run/current-system/sw/bin/" + exe,
            ]);
            if (!string.IsNullOrEmpty(home))
            {
                list.Add(Path.Combine(home, ".local", "bin", exe));
                list.Add(Path.Combine(home, "bin", exe));
                list.Add(Path.Combine(home, ".nix-profile", "bin", exe));
            }
        }

        return list;
    }

    private static string? FindOnPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name + AppPaths.ExeSuffix);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 忽略 PATH 中的非法项
            }
        }
        return null;
    }

    /// <summary>判断给定字符串是否是可执行文件名（交由系统 PATH 解析）</summary>
    private static bool IsOnPath(string value) =>
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar);

    /// <summary>在 ffmpeg 同目录中寻找 ffprobe</summary>
    private static string? FindSiblingProbe(string ffmpegPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(ffmpegPath);
            if (string.IsNullOrEmpty(dir)) return null;
            var candidate = Path.Combine(dir, "ffprobe" + AppPaths.ExeSuffix);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ProbeCandidates() =>
        WellKnownCandidates("ffprobe").Concat(BundledCandidates("ffprobe").Select(p => Path.Combine(AppContext.BaseDirectory, p)));

    private static string ProbeName() => "ffprobe";
}

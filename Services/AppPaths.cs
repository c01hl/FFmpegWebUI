using System.Runtime.InteropServices;

namespace FFmpegWebUI.Services;

/// <summary>跨平台路径与平台判定辅助</summary>
public static class AppPaths
{
    /// <summary>当前操作系统</summary>
    public static OSPlatform Platform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX :
        OSPlatform.Linux;

    /// <summary>是否为 Windows</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>是否为 macOS</summary>
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>是否为 Linux</summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>是否运行在容器中（Docker 等）</summary>
    public static bool IsContainer =>
        IsLinux && (File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true");

    /// <summary>可执行文件扩展名</summary>
    public static string ExeSuffix => IsWindows ? ".exe" : string.Empty;

    /// <summary>平台显示名</summary>
    public static string PlatformDisplayName => IsWindows ? "Windows"
        : IsMacOS ? "macOS"
        : IsLinux ? "Linux"
        : "未知系统";

    /// <summary>应用数据目录（跨平台，可被环境变量 FFMPEGWEBUI_DATA_DIR 覆盖）</summary>
    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("FFMPEGWEBUI_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return EnsureDirectory(ExpandHome(overridden));
            }

            string root;
            if (IsWindows)
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            else if (IsMacOS)
            {
                root = Path.Combine(ExpandHome("~"), "Library", "Application Support");
            }
            else
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                root = string.IsNullOrWhiteSpace(xdg)
                    ? Path.Combine(ExpandHome("~"), ".local", "share")
                    : ExpandHome(xdg);
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppContext.BaseDirectory;
            }

            return EnsureDirectory(Path.Combine(root, "FFmpegWebUI"));
        }
    }

    /// <summary>用户视频目录（Linux 下可能为空，回退到用户主目录）</summary>
    public static string DefaultOutputDirectory
    {
        get
        {
            var videos = SafeSpecialFolder(Environment.SpecialFolder.MyVideos);
            if (!string.IsNullOrWhiteSpace(videos) && Directory.Exists(videos)) return videos;

            var home = SafeSpecialFolder(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home)) return home;

            return Path.GetTempPath();
        }
    }

    /// <summary>展开开头的 ~ 为用户主目录</summary>
    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
        {
            var home = SafeSpecialFolder(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                return Path.GetFullPath(Path.Combine(home, path.Length > 2 ? path[2..] : string.Empty));
            }
        }
        return path;
    }

    /// <summary>确保目录存在并返回该目录</summary>
    public static string EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // 目录不可创建时交由调用方在使用处失败，这里不抛出
        }
        return path;
    }

    /// <summary>把路径规整为绝对路径（不要求文件存在）</summary>
    public static string ToAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var expanded = ExpandHome(path.Trim().Trim('"'));
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch
        {
            return expanded;
        }
    }

    private static string SafeSpecialFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

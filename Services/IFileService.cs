namespace FFmpegWebUI.Services;

/// <summary>文件服务接口</summary>
public interface IFileService
{
    /// <summary>验证文件是否存在且可读（长度为 0 的文件视为不可用）</summary>
    bool IsFileAccessible(string path);

    /// <summary>验证目录是否可写（不存在时尝试创建）</summary>
    bool IsDirectoryWritable(string path);

    /// <summary>获取可用磁盘空间</summary>
    long GetAvailableDiskSpace(string path);

    /// <summary>生成输出文件路径</summary>
    string GenerateOutputPath(string inputPath, string outputDirectory, string extension, string? suffix = null);

    /// <summary>生成不与已有文件冲突的输出路径</summary>
    string GenerateUniqueOutputPath(string inputPath, string outputDirectory, string extension, string? suffix = null);

    /// <summary>
    /// 格式化文件名模板，支持 {filename}/{ext}/{outputext}/{dir}/{date}/{now:fmt}/{random:N} 等占位符。
    /// </summary>
    /// <returns>格式化后的文件名（不含扩展名）</returns>
    string FormatFileName(string template, string inputPath, string extension);

    /// <summary>扫描目录中的媒体文件</summary>
    List<string> ScanMediaFiles(string directory, List<string>? extensions = null, bool recursive = false);

    /// <summary>获取文件大小（字节）</summary>
    long GetFileSize(string path);

    /// <summary>确保路径所在目录存在</summary>
    void EnsureDirectoryExists(string path);

    /// <summary>格式化文件大小用于显示，如 "12.3 MB"</summary>
    string FormatFileSize(long bytes);

    /// <summary>可识别的媒体扩展名（含点，小写）</summary>
    IReadOnlyCollection<string> SupportedMediaExtensions { get; }
}

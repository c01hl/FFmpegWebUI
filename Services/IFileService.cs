namespace FFmpegWebUI.Services;

/// <summary>文件服务接口</summary>
public interface IFileService
{
    /// <summary>验证文件是否存在且可读</summary>
    bool IsFileAccessible(string path);

    /// <summary>验证目录是否可写</summary>
    bool IsDirectoryWritable(string path);

    /// <summary>获取可用磁盘空间</summary>
    long GetAvailableDiskSpace(string path);

    /// <summary>生成输出文件路径</summary>
    string GenerateOutputPath(string inputPath, string outputDirectory, string extension, string? suffix = null);

    /// <summary>格式化文件名模板</summary>
    /// <param name="template">文件名模板，支持占位符</param>
    /// <param name="inputPath">输入文件路径</param>
    /// <param name="extension">输出扩展名</param>
    /// <returns>格式化后的文件名（不含扩展名）</returns>
    string FormatFileName(string template, string inputPath, string extension);

    /// <summary>扫描目录中的媒体文件</summary>
    /// <param name="directory">要扫描的目录</param>
    /// <param name="extensions">允许的扩展名列表</param>
    /// <param name="recursive">是否递归搜索子目录</param>
    List<string> ScanMediaFiles(string directory, List<string>? extensions = null, bool recursive = false);

    /// <summary>获取文件大小</summary>
    long GetFileSize(string path);

    /// <summary>确保目录存在</summary>
    void EnsureDirectoryExists(string path);

    /// <summary>打开文件选择对话框</summary>
    Task<string?> OpenFileDialogAsync(string title = "选择文件", string? filter = null, string? initialDirectory = null);

    /// <summary>打开文件夹选择对话框</summary>
    Task<string?> OpenFolderDialogAsync(string title = "选择文件夹", string? initialDirectory = null);
}

using System.Diagnostics;
using System.Text;

namespace FFmpegWebUI.Services;

/// <summary>文件服务实现</summary>
public class FileService : IFileService
{
    private static readonly HashSet<string> DefaultMediaExtensions =
    [
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a"
    ];

    public bool IsFileAccessible(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsDirectoryWritable(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var testFile = Path.Combine(path, $".write_test_{Guid.NewGuid()}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public long GetAvailableDiskSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return 0;

            var driveInfo = new DriveInfo(root);
            return driveInfo.AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    public string GenerateOutputPath(string inputPath, string outputDirectory, string extension, string? suffix = null)
    {
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var outputFileName = string.IsNullOrEmpty(suffix)
            ? $"{fileName}.{extension.TrimStart('.')}"
            : $"{fileName}{suffix}.{extension.TrimStart('.')}";

        return Path.Combine(outputDirectory, outputFileName);
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
        var inputDir = Path.GetDirectoryName(inputPath) ?? "";
        var inputDirName = new DirectoryInfo(inputDir).Name;

        var result = template;

        // 基本占位符替换
        result = result.Replace("{filename}", baseFileName, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{ext}", inputExt, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{outputext}", extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{dir}", inputDirName, StringComparison.OrdinalIgnoreCase);
        
        // 日期时间占位符（简单格式）
        result = result.Replace("{date}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{time}", now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{datetime}", now.ToString("yyyyMMdd_HHmmss"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{year}", now.ToString("yyyy"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{month}", now.ToString("MM"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{day}", now.ToString("dd"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{hour}", now.ToString("HH"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{minute}", now.ToString("mm"), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{second}", now.ToString("ss"), StringComparison.OrdinalIgnoreCase);

        // 支持自定义日期格式 {now:format}
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"\{now:([^}]+)\}",
            m => {
                try
                {
                    return now.ToString(m.Groups[1].Value);
                }
                catch
                {
                    return m.Value; // 格式无效时保留原文
                }
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 随机字符串 {random:N} 生成N位随机字符
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"\{random:(\d+)\}",
            m => {
                var length = int.Parse(m.Groups[1].Value);
                length = Math.Min(length, 32); // 最多32位
                return Guid.NewGuid().ToString("N")[..length];
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 简单的 {random} 生成8位随机字符
        result = result.Replace("{random}", Guid.NewGuid().ToString("N")[..8], StringComparison.OrdinalIgnoreCase);

        // 计数器占位符 {counter:N} 用零填充的数字（需要外部维护计数器，这里用时间戳后几位代替）
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"\{counter:(\d+)\}",
            m => {
                var digits = int.Parse(m.Groups[1].Value);
                digits = Math.Min(digits, 10);
                var counter = now.Ticks % (long)Math.Pow(10, digits);
                return counter.ToString().PadLeft(digits, '0');
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 清理非法文件名字符
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(c, '_');
        }

        return result;
    }

    public List<string> ScanMediaFiles(string directory, List<string>? extensions = null, bool recursive = false)
    {
        var targetExtensions = extensions?.Select(e => e.StartsWith('.') ? e : $".{e}").ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? DefaultMediaExtensions;

        try
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.GetFiles(directory, "*.*", searchOption)
                .Where(f => targetExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public long GetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    public void EnsureDirectoryExists(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<string?> OpenFileDialogAsync(string title = "选择文件", string? filter = null, string? initialDirectory = null)
    {
        // 使用 PowerShell 打开 Windows 文件选择对话框
        var filterString = filter ?? "媒体文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.mp3;*.wav;*.flac;*.aac;*.ogg|所有文件|*.*";
        var initialDir = initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        
        var script = $@"
Add-Type -AssemblyName System.Windows.Forms
$dialog = New-Object System.Windows.Forms.OpenFileDialog
$dialog.Title = '{title.Replace("'", "''")}'
$dialog.Filter = '{filterString.Replace("'", "''")}'
$dialog.InitialDirectory = '{initialDir.Replace("'", "''")}'
$dialog.CheckFileExists = $true
$dialog.CheckPathExists = $true
if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{
    Write-Output $dialog.FileName
}}
";
        
        return await RunPowerShellDialogAsync(script);
    }

    public async Task<string?> OpenFolderDialogAsync(string title = "选择文件夹", string? initialDirectory = null)
    {
        // 使用 PowerShell 打开 Windows 文件夹选择对话框
        var initialDir = initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        
        var script = $@"
Add-Type -AssemblyName System.Windows.Forms
$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
$dialog.Description = '{title.Replace("'", "''")}'
$dialog.SelectedPath = '{initialDir.Replace("'", "''")}'
$dialog.ShowNewFolderButton = $true
if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{
    Write-Output $dialog.SelectedPath
}}
";
        
        return await RunPowerShellDialogAsync(script);
    }

    private static async Task<string?> RunPowerShellDialogAsync(string script)
    {
        try
        {
            // 在脚本开头设置 UTF-8 编码
            var fullScript = $@"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
{script}
";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -Command \"{fullScript.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.Start();
            
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            var result = output.Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch
        {
            return null;
        }
    }
}

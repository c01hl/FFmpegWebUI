using System.Diagnostics;
using System.Text;

namespace FFmpegWebUI.Services;

/// <summary>本机文件/文件夹选择对话框</summary>
public interface IFileDialogService
{
    /// <summary>是否支持弹出原生对话框</summary>
    bool IsSupported { get; }

    /// <summary>当前平台使用的对话框实现说明（用于界面提示）</summary>
    string BackendDescription { get; }

    /// <summary>打开文件选择对话框；失败返回 null</summary>
    Task<string?> PickFileAsync(string title = "选择文件", string? filter = null, string? initialDirectory = null);

    /// <summary>打开文件夹选择对话框；失败返回 null</summary>
    Task<string?> PickFolderAsync(string title = "选择文件夹", string? initialDirectory = null);
}

/// <summary>
/// 跨平台文件选择对话框：
/// macOS 使用 osascript，Linux 使用 zenity / kdialog，Windows 使用 WinForms（PowerShell）。
/// 任何平台都失败时返回 null，由界面提示用户手工粘贴路径。
/// </summary>
public sealed class FileDialogService : IFileDialogService
{
    /// <summary>对话框最长等待时间，避免某些环境把界面卡死</summary>
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromMinutes(5);

    private bool? _supported;
    private string? _backend;
    private readonly Lock _gate = new();

    /// <summary>
    /// 是否有可用的原生对话框后端。
    /// Windows 依赖 PowerShell / macOS 依赖 osascript（两者都属于系统自带），
    /// Linux 则需要 zenity 或 kdialog 之一。
    /// </summary>
    public bool IsSupported
    {
        get
        {
            lock (_gate)
            {
                if (_supported.HasValue) return _supported.Value;

                if (AppPaths.IsWindows || AppPaths.IsMacOS)
                {
                    _supported = true;
                }
                else
                {
                    _supported = CommandExists("zenity") || CommandExists("kdialog");
                }

                return _supported.Value;
            }
        }
    }

    public string BackendDescription
    {
        get
        {
            lock (_gate)
            {
                if (_backend != null) return _backend;

                if (AppPaths.IsWindows) _backend = "Windows 文件对话框（PowerShell）";
                else if (AppPaths.IsMacOS) _backend = "macOS 原生选择器（osascript）";
                else if (CommandExists("zenity")) _backend = "zenity";
                else if (CommandExists("kdialog")) _backend = "kdialog";
                else _backend = "不可用（未安装 zenity 或 kdialog）";

                return _backend;
            }
        }
    }

    public async Task<string?> PickFileAsync(string title = "选择文件", string? filter = null, string? initialDirectory = null)
    {
        if (!IsSupported) return null;

        var start = ResolveStartDirectory(initialDirectory);

        if (AppPaths.IsWindows)
        {
            return await RunPowerShellAsync(BuildWindowsFileScript(title, start), title).ConfigureAwait(false);
        }

        if (AppPaths.IsMacOS)
        {
            return await RunMacOsAsync(BuildMacChooseScript(title, start, pickFolder: false), title).ConfigureAwait(false);
        }

        return await RunLinuxFilePickerAsync(title, start).ConfigureAwait(false);
    }

    public async Task<string?> PickFolderAsync(string title = "选择文件夹", string? initialDirectory = null)
    {
        if (!IsSupported) return null;

        var start = ResolveStartDirectory(initialDirectory);

        if (AppPaths.IsWindows)
        {
            return await RunPowerShellAsync(BuildWindowsFolderScript(title, start), title).ConfigureAwait(false);
        }

        if (AppPaths.IsMacOS)
        {
            return await RunMacOsAsync(BuildMacChooseScript(title, start, pickFolder: true), title).ConfigureAwait(false);
        }

        return await RunLinuxFolderPickerAsync(title, start).ConfigureAwait(false);
    }

    // ────────────────────────────────────────────────────────────────
    // macOS（osascript）
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 生成 macOS 选择器脚本。
    ///
    /// 两个关键点：
    /// <list type="number">
    /// <item>必须用 <c>POSIX path of</c> 包住结果。直接返回 <c>choose file</c> 的结果会得到
    /// AppleScript 的 alias 字符串（形如 <c>alias Macintosh HD:Users:me:Movies:a.mp4</c>），
    /// 不是文件系统路径，传回应用后会被判定为"文件不存在"。</item>
    /// <item>起始目录不可用时不能让整个选择器失败：先带起始目录试一次，失败则退化为不指定。</item>
    /// </list>
    /// </summary>
    internal static string BuildMacChooseScript(string title, string start, bool pickFolder)
    {
        var verb = pickFolder ? "choose folder" : "choose file";
        var escapedTitle = EscapeAppleScript(title);
        var escapedStart = EscapeAppleScript(start);
        var hasStart = !string.IsNullOrWhiteSpace(start);

        var withStart = $"POSIX path of ({verb} with prompt \"{escapedTitle}\" default location POSIX file \"{escapedStart}\")";
        var withoutStart = $"POSIX path of ({verb} with prompt \"{escapedTitle}\")";

        var script = new StringBuilder();

        // 把选择器窗口带到前台，否则从后台启动的进程弹出来的选择器会藏在别的窗口后面
        script.AppendLine("set theResult to \"\"");
        script.AppendLine("try");
        script.AppendLine("    tell application \"System Events\" to set frontmost of first process whose frontmost is true to true");
        script.AppendLine("end try");

        if (!hasStart)
        {
            // 单个 try：用户取消（-128）或不支持的环境都返回空字符串
            script.AppendLine("try");
            script.AppendLine("    set theResult to " + withoutStart);
            script.AppendLine("on error");
            script.AppendLine("    return \"\"");
            script.AppendLine("end try");
        }
        else
        {
            // 注意：AppleScript 的一个 try 只能有一个 on error 分支，
            // 所以「用户取消」和「起始目录不可用」必须在同一个处理块里用错误号区分。
            script.AppendLine("try");
            script.AppendLine("    set theResult to " + withStart);
            script.AppendLine("on error errMsg number errNum");
            script.AppendLine("    if errNum is -128 then return \"\"");
            script.AppendLine("    try");
            script.AppendLine("        set theResult to " + withoutStart);
            script.AppendLine("    on error");
            script.AppendLine("        return \"\"");
            script.AppendLine("    end try");
            script.AppendLine("end try");
        }

        script.AppendLine("return theResult");
        return script.ToString().TrimEnd();
    }

    private static async Task<string?> RunMacOsAsync(string appleScript, string title)
    {
        var output = await RunProcessAsync("/usr/bin/osascript", ["-e", appleScript], title).ConfigureAwait(false);
        return NormalizeResult(output);
    }

    private static string EscapeAppleScript(string text) =>
        (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ────────────────────────────────────────────────────────────────
    // Linux（zenity / kdialog）
    // ────────────────────────────────────────────────────────────────

    private static async Task<string?> RunLinuxFilePickerAsync(string title, string start)
    {
        var mediaFilter = "*.mp4 *.mkv *.avi *.mov *.wmv *.flv *.webm *.m4v *.ts *.mpg " +
                          "*.mp3 *.wav *.flac *.aac *.ogg *.m4a *.opus *.gif *.png *.jpg";

        if (CommandExists("zenity"))
        {
            var output = await RunProcessAsync("zenity", [
                "--file-selection",
                $"--title={title}",
                "--filename=" + EnsureTrailingSeparator(start),
                "--file-filter=媒体文件 | " + mediaFilter,
                "--file-filter=所有文件 | *"
            ], title).ConfigureAwait(false);
            var normalized = NormalizeResult(output);
            if (normalized != null) return normalized;
        }

        if (CommandExists("kdialog"))
        {
            var output = await RunProcessAsync("kdialog", [
                "--getopenfilename", EnsureTrailingSeparator(start), mediaFilter, "--title", title
            ], title).ConfigureAwait(false);
            var normalized = NormalizeResult(output);
            if (normalized != null) return normalized;
        }

        return null;
    }

    private static async Task<string?> RunLinuxFolderPickerAsync(string title, string start)
    {
        if (CommandExists("zenity"))
        {
            var output = await RunProcessAsync("zenity", [
                "--file-selection", "--directory", $"--title={title}", "--filename=" + EnsureTrailingSeparator(start)
            ], title).ConfigureAwait(false);
            var normalized = NormalizeResult(output);
            if (normalized != null) return normalized;
        }

        if (CommandExists("kdialog"))
        {
            var output = await RunProcessAsync("kdialog", [
                "--getexistingdirectory", EnsureTrailingSeparator(start), "--title", title
            ], title).ConfigureAwait(false);
            var normalized = NormalizeResult(output);
            if (normalized != null) return normalized;
        }

        return null;
    }

    private static bool CommandExists(string command)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, command))) return true;
            }
            catch
            {
                // 忽略非法 PATH 项
            }
        }
        return false;
    }

    // ────────────────────────────────────────────────────────────────
    // Windows（WinForms via PowerShell）
    // ────────────────────────────────────────────────────────────────

    private static string BuildWindowsFileScript(string title, string start)
    {
        // 用普通字符串拼接，避免原始字符串字面量与 PowerShell 的花括号互相干扰
        var sb = new StringBuilder();
        sb.AppendLine("Add-Type -AssemblyName System.Windows.Forms");
        sb.AppendLine("$dialog = New-Object System.Windows.Forms.OpenFileDialog");
        sb.AppendLine("$dialog.Title = " + PowerShellLiteral(title));
        sb.AppendLine("$dialog.Filter = '媒体文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.mpg;*.mp3;*.wav;*.flac;*.aac;*.ogg;*.m4a;*.opus;*.gif;*.png;*.jpg|所有文件|*.*'");
        sb.AppendLine("$dialog.CheckFileExists = $true");
        sb.AppendLine("$dialog.CheckPathExists = $true");
        sb.AppendLine("if (Test-Path -LiteralPath " + PowerShellLiteral(start) + ") { $dialog.InitialDirectory = " + PowerShellLiteral(start) + " }");
        sb.AppendLine("if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dialog.FileName }");
        return sb.ToString();
    }

    private static string BuildWindowsFolderScript(string title, string start)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Add-Type -AssemblyName System.Windows.Forms");
        sb.AppendLine("$dialog = New-Object System.Windows.Forms.FolderBrowserDialog");
        sb.AppendLine("$dialog.Description = " + PowerShellLiteral(title));
        sb.AppendLine("$dialog.ShowNewFolderButton = $true");
        sb.AppendLine("if (Test-Path -LiteralPath " + PowerShellLiteral(start) + ") { $dialog.SelectedPath = " + PowerShellLiteral(start) + " }");
        sb.AppendLine("if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dialog.SelectedPath }");
        return sb.ToString();
    }

    /// <summary>PowerShell 单引号字符串字面量（内部单引号翻倍），可安全承载中文与特殊字符</summary>
    private static string PowerShellLiteral(string text) =>
        "'" + (text ?? string.Empty).Replace("'", "''") + "'";

    private static async Task<string?> RunPowerShellAsync(string script, string title)
    {
        // 用 -EncodedCommand 传递脚本：避免引号转义问题，也避免中文乱码
        var preamble = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(preamble + script));

        foreach (var shell in new[] { "powershell.exe", "pwsh.exe", "pwsh" })
        {
            var output = await RunProcessAsync(shell,
                ["-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand", encoded], title).ConfigureAwait(false);

            if (output != null) return NormalizeResult(output);
        }

        return null;
    }

    // ────────────────────────────────────────────────────────────────
    // 公共
    // ────────────────────────────────────────────────────────────────

    private static async Task<string?> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string title)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            try { process.StandardInput.Close(); } catch { }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(DialogTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var stdout = await SafeAwait(stdoutTask).ConfigureAwait(false);
            _ = await SafeAwait(stderrTask).ConfigureAwait(false);

            if (process.ExitCode != 0) return null;
            return stdout;
        }
        catch
        {
            // 命令不存在或无法执行：由调用方回退到「手工输入路径」
            return null;
        }
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? NormalizeResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().Trim('\r', '\n').Trim();
        if (value.Length == 0) return null;
        if (value.Contains("User canceled", StringComparison.OrdinalIgnoreCase)) return null;

        // 兜底：万一某个 macOS / AppleScript 组合仍然把结果序列化成文件引用字符串
        // （实测会以 alias / file / folder / disk / document file 开头，后面跟 HFS 路径），
        // 在这里就转成 POSIX 路径，绝不让 "alias Macintosh HD:Users:…" 流到界面上去。
        foreach (var prefix in AppleScriptReferencePrefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            value = ConvertAppleScriptAliasToPosix(value[prefix.Length..]);
            if (string.IsNullOrEmpty(value)) return null;
            break;
        }

        // 目录会带末尾斜杠，去掉更符合直觉（根目录除外）
        value = TrimTrailingSeparator(value);
        return value.Length == 0 ? null : value;
    }

    /// <summary>AppleScript 文件引用被序列化成字符串时可能出现的前缀</summary>
    private static readonly string[] AppleScriptReferencePrefixes =
    [
        "alias ", "file ", "folder ", "disk ", "document file "
    ];

    /// <summary>
    /// 把 AppleScript 的 HFS 风格 alias 路径转成 POSIX 路径。
    /// 形如 <c>Macintosh HD:Users:me:Movies:25 4K.mp4</c> → <c>/Users/me/Movies/25 4K.mp4</c>。
    /// 第一段是卷名：启动卷映射到 <c>/</c>，其它卷映射到 <c>/Volumes/卷名/…</c>。
    /// </summary>
    internal static string? ConvertAppleScriptAliasToPosix(string hfsPath)
    {
        if (string.IsNullOrWhiteSpace(hfsPath)) return null;

        // HFS 路径用 ':' 分隔，因此路径段内部不会再出现 ':'，不需要反转义
        var parts = hfsPath.Split(':', StringSplitOptions.None);
        if (parts.Length <= 1) return null;

        var volume = parts[0];
        var rest = parts.Skip(1).Where(p => p.Length > 0).ToArray();

        var isBootVolume = string.Equals(volume, "Macintosh HD", StringComparison.OrdinalIgnoreCase);
        var segments = isBootVolume
            ? rest
            : new[] { "Volumes", volume }.Concat(rest).ToArray();

        if (segments.Length == 0) return "/";
        return "/" + string.Join('/', segments);
    }

    /// <summary>供验证工具调用的包装（NormalizeResult 是私有的）</summary>
    internal static string? NormalizeDialogResultForTests(string? raw) => NormalizeResult(raw);

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1) return path;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length > 0 ? trimmed : path;
    }

    private static string ResolveStartDirectory(string? initialDirectory)
    {
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            return AppPaths.ToAbsolute(initialDirectory);
        }
        return AppPaths.DefaultOutputDirectory;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }
}

using System.Diagnostics;
using System.Text;
using FFmpegWebUI.Data;
using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>设置服务实现</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly Lock _gate = new();
    private AppSettings? _cached;

    public SettingsService(ILiteDbContext db, IFFmpegLocator locator)
    {
        Db = db;
        Locator = locator;
    }

    private ILiteDbContext Db { get; }
    private IFFmpegLocator Locator { get; }

    public event EventHandler? SettingsChanged;

    public Task<AppSettings> GetSettingsAsync()
    {
        lock (_gate)
        {
            if (_cached != null) return Task.FromResult(_cached);

            AppSettings? settings;
            try
            {
                settings = Db.Settings.FindAll().FirstOrDefault();
            }
            catch
            {
                settings = null;
            }

            if (settings == null)
            {
                settings = CreateDefaultSettings();
                try
                {
                    Db.Settings.Insert(settings);
                }
                catch
                {
                    // 数据库不可写时仍返回内存默认值，保证界面可用
                }
            }

            _cached = settings.Normalize();
            return Task.FromResult(_cached);
        }
    }

    public Task SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            settings.Normalize();

            try
            {
                var existing = Db.Settings.FindAll().FirstOrDefault();
                settings.Id = existing?.Id ?? settings.Id;
                Db.Settings.Upsert(settings);
            }
            catch
            {
                // 保存失败时保留内存状态
            }

            _cached = settings;
        }

        // 路径可能已变化，清掉 FFmpeg 定位缓存并通知订阅者
        Locator.Invalidate();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ResetToDefaultAsync()
    {
        lock (_gate)
        {
            try
            {
                Db.Settings.DeleteAll();
                var fresh = CreateDefaultSettings();
                Db.Settings.Insert(fresh);
                _cached = fresh.Normalize();
            }
            catch
            {
                _cached = CreateDefaultSettings().Normalize();
            }
        }

        Locator.Invalidate();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task<bool> ValidateFFmpegPathAsync(string path)
    {
        var output = await RunVersionAsync(path, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return output is not null && output.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> GetFFmpegVersionAsync(string path)
    {
        var output = await RunVersionAsync(path, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (output is null) return null;

        var firstLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase));

        if (firstLine is null) return null;

        // 截断到版权信息之前，避免把整段编译参数塞进界面
        var buildIndex = firstLine.IndexOf("Copyright", StringComparison.OrdinalIgnoreCase);
        return buildIndex > 0 ? firstLine[..buildIndex].Trim() : firstLine;
    }

    private static async Task<string?> RunVersionAsync(string path, TimeSpan maxWait)
    {
        var executable = string.IsNullOrWhiteSpace(path) ? "ffmpeg" : AppPaths.ToAbsolute(path);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-version");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            try { process.StandardInput.Close(); } catch { }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(maxWait);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var stdout = await SafeRead(stdoutTask).ConfigureAwait(false);
            var stderr = await SafeRead(stderrTask).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch
        {
            // 文件不存在或不可执行
            return null;
        }
    }

    private static async Task<string> SafeRead(Task<string> task)
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

    private static AppSettings CreateDefaultSettings() => new()
    {
        FFmpegPath = string.Empty,
        FFprobePath = string.Empty,
        DefaultOutputDirectory = AppPaths.DefaultOutputDirectory,
        OutputNaming = OutputNamingRule.Suffix,
        OutputSuffix = "_converted",
        FileExistsAction = FileExistsAction.Ask,
        PreferHardwareAcceleration = true,
        PreferHardwareDecoding = false,
        AutoFallbackToSoftware = true,
        PreferredHardwareEncoder = string.Empty,
        MaxConcurrentTasks = 1,
        MaxLogLines = 500,
        TaskHistoryRetentionDays = 30,
        Theme = "System",
        NotifyOnCompletion = true,
        AutoOpenBrowser = true,
        ListenPort = 5265
    };
}

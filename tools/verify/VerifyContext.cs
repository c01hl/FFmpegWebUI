using FFmpegWebUI.Data;
using FFmpegWebUI.Models;
using FFmpegWebUI.Services;
using Microsoft.Extensions.Configuration;

namespace FFmpegWebUI.Verify;

/// <summary>
/// 验证运行的公共环境：构造一套与界面完全相同的服务、生成测试素材、记录检查结果。
/// </summary>
internal sealed class VerifyContext : IAsyncDisposable
{
    private readonly string _root;

    public VerifyContext(string ffmpegPath)
    {
        _root = Path.Combine(Path.GetTempPath(), "ffmpegwebui-verify-" + Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(_root, "data");
        MediaDirectory = Path.Combine(_root, "media");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(MediaDirectory);

        Environment.SetEnvironmentVariable("FFMPEGWEBUI_DATA_DIR", DataDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FFmpegWebUI:DatabasePath"] = Path.Combine(DataDirectory, "data.db")
            })
            .Build();

        Db = new LiteDbContext(configuration);
        Locator = new FFmpegLocator(Db);
        Settings = new SettingsService(Db, Locator);
        FFmpeg = new FFmpegService(Settings, Locator);
        Files = new FileService();
        Hardware = new HardwareDetectionService(Db, Settings, FFmpeg, Locator);
        Templates = new TemplateService(Db, FFmpeg, Files, Hardware, Settings);
        Queue = new ConversionQueue(Db, FFmpeg, Templates, Files, Settings, Hardware,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversionQueue>.Instance);
        Tasks = new TaskService(Db, FFmpeg, Templates, Files, Settings, Queue);

        RequestedFfmpegPath = ffmpegPath;
    }

    public string DataDirectory { get; }
    public string MediaDirectory { get; }
    public string RequestedFfmpegPath { get; }

    public LiteDbContext Db { get; }
    public FFmpegLocator Locator { get; }
    public SettingsService Settings { get; }
    public FFmpegService FFmpeg { get; }
    public FileService Files { get; }
    public HardwareDetectionService Hardware { get; }
    public TemplateService Templates { get; }
    public ConversionQueue Queue { get; }
    public TaskService Tasks { get; }

    private int _passed;
    private readonly List<string> _failures = [];

    public int Passed => _passed;
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>记录一项检查结果</summary>
    public void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  ✅ {name}");
        }
        else
        {
            var line = detail is null ? name : $"{name} — {detail}";
            _failures.Add(line);
            Console.WriteLine($"  ❌ {line}");
        }
    }

    /// <summary>输出一行信息（不计入检查）</summary>
    public static void Info(string text) => Console.WriteLine("     " + text);

    /// <summary>轮询等待条件成立</summary>
    public static async Task<T?> WaitForAsync<T>(
        Func<Task<T?>> read, Func<T?, bool> predicate, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        T? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await read();
            if (predicate(last)) return last;
            await Task.Delay(120);
        }
        return last;
    }

    /// <summary>用 ffmpeg 生成一段带音轨的测试素材</summary>
    public async Task<string> CreateSampleAsync(string name, int seconds = 3, string size = "640x360", int fps = 25, bool withAudio = true)
    {
        var path = Path.Combine(MediaDirectory, name);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc2=duration={seconds}:size={size}:rate={fps}"
        };
        if (withAudio)
        {
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}"]);
        }
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        if (withAudio) args.AddRange(["-c:a", "aac", "-shortest"]);
        args.AddRange(["-y", path]);

        await RunProcessAsync(RequestedFfmpegPath, args);
        return path;
    }

    public static async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            Info($"[生成测试素材失败] {stderr.Trim()}");
        }
    }

    /// <summary>当前主机上可用的编码器名字集合</summary>
    public async Task<HashSet<string>> GetAvailableEncoderNamesAsync()
    {
        var info = await FFmpeg.GetFFmpegInfoAsync(forceRefresh: true);
        return info?.SupportedEncoders.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>当前主机上可用的滤镜名字集合</summary>
    public async Task<HashSet<string>> GetAvailableFilterNamesAsync()
    {
        var info = await FFmpeg.GetFFmpegInfoAsync();
        return info?.SupportedFilters.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Queue.DisposeAsync(); } catch { /* 关闭期异常可忽略 */ }
        try { Db.Dispose(); } catch { /* 同上 */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录可能被占用 */ }
    }
}

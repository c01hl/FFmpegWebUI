using FFmpegWebUI.Services;

namespace FFmpegWebUI.Verify;

/// <summary>
/// 离线验证工具入口。
/// 不启动 Web 界面，直接调用与界面完全相同的服务，验证整条转换链路。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "all";

        var ffmpegPath = ResolveFfmpegPath(args);
        Console.WriteLine($"FFmpeg WebUI 验证工具 · v{AppInfo.Version}");
        Console.WriteLine($"FFmpeg: {ffmpegPath}");
        Console.WriteLine(new string('=', 72));

        await using var ctx = new VerifyContext(ffmpegPath);

        try
        {
            if (mode is "all" or "pipeline")
            {
                await PipelineChecks.RunAsync(ctx);
            }

            if (mode is "all" or "templates")
            {
                await TemplateChecks.RunAsync(ctx);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"❌ 验证过程中抛出异常：{ex}");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"结果：通过 {ctx.Passed} 项，失败 {ctx.Failures.Count} 项");

        if (ctx.Failures.Count > 0)
        {
            Console.WriteLine("失败明细：");
            foreach (var failure in ctx.Failures) Console.WriteLine("  · " + failure);
            return 1;
        }

        Console.WriteLine("全部检查通过。");
        return 0;
    }

    /// <summary>解析要使用的 ffmpeg：命令行 --ffmpeg=路径 > 环境变量 FFMPEG > 自动探测</summary>
    private static string ResolveFfmpegPath(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--ffmpeg=", StringComparison.OrdinalIgnoreCase))
            {
                return arg["--ffmpeg=".Length..];
            }
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;

        // 复用应用自己的探测逻辑（内置副本 → PATH → 各平台常见安装位置）
        var dataDirectory = Path.Combine(Path.GetTempPath(), "ffmpegwebui-verify-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        Environment.SetEnvironmentVariable("FFMPEGWEBUI_DATA_DIR", dataDirectory);

        try
        {
            using var db = new Data.LiteDbContext(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            var locator = new FFmpegLocator(db);
            return locator.Resolve().FFmpeg;
        }
        finally
        {
            try { Directory.Delete(dataDirectory, true); } catch { /* 忽略 */ }
        }
    }
}

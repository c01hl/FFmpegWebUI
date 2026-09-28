namespace FFmpegWebUI.Services;

/// <summary>应用元信息</summary>
public static class AppInfo
{
    /// <summary>应用显示名称</summary>
    public const string Name = "FFmpeg WebUI";

    /// <summary>版本号</summary>
    public const string Version = "2.0.0";

    /// <summary>包含平台信息的长版本串</summary>
    public static string FullVersion => $"{Name} v{Version} · {AppPaths.PlatformDisplayName} · .NET {Environment.Version}";

    /// <summary>本地服务地址</summary>
    public static string BaseUrl(int port) => $"http://127.0.0.1:{port}";
}

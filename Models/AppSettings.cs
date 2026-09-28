using LiteDB;

namespace FFmpegWebUI.Models;

/// <summary>应用设置</summary>
public class AppSettings
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>FFmpeg 可执行文件路径（空则自动探测 PATH / 内置目录）</summary>
    public string FFmpegPath { get; set; } = string.Empty;

    /// <summary>ffprobe 可执行文件路径（空则自动探测 PATH / 内置目录）</summary>
    public string FFprobePath { get; set; } = string.Empty;

    /// <summary>默认输出目录</summary>
    public string DefaultOutputDirectory { get; set; } = string.Empty;

    /// <summary>输出文件命名规则：Original（保持原名）/ Suffix（添加后缀）</summary>
    public OutputNamingRule OutputNaming { get; set; } = OutputNamingRule.Suffix;

    /// <summary>输出文件后缀（如 "_converted"）</summary>
    public string OutputSuffix { get; set; } = "_converted";

    /// <summary>文件已存在时的处理：Ask / Overwrite / Skip / Rename</summary>
    public FileExistsAction FileExistsAction { get; set; } = FileExistsAction.Ask;

    /// <summary>是否优先使用硬件加速</summary>
    public bool PreferHardwareAcceleration { get; set; } = true;

    /// <summary>是否优先使用硬件解码（输入侧 hwaccel）</summary>
    public bool PreferHardwareDecoding { get; set; } = false;

    /// <summary>用户手动指定的首选硬件编码器（空 = 自动选择）</summary>
    public string PreferredHardwareEncoder { get; set; } = string.Empty;

    /// <summary>
    /// 硬件编码器初始化失败时是否自动回退到软件编码。
    /// 只有在 FFmpeg 尚未写出有效数据时才会回退，避免产出损坏文件。
    /// </summary>
    public bool AutoFallbackToSoftware { get; set; } = true;

    /// <summary>同时执行的转换任务上限</summary>
    public int MaxConcurrentTasks { get; set; } = 1;

    /// <summary>单个任务保留的最大日志行数</summary>
    public int MaxLogLines { get; set; } = 500;

    /// <summary>保留任务历史天数（0 表示不清理）</summary>
    public int TaskHistoryRetentionDays { get; set; } = 30;

    /// <summary>界面主题：Light / Dark / System</summary>
    public string Theme { get; set; } = "System";

    /// <summary>追加到 ffmpeg 全局参数（高级用户，如 -threads 4）</summary>
    public string GlobalFFmpegArguments { get; set; } = string.Empty;

    /// <summary>任务完成后发送浏览器通知</summary>
    public bool NotifyOnCompletion { get; set; } = true;

    /// <summary>启动时自动打开浏览器（仅发布版本生效）</summary>
    public bool AutoOpenBrowser { get; set; } = true;

    /// <summary>Web 服务监听端口（0 表示自动选择空闲端口）</summary>
    public int ListenPort { get; set; } = 5265;

    /// <summary>规范化：修正非法/越界值，避免旧版本数据导致运行时异常</summary>
    public AppSettings Normalize()
    {
        if (MaxConcurrentTasks is < 1 or > 16) MaxConcurrentTasks = 1;
        if (MaxLogLines is < 50 or > 20000) MaxLogLines = 500;
        if (TaskHistoryRetentionDays is < 0 or > 3650) TaskHistoryRetentionDays = 30;
        if (ListenPort is < 0 or > 65535) ListenPort = 5265;
        if (string.IsNullOrWhiteSpace(OutputSuffix)) OutputSuffix = "_converted";
        OutputSuffix = OutputSuffix.Trim();
        Theme = Theme is "Light" or "Dark" or "System" ? Theme : "System";
        FFmpegPath = (FFmpegPath ?? string.Empty).Trim().Trim('"');
        FFprobePath = (FFprobePath ?? string.Empty).Trim().Trim('"');
        DefaultOutputDirectory = (DefaultOutputDirectory ?? string.Empty).Trim();
        PreferredHardwareEncoder = (PreferredHardwareEncoder ?? string.Empty).Trim();
        GlobalFFmpegArguments = (GlobalFFmpegArguments ?? string.Empty).Trim();
        return this;
    }
}

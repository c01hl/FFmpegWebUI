using LiteDB;

namespace FFmpegWebUI.Models;

/// <summary>应用设置</summary>
public class AppSettings
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>FFmpeg 可执行文件路径（空则使用 PATH）</summary>
    public string FFmpegPath { get; set; } = string.Empty;

    /// <summary>ffprobe 可执行文件路径</summary>
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

    /// <summary>保留任务历史天数（0 表示不保留）</summary>
    public int TaskHistoryRetentionDays { get; set; } = 30;

    /// <summary>界面主题：Light / Dark / System</summary>
    public string Theme { get; set; } = "System";
}

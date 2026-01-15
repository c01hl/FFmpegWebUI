using LiteDB;

namespace FFmpegWebUI.Models;

/// <summary>FFmpeg 命令模板</summary>
public class CommandTemplate
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>模板名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>模板描述</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>FFmpeg 命令参数模板（支持占位符）</summary>
    public string CommandArgs { get; set; } = string.Empty;

    /// <summary>模板类型：System（系统预设）/ User（用户自定义）</summary>
    public TemplateType Type { get; set; } = TemplateType.User;

    /// <summary>模板分类：VideoTranscode / AudioExtract / Compress / etc.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>支持的输入格式（如 ["mp4", "avi", "mkv"]，空表示不限）</summary>
    public List<string> SupportedInputFormats { get; set; } = [];

    /// <summary>默认输出扩展名</summary>
    public string OutputExtension { get; set; } = string.Empty;

    /// <summary>是否需要硬件加速</summary>
    public bool RequiresHardwareAcceleration { get; set; }

    /// <summary>所需的硬件编码器类型（如 "nvenc", "qsv"）</summary>
    public string? RequiredEncoder { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>最后修改时间</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>排序权重（越小越靠前）</summary>
    public int SortOrder { get; set; }
}

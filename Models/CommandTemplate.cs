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

    /// <summary>模板分类：视频转码 / 音频提取 / 压缩 / 分辨率 / 硬件加速 …</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>支持的输入格式（如 ["mp4", "avi"]，空表示不限）</summary>
    public List<string> SupportedInputFormats { get; set; } = [];

    /// <summary>默认输出扩展名（不含点）</summary>
    public string OutputExtension { get; set; } = string.Empty;

    /// <summary>
    /// 目标视频编码格式（h264/hevc/av1/vp9/vp8/prores），
    /// 用于在 {encoder} 占位符下自动挑选本机可用的编码器。留空则自动推断。
    /// </summary>
    public string TargetCodec { get; set; } = string.Empty;

    /// <summary>是否需要硬件加速</summary>
    public bool RequiresHardwareAcceleration { get; set; }

    /// <summary>硬件加速策略</summary>
    public HardwareAccelerationMode HardwareMode { get; set; } = HardwareAccelerationMode.None;

    /// <summary>
    /// 所需编码器。可用族名（"nvenc"/"qsv"/"amf"/"videotoolbox"/"vaapi"/"vulkan"）
    /// 表示该族的任意可用编码器；也可写具体编码器名（如 "h264_nvenc"）。
    /// </summary>
    public string? RequiredEncoder { get; set; }

    /// <summary>模板声明的可调参数，界面据此生成输入控件</summary>
    public List<TemplateParameter> Parameters { get; set; } = [];

    /// <summary>
    /// 模板必须用到的 FFmpeg 滤镜（如 "subtitles"、"drawtext"）。
    /// 用于在界面上提前告知「当前 FFmpeg 构建缺少该滤镜」，
    /// 而不是等到转换时才失败。
    /// </summary>
    public List<string> RequiredFilters { get; set; } = [];

    /// <summary>标签，便于搜索</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>是否推荐（在模板列表中优先展示）</summary>
    public bool IsRecommended { get; set; }

    /// <summary>系统模板预设版本号，用于随应用升级同步内置模板</summary>
    public int PresetVersion { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>最后修改时间</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>排序权重（越小越靠前）</summary>
    public int SortOrder { get; set; }

    /// <summary>用于展示的输入格式描述</summary>
    public string InputFormatsDisplay => SupportedInputFormats.Count == 0
        ? "任意格式"
        : string.Join(" / ", SupportedInputFormats);
}

/// <summary>模板可调参数定义</summary>
public class TemplateParameter
{
    /// <summary>占位符名称（不含花括号），如 "crf"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>界面显示名称</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>帮助说明</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>控件类型</summary>
    public TemplateParameterKind Kind { get; set; } = TemplateParameterKind.Select;

    /// <summary>默认值（占位符未被用户修改时使用）</summary>
    public string DefaultValue { get; set; } = string.Empty;

    /// <summary>可选项（Kind = Select 时使用）</summary>
    public List<TemplateParameterOption> Options { get; set; } = [];

    /// <summary>数字下界</summary>
    public double? Min { get; set; }

    /// <summary>数字上界</summary>
    public double? Max { get; set; }

    /// <summary>布尔为 true 时替换成的文本</summary>
    public string TrueValue { get; set; } = "1";

    /// <summary>布尔为 false 时替换成的文本（空字符串表示移除该片段）</summary>
    public string FalseValue { get; set; } = string.Empty;
}

/// <summary>模板参数选项</summary>
public class TemplateParameterOption
{
    /// <summary>提交给 FFmpeg 的值</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>界面显示文本</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>补充说明</summary>
    public string Description { get; set; } = string.Empty;

    public TemplateParameterOption() { }

    public TemplateParameterOption(string value, string label, string description = "")
    {
        Value = value;
        Label = label;
        Description = description;
    }
}

namespace FFmpegWebUI.Models;

/// <summary>模板类型</summary>
public enum TemplateType
{
    /// <summary>系统预设（不可删除）</summary>
    System = 0,
    /// <summary>用户自定义</summary>
    User = 1
}

/// <summary>任务状态</summary>
public enum TaskStatus
{
    /// <summary>待执行</summary>
    Pending = 0,
    /// <summary>执行中</summary>
    Running = 1,
    /// <summary>已完成</summary>
    Completed = 2,
    /// <summary>失败</summary>
    Failed = 3,
    /// <summary>已取消</summary>
    Cancelled = 4,
    /// <summary>已跳过（输出文件已存在且设置为跳过）</summary>
    Skipped = 5
}

/// <summary>硬件编码器厂商/类型</summary>
public enum EncoderType
{
    /// <summary>软件编码（CPU）</summary>
    Software = 0,
    /// <summary>NVIDIA NVENC</summary>
    Nvenc = 1,
    /// <summary>Intel Quick Sync Video</summary>
    Qsv = 2,
    /// <summary>AMD Advanced Media Framework</summary>
    Amf = 3,
    /// <summary>Apple VideoToolbox</summary>
    VideoToolbox = 4,
    /// <summary>Linux VA-API（Intel/AMD 通用）</summary>
    Vaapi = 5,
    /// <summary>Vulkan Video（跨厂商）</summary>
    Vulkan = 6,
    /// <summary>Windows Media Foundation</summary>
    MediaFoundation = 7,
    /// <summary>其他/未知</summary>
    Other = 99
}

/// <summary>模板对硬件加速的依赖程度</summary>
public enum HardwareAccelerationMode
{
    /// <summary>不使用硬件加速（纯软件编码）</summary>
    None = 0,
    /// <summary>自动：有可用硬件编码器就优先使用，否则回退到软件编码</summary>
    Auto = 1,
    /// <summary>必须使用指定族的硬件编码器，否则模板不可用</summary>
    Required = 2
}

/// <summary>输出文件命名规则</summary>
public enum OutputNamingRule
{
    /// <summary>保持原名</summary>
    Original = 0,
    /// <summary>添加后缀</summary>
    Suffix = 1
}

/// <summary>文件已存在时的处理方式</summary>
public enum FileExistsAction
{
    /// <summary>询问用户（单文件转换时在界面确认，批量处理时自动重命名）</summary>
    Ask = 0,
    /// <summary>覆盖</summary>
    Overwrite = 1,
    /// <summary>跳过</summary>
    Skip = 2,
    /// <summary>重命名</summary>
    Rename = 3
}

/// <summary>模板参数控件类型</summary>
public enum TemplateParameterKind
{
    /// <summary>下拉选择</summary>
    Select = 0,
    /// <summary>数字输入</summary>
    Number = 1,
    /// <summary>文本输入</summary>
    Text = 2,
    /// <summary>布尔开关</summary>
    Boolean = 3
}

/// <summary>模板校验问题的严重程度</summary>
public enum TemplateIssueSeverity
{
    /// <summary>提示</summary>
    Info = 0,
    /// <summary>警告：可用，但可能不符合预期</summary>
    Warning = 1,
    /// <summary>错误：几乎必然执行失败</summary>
    Error = 2
}

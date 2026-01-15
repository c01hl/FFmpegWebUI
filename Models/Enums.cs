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
    Cancelled = 4
}

/// <summary>编码器类型</summary>
public enum EncoderType
{
    /// <summary>软件编码（CPU）</summary>
    Software = 0,
    /// <summary>NVIDIA NVENC</summary>
    Nvenc = 1,
    /// <summary>Intel Quick Sync</summary>
    Qsv = 2,
    /// <summary>AMD AMF</summary>
    Amf = 3,
    /// <summary>Apple VideoToolbox</summary>
    VideoToolbox = 4
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
    /// <summary>询问用户</summary>
    Ask = 0,
    /// <summary>覆盖</summary>
    Overwrite = 1,
    /// <summary>跳过</summary>
    Skip = 2,
    /// <summary>重命名</summary>
    Rename = 3
}

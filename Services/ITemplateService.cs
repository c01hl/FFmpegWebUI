using FFmpegWebUI.Models;
using LiteDB;

namespace FFmpegWebUI.Services;

/// <summary>模板管理服务接口</summary>
public interface ITemplateService
{
    /// <summary>获取所有模板（排序：推荐 → 排序权重 → 名称）</summary>
    Task<List<CommandTemplate>> GetAllTemplatesAsync(bool includeSystem = true);

    /// <summary>按分类获取模板</summary>
    Task<List<CommandTemplate>> GetTemplatesByCategoryAsync(string category);

    /// <summary>获取单个模板</summary>
    Task<CommandTemplate?> GetTemplateByIdAsync(ObjectId id);

    /// <summary>创建用户模板</summary>
    Task<CommandTemplate> CreateTemplateAsync(CommandTemplate template);

    /// <summary>更新模板（系统模板保留 System 类型）</summary>
    Task<bool> UpdateTemplateAsync(CommandTemplate template);

    /// <summary>删除用户模板（系统模板不允许通过此方法删除）</summary>
    Task<bool> DeleteTemplateAsync(ObjectId id);

    /// <summary>复制模板为用户模板</summary>
    Task<CommandTemplate> CopyTemplateAsync(ObjectId sourceId, string? newName = null);

    /// <summary>初始化/同步系统预设模板（按 PresetVersion 增量更新，不会覆盖用户模板）</summary>
    Task<SystemTemplateSyncResult> InitializeSystemTemplatesAsync();

    /// <summary>重置系统预设模板（删除后重新初始化）</summary>
    Task ResetSystemTemplatesAsync();

    /// <summary>创建系统模板（管理员功能）</summary>
    Task<CommandTemplate> CreateSystemTemplateAsync(CommandTemplate template);

    /// <summary>更新系统模板（管理员功能）</summary>
    Task<bool> UpdateSystemTemplateAsync(CommandTemplate template);

    /// <summary>删除系统模板（管理员功能）</summary>
    Task<bool> DeleteSystemTemplateAsync(ObjectId id);

    /// <summary>获取所有分类（按预设顺序）</summary>
    Task<List<string>> GetCategoriesAsync();

    /// <summary>生成模板参数的默认值字典</summary>
    Dictionary<string, string> CreateDefaultParameterValues(CommandTemplate template);

    /// <summary>校验模板是否可以正常执行</summary>
    IReadOnlyList<TemplateIssue> Validate(CommandTemplate template);

    /// <summary>
    /// 返回模板在本机缺失的必需滤镜（空列表表示都可用）。
    /// report 为 null 或尚未包含滤镜清单时返回空列表，避免误报不可用。
    /// </summary>
    IReadOnlyList<string> GetMissingFilters(CommandTemplate template, HardwareDetectionReport? report);

    /// <summary>用一段自动生成的测试片段试跑模板，确认模板在本机真的可用</summary>
    Task<TemplateTestResult> TestTemplateAsync(CommandTemplate template, IReadOnlyDictionary<string, string>? parameterValues = null);

    /// <summary>导出模板为 JSON（便于分享/备份）</summary>
    string ExportToJson(IEnumerable<CommandTemplate> templates);

    /// <summary>从 JSON 导入模板，返回成功导入的数量</summary>
    Task<int> ImportFromJsonAsync(string json, bool asSystemTemplate = false);

    /// <summary>当前系统模板预设版本</summary>
    int PresetVersion { get; }
}

/// <summary>系统模板同步结果</summary>
public sealed record SystemTemplateSyncResult(int Added, int Updated, int Unchanged, int PresetVersion)
{
    /// <summary>是否有任何变化</summary>
    public bool Changed => Added > 0 || Updated > 0;

    /// <summary>用户可读说明</summary>
    public string Summary => Changed
        ? $"系统模板已更新：新增 {Added} 个，更新 {Updated} 个（预设 v{PresetVersion}）"
        : $"系统模板已是最新（预设 v{PresetVersion}）";
}

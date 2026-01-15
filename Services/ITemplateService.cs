using FFmpegWebUI.Models;
using LiteDB;

namespace FFmpegWebUI.Services;

/// <summary>模板管理服务接口</summary>
public interface ITemplateService
{
    /// <summary>获取所有模板</summary>
    Task<List<CommandTemplate>> GetAllTemplatesAsync(bool includeSystem = true);

    /// <summary>按分类获取模板</summary>
    Task<List<CommandTemplate>> GetTemplatesByCategoryAsync(string category);

    /// <summary>获取单个模板</summary>
    Task<CommandTemplate?> GetTemplateByIdAsync(ObjectId id);

    /// <summary>创建用户模板</summary>
    Task<CommandTemplate> CreateTemplateAsync(CommandTemplate template);

    /// <summary>更新用户模板</summary>
    Task<bool> UpdateTemplateAsync(CommandTemplate template);

    /// <summary>删除用户模板</summary>
    Task<bool> DeleteTemplateAsync(ObjectId id);

    /// <summary>复制模板为用户模板</summary>
    Task<CommandTemplate> CopyTemplateAsync(ObjectId sourceId, string? newName = null);

    /// <summary>初始化系统预设模板</summary>
    Task InitializeSystemTemplatesAsync();

    /// <summary>重置系统预设模板（删除后重新初始化）</summary>
    Task ResetSystemTemplatesAsync();

    /// <summary>创建系统模板（管理员功能）</summary>
    Task<CommandTemplate> CreateSystemTemplateAsync(CommandTemplate template);

    /// <summary>更新系统模板（管理员功能）</summary>
    Task<bool> UpdateSystemTemplateAsync(CommandTemplate template);

    /// <summary>删除系统模板（管理员功能）</summary>
    Task<bool> DeleteSystemTemplateAsync(ObjectId id);

    /// <summary>获取所有分类</summary>
    Task<List<string>> GetCategoriesAsync();
}

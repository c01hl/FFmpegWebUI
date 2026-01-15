using FFmpegWebUI.Data;
using FFmpegWebUI.Models;
using LiteDB;

namespace FFmpegWebUI.Services;

/// <summary>模板管理服务实现</summary>
public class TemplateService(ILiteDbContext db) : ITemplateService
{
    public Task<List<CommandTemplate>> GetAllTemplatesAsync(bool includeSystem = true)
    {
        var query = db.Templates.Query();
        if (!includeSystem)
        {
            query = query.Where(t => t.Type == TemplateType.User);
        }
        var templates = query.OrderBy(t => t.SortOrder).ToList();
        return Task.FromResult(templates);
    }

    public Task<List<CommandTemplate>> GetTemplatesByCategoryAsync(string category)
    {
        var templates = db.Templates
            .Query()
            .Where(t => t.Category == category)
            .OrderBy(t => t.SortOrder)
            .ToList();
        return Task.FromResult(templates);
    }

    public Task<CommandTemplate?> GetTemplateByIdAsync(ObjectId id)
    {
        var template = db.Templates.FindById(id);
        return Task.FromResult(template);
    }

    public Task<CommandTemplate> CreateTemplateAsync(CommandTemplate template)
    {
        template.Type = TemplateType.User;
        template.CreatedAt = DateTime.UtcNow;
        template.UpdatedAt = DateTime.UtcNow;
        db.Templates.Insert(template);
        return Task.FromResult(template);
    }

    public Task<bool> UpdateTemplateAsync(CommandTemplate template)
    {
        var existing = db.Templates.FindById(template.Id);
        if (existing == null || existing.Type == TemplateType.System)
        {
            return Task.FromResult(false);
        }

        template.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(db.Templates.Update(template));
    }

    public Task<bool> DeleteTemplateAsync(ObjectId id)
    {
        var template = db.Templates.FindById(id);
        if (template == null || template.Type == TemplateType.System)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(db.Templates.Delete(id));
    }

    public Task<CommandTemplate> CopyTemplateAsync(ObjectId sourceId, string? newName = null)
    {
        var source = db.Templates.FindById(sourceId);
        if (source == null)
        {
            throw new ArgumentException("源模板不存在");
        }

        var copy = new CommandTemplate
        {
            Id = ObjectId.NewObjectId(),
            Name = newName ?? $"{source.Name} (副本)",
            Description = source.Description,
            CommandArgs = source.CommandArgs,
            Type = TemplateType.User,
            Category = source.Category,
            SupportedInputFormats = [.. source.SupportedInputFormats],
            OutputExtension = source.OutputExtension,
            RequiresHardwareAcceleration = source.RequiresHardwareAcceleration,
            RequiredEncoder = source.RequiredEncoder,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SortOrder = source.SortOrder + 1
        };

        db.Templates.Insert(copy);
        return Task.FromResult(copy);
    }

    public Task InitializeSystemTemplatesAsync()
    {
        // 检查是否已初始化
        if (db.Templates.Query().Where(t => t.Type == TemplateType.System).Count() > 0)
        {
            return Task.CompletedTask;
        }

        var templates = GetSystemTemplates();
        db.Templates.InsertBulk(templates);
        return Task.CompletedTask;
    }

    public Task ResetSystemTemplatesAsync()
    {
        // 删除所有系统模板
        var systemIds = db.Templates.Query()
            .Where(t => t.Type == TemplateType.System)
            .Select(t => t.Id)
            .ToList();
        
        foreach (var id in systemIds)
        {
            db.Templates.Delete(id);
        }

        // 重新初始化
        var templates = GetSystemTemplates();
        db.Templates.InsertBulk(templates);
        return Task.CompletedTask;
    }

    public Task<CommandTemplate> CreateSystemTemplateAsync(CommandTemplate template)
    {
        template.Type = TemplateType.System;
        template.CreatedAt = DateTime.UtcNow;
        template.UpdatedAt = DateTime.UtcNow;
        db.Templates.Insert(template);
        return Task.FromResult(template);
    }

    public Task<bool> UpdateSystemTemplateAsync(CommandTemplate template)
    {
        var existing = db.Templates.FindById(template.Id);
        if (existing == null)
        {
            return Task.FromResult(false);
        }

        template.Type = TemplateType.System; // 确保类型不变
        template.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(db.Templates.Update(template));
    }

    public Task<bool> DeleteSystemTemplateAsync(ObjectId id)
    {
        var template = db.Templates.FindById(id);
        if (template == null)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(db.Templates.Delete(id));
    }

    public Task<List<string>> GetCategoriesAsync()
    {
        var categories = db.Templates
            .Query()
            .Select(t => t.Category)
            .ToList()
            .Distinct()
            .Where(c => !string.IsNullOrEmpty(c))
            .OrderBy(c => c)
            .ToList();
        return Task.FromResult(categories);
    }

    private static List<CommandTemplate> GetSystemTemplates()
    {
        var sortOrder = 0;
        return
        [
            // 视频转码
            new CommandTemplate
            {
                Name = "转换为 MP4 (H.264)",
                Description = "将视频转换为 MP4 格式，使用 H.264 编码，兼容性最佳",
                CommandArgs = "-i \"{input}\" -c:v libx264 -preset medium -crf 23 -c:a aac -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "视频转码",
                SupportedInputFormats = ["avi", "mkv", "mov", "wmv", "flv", "webm"],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "转换为 MP4 (H.265/HEVC)",
                Description = "将视频转换为 MP4 格式，使用 H.265 编码，文件更小",
                CommandArgs = "-i \"{input}\" -c:v libx265 -preset medium -crf 28 -c:a aac -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "视频转码",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "转换为 WebM (VP9)",
                Description = "将视频转换为 WebM 格式，适合网页使用",
                CommandArgs = "-i \"{input}\" -c:v libvpx-vp9 -crf 30 -b:v 0 -c:a libopus -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "视频转码",
                SupportedInputFormats = [],
                OutputExtension = "webm",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },

            // 音频提取
            new CommandTemplate
            {
                Name = "提取音频为 MP3",
                Description = "从视频中提取音频并转换为 MP3 格式",
                CommandArgs = "-i \"{input}\" -vn -c:a libmp3lame -b:a 192k \"{output}\"",
                Type = TemplateType.System,
                Category = "音频提取",
                SupportedInputFormats = ["mp4", "avi", "mkv", "mov", "webm"],
                OutputExtension = "mp3",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "提取音频为 AAC",
                Description = "从视频中提取音频并转换为 AAC 格式，音质更好",
                CommandArgs = "-i \"{input}\" -vn -c:a aac -b:a 256k \"{output}\"",
                Type = TemplateType.System,
                Category = "音频提取",
                SupportedInputFormats = ["mp4", "avi", "mkv", "mov", "webm"],
                OutputExtension = "m4a",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "提取音频为 FLAC",
                Description = "从视频中提取音频并转换为无损 FLAC 格式",
                CommandArgs = "-i \"{input}\" -vn -c:a flac \"{output}\"",
                Type = TemplateType.System,
                Category = "音频提取",
                SupportedInputFormats = ["mp4", "avi", "mkv", "mov", "webm"],
                OutputExtension = "flac",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },

            // 视频压缩
            new CommandTemplate
            {
                Name = "压缩视频 (高质量)",
                Description = "压缩视频文件，保持较高画质 (CRF 18)",
                CommandArgs = "-i \"{input}\" -c:v libx264 -preset slow -crf 18 -c:a aac -b:a 192k \"{output}\"",
                Type = TemplateType.System,
                Category = "视频压缩",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "压缩视频 (中等质量)",
                Description = "压缩视频文件，平衡画质和文件大小 (CRF 23)",
                CommandArgs = "-i \"{input}\" -c:v libx264 -preset medium -crf 23 -c:a aac -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "视频压缩",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "压缩视频 (小体积)",
                Description = "大幅压缩视频文件，牺牲部分画质 (CRF 28)",
                CommandArgs = "-i \"{input}\" -c:v libx264 -preset fast -crf 28 -c:a aac -b:a 96k \"{output}\"",
                Type = TemplateType.System,
                Category = "视频压缩",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },

            // 分辨率调整
            new CommandTemplate
            {
                Name = "调整为 1080p",
                Description = "将视频分辨率调整为 1920x1080 (1080p)",
                CommandArgs = "-i \"{input}\" -vf \"scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2\" -c:v libx264 -crf 23 -c:a aac \"{output}\"",
                Type = TemplateType.System,
                Category = "分辨率调整",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "调整为 720p",
                Description = "将视频分辨率调整为 1280x720 (720p)",
                CommandArgs = "-i \"{input}\" -vf \"scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2\" -c:v libx264 -crf 23 -c:a aac \"{output}\"",
                Type = TemplateType.System,
                Category = "分辨率调整",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "调整为 480p",
                Description = "将视频分辨率调整为 854x480 (480p)",
                CommandArgs = "-i \"{input}\" -vf \"scale=854:480:force_original_aspect_ratio=decrease,pad=854:480:(ow-iw)/2:(oh-ih)/2\" -c:v libx264 -crf 23 -c:a aac \"{output}\"",
                Type = TemplateType.System,
                Category = "分辨率调整",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },

            // 特殊处理
            new CommandTemplate
            {
                Name = "视频转 GIF",
                Description = "将视频转换为 GIF 动图",
                CommandArgs = "-i \"{input}\" -vf \"fps=10,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -loop 0 \"{output}\"",
                Type = TemplateType.System,
                Category = "特殊处理",
                SupportedInputFormats = ["mp4", "avi", "mkv", "mov", "webm"],
                OutputExtension = "gif",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "仅复制流 (无转码)",
                Description = "快速复制视频和音频流到新容器，不重新编码",
                CommandArgs = "-i \"{input}\" -c copy \"{output}\"",
                Type = TemplateType.System,
                Category = "特殊处理",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = false,
                SortOrder = sortOrder++
            },

            // 硬件加速模板
            new CommandTemplate
            {
                Name = "NVENC H.264 (NVIDIA GPU)",
                Description = "使用 NVIDIA GPU 硬件加速编码 H.264",
                CommandArgs = "-i \"{input}\" -c:v h264_nvenc -preset p4 -cq 23 -c:a aac -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "硬件加速",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = true,
                RequiredEncoder = "h264_nvenc",
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "NVENC H.265 (NVIDIA GPU)",
                Description = "使用 NVIDIA GPU 硬件加速编码 H.265/HEVC",
                CommandArgs = "-i \"{input}\" -c:v hevc_nvenc -preset p4 -cq 28 -c:a aac -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "硬件加速",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = true,
                RequiredEncoder = "hevc_nvenc",
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "QSV H.264 (Intel GPU)",
                Description = "使用 Intel GPU 硬件加速编码 H.264",
                CommandArgs = "-i \"{input}\" -c:v h264_qsv -preset medium -global_quality 23 -c:a aac -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "硬件加速",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = true,
                RequiredEncoder = "h264_qsv",
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "QSV H.265 (Intel GPU)",
                Description = "使用 Intel GPU 硬件加速编码 H.265/HEVC",
                CommandArgs = "-i \"{input}\" -c:v hevc_qsv -preset medium -global_quality 28 -c:a aac -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "硬件加速",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = true,
                RequiredEncoder = "hevc_qsv",
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "AMF H.264 (AMD GPU)",
                Description = "使用 AMD GPU 硬件加速编码 H.264",
                CommandArgs = "-i \"{input}\" -c:v h264_amf -quality balanced -rc cqp -qp_i 23 -qp_p 23 -c:a aac -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "硬件加速",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = true,
                RequiredEncoder = "h264_amf",
                SortOrder = sortOrder++
            },
            new CommandTemplate
            {
                Name = "AMF H.265 (AMD GPU)",
                Description = "使用 AMD GPU 硬件加速编码 H.265/HEVC",
                CommandArgs = "-i \"{input}\" -c:v hevc_amf -quality balanced -rc cqp -qp_i 28 -qp_p 28 -c:a aac -b:a 128k \"{output}\"",
                Type = TemplateType.System,
                Category = "硬件加速",
                SupportedInputFormats = [],
                OutputExtension = "mp4",
                RequiresHardwareAcceleration = true,
                RequiredEncoder = "hevc_amf",
                SortOrder = sortOrder++
            },
        ];
    }
}

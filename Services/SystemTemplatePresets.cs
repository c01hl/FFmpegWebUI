using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>
/// 内置系统模板预设。
/// 每次修改模板内容都要提升 <see cref="Version"/>，应用启动时会自动把已有安装
/// 里的系统模板同步到新版本（用户自己创建/复制的模板不受影响）。
/// </summary>
public static class SystemTemplatePresets
{
    /// <summary>预设版本号（修改任何预设内容时递增）</summary>
    public const int Version = 4;

    /// <summary>分类展示顺序</summary>
    public static readonly string[] CategoryOrder =
    [
        "推荐",
        "视频转码",
        "硬件加速",
        "视频压缩",
        "分辨率与画面",
        "音频处理",
        "特殊处理",
    ];

    /// <summary>获取全部预设模板</summary>
    public static List<CommandTemplate> CreateAll()
    {
        var order = 0;
        var templates = new List<CommandTemplate>();

        void Add(
            string category,
            string name,
            string description,
            string args,
            string extension,
            string[]? inputFormats = null,
            string? targetCodec = null,
            HardwareAccelerationMode hardwareMode = HardwareAccelerationMode.None,
            string? requiredEncoder = null,
            List<TemplateParameter>? parameters = null,
            string[]? tags = null,
            bool recommended = false,
            string[]? requiredFilters = null)
        {
            templates.Add(new CommandTemplate
            {
                Name = name,
                Description = description,
                CommandArgs = args,
                Type = TemplateType.System,
                Category = category,
                SupportedInputFormats = inputFormats?.ToList() ?? [],
                OutputExtension = extension,
                TargetCodec = targetCodec ?? string.Empty,
                HardwareMode = hardwareMode,
                RequiresHardwareAcceleration = hardwareMode == HardwareAccelerationMode.Required,
                RequiredEncoder = requiredEncoder,
                Parameters = parameters ?? [],
                RequiredFilters = requiredFilters?.ToList() ?? [],
                Tags = tags?.ToList() ?? [],
                IsRecommended = recommended,
                PresetVersion = Version,
                SortOrder = order++,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var qualityAuto = new TemplateParameter
        {
            Name = "quality",
            Label = "质量",
            Description = "数值越小画质越好、文件越大。留空则由当前编码器的最佳默认值决定。",
            Kind = TemplateParameterKind.Select,
            DefaultValue = string.Empty,
            Options =
            [
                new("", "自动（推荐）"),
                new("18", "高画质"),
                new("23", "均衡"),
                new("28", "小体积"),
                new("32", "极小体积"),
            ]
        };

        var audioBitrate = new TemplateParameter
        {
            Name = "audio_bitrate",
            Label = "音频码率",
            Kind = TemplateParameterKind.Select,
            DefaultValue = "128k",
            Options =
            [
                new("96k", "96 kbps"), new("128k", "128 kbps"), new("192k", "192 kbps"), new("256k", "256 kbps")
            ]
        };

        // ── 推荐 ───────────────────────────────────────────────────
        Add("推荐", "MP4 · H.264（自动选择最佳编码器）",
            "输出兼容性最好的 H.264 MP4。会自动使用本机可用的硬件编码器（NVENC / Quick Sync / AMF / VideoToolbox / VA-API），没有硬件时回退到 x264。",
            "-i \"{input}\" -c:v {encoder} {quality_args} {preset_args} {hwupload_args} " +
            "-map 0:v:0 -map 0:a? -c:a {audio_encoder} -b:a {audio_bitrate} " +
            "-movflags +faststart -pix_fmt yuv420p \"{output}\"",
            "mp4",
            targetCodec: "h264",
            hardwareMode: HardwareAccelerationMode.Auto,
            parameters: [qualityAuto, audioBitrate],
            tags: ["mp4", "h264", "通用", "硬件加速"],
            recommended: true);

        Add("推荐", "MP4 · H.265 / HEVC（自动选择最佳编码器）",
            "同等画质下体积比 H.264 小约 40%。会自动使用本机可用的硬件编码器，没有硬件时回退到 x265。",
            "-i \"{input}\" -c:v {encoder} {quality_args} {preset_args} {hwupload_args} " +
            "-map 0:v:0 -map 0:a? -c:a {audio_encoder} -b:a {audio_bitrate} " +
            "-tag:v hvc1 -movflags +faststart \"{output}\"",
            "mp4",
            targetCodec: "hevc",
            hardwareMode: HardwareAccelerationMode.Auto,
            parameters: [qualityAuto, audioBitrate],
            tags: ["mp4", "hevc", "压缩", "硬件加速"],
            recommended: true);

        Add("推荐", "音频 · 提取为 MP3",
            "从视频或音频中提取音轨并转成通用性最好的 MP3。",
            "-i \"{input}\" -vn -map 0:a:0 -c:a libmp3lame -b:a {audio_bitrate} \"{output}\"",
            "mp3",
            inputFormats: ["mp4", "mkv", "avi", "mov", "webm", "flv", "ts", "wav", "flac", "m4a", "aac", "ogg", "wmv"],
            parameters: [audioBitrate],
            tags: ["mp3", "音频", "提取"],
            recommended: true);

        Add("推荐", "快速换容器（不改画质，秒完成）",
            "只重新封装容器，不重新编码，速度极快、画质完全无损。仅当目标容器支持源编码格式时可用。",
            "-i \"{input}\" -map 0 -c copy -movflags +faststart \"{output}\"",
            "mp4",
            tags: ["封装", "无损", "快速"],
            recommended: true);

        // ── 视频转码 ────────────────────────────────────────────────
        Add("视频转码", "转换为 MP4（H.264 软件编码 x264)",
            "使用 CPU 软件编码，画质与体积控制最精细，兼容所有平台。",
            "-i \"{input}\" -c:v libx264 -crf {quality} -preset {preset} -c:a aac -b:a {audio_bitrate} " +
            "-movflags +faststart -pix_fmt yuv420p \"{output}\"",
            "mp4",
            inputFormats: ["avi", "mkv", "mov", "wmv", "flv", "webm", "ts", "mpg", "m4v", "3gp"],
            parameters:
            [
                new TemplateParameter
                {
                    Name = "quality", Label = "CRF 质量", Kind = TemplateParameterKind.Select, DefaultValue = "23",
                    Description = "18 接近无损，23 为默认均衡值，28 体积明显更小。",
                    Options = [new("18", "18 · 高画质"), new("23", "23 · 均衡"), new("28", "28 · 小体积")]
                },
                new TemplateParameter
                {
                    Name = "preset", Label = "编码速度", Kind = TemplateParameterKind.Select, DefaultValue = "medium",
                    Description = "越慢压缩率越高（体积更小）。",
                    Options = [new("veryfast", "很快"), new("fast", "快"), new("medium", "中等（推荐）"), new("slow", "慢 · 更小"), new("veryslow", "很慢 · 最小")]
                },
                audioBitrate
            ],
            tags: ["mp4", "h264", "软件编码"]);

        Add("视频转码", "转换为 MKV（H.265 软件编码 x265)",
            "MKV 容器 + x265 软件编码，适合本地归档保存。",
            "-i \"{input}\" -c:v libx265 -crf {quality} -preset {preset} -c:a aac -b:a {audio_bitrate} \"{output}\"",
            "mkv",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "quality", Label = "CRF 质量", Kind = TemplateParameterKind.Select, DefaultValue = "28",
                    Description = "x265 的 CRF 通常比 x264 高 3~5。",
                    Options = [new("22", "22 · 高画质"), new("28", "28 · 均衡"), new("33", "33 · 小体积")]
                },
                new TemplateParameter
                {
                    Name = "preset", Label = "编码速度", Kind = TemplateParameterKind.Select, DefaultValue = "medium",
                    Options = [new("veryfast", "很快"), new("medium", "中等（推荐）"), new("slow", "慢 · 更小")]
                },
                audioBitrate
            ],
            tags: ["mkv", "hevc", "归档"]);

        Add("视频转码", "转换为 WebM（VP9 + Opus）",
            "开源免专利格式，适合网页嵌入。",
            "-i \"{input}\" -c:v libvpx-vp9 -crf {quality} -b:v 0 -row-mt 1 -c:a libopus -b:a {audio_bitrate} \"{output}\"",
            "webm",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "quality", Label = "CRF 质量", Kind = TemplateParameterKind.Select, DefaultValue = "32",
                    Options = [new("24", "24 · 高画质"), new("32", "32 · 均衡"), new("40", "40 · 小体积")]
                },
                audioBitrate
            ],
            tags: ["webm", "vp9"]);

        Add("视频转码", "转换为 AV1（SVT-AV1，体积最小)",
            "压缩率最高的开放格式，编码速度较慢但体积最小。需要 FFmpeg 编译了 libsvtav1。",
            "-i \"{input}\" -c:v libsvtav1 -crf {quality} -preset {preset} -c:a aac -b:a {audio_bitrate} " +
            "-movflags +faststart \"{output}\"",
            "mp4",
            targetCodec: "av1",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "quality", Label = "CRF 质量", Kind = TemplateParameterKind.Select, DefaultValue = "32",
                    Options = [new("24", "24 · 高画质"), new("32", "32 · 均衡"), new("40", "40 · 小体积")]
                },
                new TemplateParameter
                {
                    Name = "preset", Label = "编码速度", Kind = TemplateParameterKind.Select, DefaultValue = "8",
                    Description = "数字越大越快、压缩率越低。",
                    Options = [new("4", "4 · 最慢/最小"), new("8", "8 · 均衡（推荐）"), new("12", "12 · 快")]
                },
                audioBitrate
            ],
            tags: ["av1", "mp4", "压缩"]);

        Add("视频转码", "转换为 MOV（ProRes 422，剪辑友好）",
            "生成适合 Final Cut / Premiere 剪辑的中间码率素材，体积较大。",
            "-i \"{input}\" -c:v prores_ks -profile:v 3 -vendor apl0 -pix_fmt yuv422p10le " +
            "-c:a pcm_s16le \"{output}\"",
            "mov",
            targetCodec: "prores",
            tags: ["mov", "prores", "剪辑"]);

        Add("视频转码", "转换为 GIF 动图",
            "生成高质量调色板的 GIF，默认限制在 480 宽、12fps。",
            "-i \"{input}\" -vf \"fps={fps},scale={width}:-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -loop 0 \"{output}\"",
            "gif",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "fps", Label = "帧率", Kind = TemplateParameterKind.Select, DefaultValue = "12",
                    Options = [new("8", "8 fps · 最小"), new("12", "12 fps · 推荐"), new("15", "15 fps"), new("25", "25 fps · 最流畅")]
                },
                new TemplateParameter
                {
                    Name = "width", Label = "宽度", Kind = TemplateParameterKind.Select, DefaultValue = "480",
                    Description = "高度按比例自动计算且保证为偶数。",
                    Options = [new("320", "320 px"), new("480", "480 px"), new("640", "640 px"), new("800", "800 px")]
                }
            ],
            tags: ["gif", "动图"]);

        // ── 硬件加速 ────────────────────────────────────────────────
        Add("硬件加速", "硬件加速 · H.264（自动挑选可用编码器）",
            "自动使用本机最合适的 H.264 硬件编码器；模板会带上对应编码器的正确质量参数。",
            "-i \"{input}\" -c:v {encoder} {quality_args} {preset_args} {hwupload_args} " +
            "-c:a {audio_encoder} -b:a {audio_bitrate} -movflags +faststart \"{output}\"",
            "mp4",
            targetCodec: "h264",
            hardwareMode: HardwareAccelerationMode.Auto,
            parameters: [qualityAuto, audioBitrate],
            tags: ["硬件加速", "h264"]);

        Add("硬件加速", "硬件加速 · H.265（自动挑选可用编码器）",
            "自动使用本机最合适的 H.265 硬件编码器，画质/体积比优于 H.264。",
            "-i \"{input}\" -c:v {encoder} {quality_args} {preset_args} {hwupload_args} " +
            "-c:a {audio_encoder} -b:a {audio_bitrate} -tag:v hvc1 -movflags +faststart \"{output}\"",
            "mp4",
            targetCodec: "hevc",
            hardwareMode: HardwareAccelerationMode.Auto,
            parameters: [qualityAuto, audioBitrate],
            tags: ["硬件加速", "hevc"]);

        AddHardwareFamily(Add, "NVIDIA NVENC", "nvenc", "NVIDIA 显卡");
        AddHardwareFamily(Add, "Intel Quick Sync", "qsv", "Intel 核显 / 独显");
        AddHardwareFamily(Add, "AMD AMF", "amf", "AMD 显卡");
        AddHardwareFamily(Add, "Apple VideoToolbox", "videotoolbox", "Apple Silicon / macOS");
        AddHardwareFamily(Add, "Linux VA-API", "vaapi", "Linux 下的 Intel / AMD 显卡");

        // ── 视频压缩 ────────────────────────────────────────────────
        Add("视频压缩", "压缩 · 高画质（CRF 18)",
            "肉眼几乎无损失，体积约为原文件的一半。",
            "-i \"{input}\" -c:v libx264 -crf 18 -preset slow -c:a aac -b:a 192k -movflags +faststart \"{output}\"",
            "mp4", tags: ["压缩"]);

        Add("视频压缩", "压缩 · 均衡（CRF 23)",
            "在画质和体积之间取得平衡，适合大多数场景。",
            "-i \"{input}\" -c:v libx264 -crf 23 -preset medium -c:a aac -b:a 128k -movflags +faststart \"{output}\"",
            "mp4", tags: ["压缩"]);

        Add("视频压缩", "压缩 · 小体积（CRF 28)",
            "明显缩小体积，适合手机观看或分享。",
            "-i \"{input}\" -c:v libx264 -crf 28 -preset fast -c:a aac -b:a 96k -movflags +faststart \"{output}\"",
            "mp4", tags: ["压缩"]);

        Add("视频压缩", "压缩 · 限制文件大小",
            "按目标体积限制输出文件大小，适合有上传大小限制的场景。",
            "-i \"{input}\" -c:v libx264 -preset fast -crf 26 -maxrate {maxrate} -bufsize {bufsize} " +
            "-fs {maxsize}M -c:a aac -b:a 96k -movflags +faststart \"{output}\"",
            "mp4",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "maxsize", Label = "目标大小 (MB)", Kind = TemplateParameterKind.Number,
                    DefaultValue = "50", Min = 1, Max = 100000,
                    Description = "达到该大小后 FFmpeg 会停止写入。"
                },
                new TemplateParameter
                {
                    Name = "maxrate", Label = "最大码率", Kind = TemplateParameterKind.Select, DefaultValue = "1500k",
                    Options = [new("800k", "800 kbps"), new("1500k", "1500 kbps"), new("3000k", "3000 kbps"), new("6000k", "6000 kbps")]
                },
                new TemplateParameter
                {
                    Name = "bufsize", Label = "缓冲区", Kind = TemplateParameterKind.Select, DefaultValue = "3000k",
                    Options = [new("1600k", "1600 kbps"), new("3000k", "3000 kbps"), new("6000k", "6000 kbps"), new("12000k", "12000 kbps")]
                }
            ],
            tags: ["压缩", "体积限制"]);

        // ── 分辨率与画面 ────────────────────────────────────────────
        AddScaleTemplate(Add, "调整为 4K（3840×2160）", 3840, 2160);
        AddScaleTemplate(Add, "调整为 1080p（1920×1080）", 1920, 1080);
        AddScaleTemplate(Add, "调整为 720p（1280×720）", 1280, 720);
        AddScaleTemplate(Add, "调整为 480p（854×480）", 854, 480);

        Add("分辨率与画面", "缩放 · 指定宽度（高度自适应）",
            "只指定宽度，高度按原始比例自动计算，并保证为偶数。",
            "-i \"{input}\" -vf \"scale={width}:-2:flags=lanczos\" -c:v libx264 -crf 23 -c:a aac -b:a 128k " +
            "-movflags +faststart \"{output}\"",
            "mp4",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "width", Label = "目标宽度 (px)", Kind = TemplateParameterKind.Select, DefaultValue = "1280",
                    Options = [new("640", "640"), new("854", "854"), new("1280", "1280"), new("1920", "1920"), new("2560", "2560"), new("3840", "3840")]
                }
            ],
            tags: ["缩放"]);

        Add("分辨率与画面", "裁剪 · 竖屏 9:16（1080×1920）",
            "居中裁剪为竖屏短视频比例，适合抖音 / 视频号 / Shorts。",
            "-i \"{input}\" -vf \"scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920\" " +
            "-c:v libx264 -crf 23 -c:a aac -b:a 128k -movflags +faststart \"{output}\"",
            "mp4", tags: ["竖屏", "裁剪"]);

        Add("分辨率与画面", "旋转 · 顺时针 90°",
            "把横屏视频旋转为竖屏（或反过来）。",
            "-i \"{input}\" -vf \"transpose=1\" -c:v libx264 -crf 23 -c:a aac -b:a 128k -movflags +faststart \"{output}\"",
            "mp4",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "angle", Label = "方向", Kind = TemplateParameterKind.Select, DefaultValue = "1",
                    Description = "对应 FFmpeg transpose 参数。",
                    Options = [new("1", "顺时针 90°"), new("2", "逆时针 90°")]
                }
            ],
            tags: ["旋转"]);

        Add("分辨率与画面", "变速 · 修改播放速度",
            "通过调整时间戳实现无音调变化的变速（视频不变调，音频会被重采样）。",
            "-i \"{input}\" -filter_complex \"[0:v]setpts=PTS/{speed}[v];[0:a]atempo={atempo}[a]\" " +
            "-map \"[v]\" -map \"[a]\" -c:v libx264 -crf 23 -c:a aac -b:a 128k \"{output}\"",
            "mp4",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "speed", Label = "速度倍率", Kind = TemplateParameterKind.Select, DefaultValue = "2",
                    Options = [new("0.5", "0.5× 慢放"), new("1.5", "1.5×"), new("2", "2× 快放"), new("4", "4×")],
                    Description = "大于 1 变快，小于 1 变慢。"
                },
                new TemplateParameter
                {
                    Name = "atempo", Label = "音频同步倍率", Kind = TemplateParameterKind.Select, DefaultValue = "2",
                    Description = "需要与速度倍率一致，音频允许的范围是 0.5~100。",
                    Options = [new("0.5", "0.5×"), new("1.5", "1.5×"), new("2", "2×"), new("4", "4×")]
                }
            ],
            tags: ["变速"]);

        Add("分辨率与画面", "去隔行 · 消除交错的横纹",
            "对老式摄像机/电视录制的隔行视频做反交错处理。",
            "-i \"{input}\" -vf \"yadif=1:-1:0\" -c:v libx264 -crf 20 -preset medium -c:a aac -b:a 128k " +
            "-movflags +faststart \"{output}\"",
            "mp4", tags: ["去隔行", "老视频"]);

        // ── 音频处理 ────────────────────────────────────────────────
        Add("音频处理", "音频 · 提取为 AAC / M4A",
            "提取音轨并转为 AAC，体积小、兼容性好。",
            "-i \"{input}\" -vn -map 0:a:0 -c:a aac -b:a {audio_bitrate} \"{output}\"",
            "m4a",
            inputFormats: ["mp4", "mkv", "avi", "mov", "webm", "flv", "ts", "wav", "flac", "m4a", "aac", "ogg", "wmv"],
            parameters: [audioBitrate], tags: ["音频", "aac"]);

        Add("音频处理", "音频 · 提取为 FLAC（无损）",
            "无损提取音轨，不损失任何音质。",
            "-i \"{input}\" -vn -map 0:a:0 -c:a flac -compression_level 8 \"{output}\"",
            "flac",
            inputFormats: ["mp4", "mkv", "avi", "mov", "webm", "flv", "ts", "wav", "m4a", "aac", "ogg", "wmv"],
            tags: ["音频", "无损"]);

        Add("音频处理", "音频 · 提取为 WAV（PCM 无损）",
            "未压缩 PCM，适合后续在音频软件里继续处理。",
            "-i \"{input}\" -vn -map 0:a:0 -c:a pcm_s16le -ar 48000 \"{output}\"",
            "wav",
            inputFormats: ["mp4", "mkv", "avi", "mov", "webm", "flv", "ts", "flac", "m4a", "aac", "ogg", "wmv"],
            tags: ["音频", "wav", "无损"]);

        Add("音频处理", "音频 · 提取为 Opus",
            "同码率下音质优于 MP3/AAC，适合网络传输。",
            "-i \"{input}\" -vn -map 0:a:0 -c:a libopus -b:a 128k -vbr on \"{output}\"",
            "opus",
            inputFormats: ["mp4", "mkv", "avi", "mov", "webm", "flv", "ts", "wav", "flac", "m4a", "aac", "ogg"],
            tags: ["音频", "opus"]);

        Add("音频处理", "音频 · 音量标准化（EBU R128）",
            "把整体响度标准化到 -16 LUFS，避免忽大忽小。",
            "-i \"{input}\" -vn -af \"loudnorm=I=-16:TP=-1.5:LRA=11\" -c:a aac -b:a 192k \"{output}\"",
            "m4a",
            inputFormats: ["mp4", "mkv", "avi", "mov", "webm", "flv", "ts", "wav", "flac", "m4a", "aac", "ogg", "mp3"],
            tags: ["音频", "响度"]);

        Add("音频处理", "视频 · 移除音轨（静音视频）",
            "彻底去掉音频流，只保留画面。",
            "-i \"{input}\" -an -c:v copy \"{output}\"",
            "mp4", tags: ["静音", "去音轨"]);

        // ── 特殊处理 ────────────────────────────────────────────────
        Add("特殊处理", "画面 · 添加图片水印",
            "在右下角叠加一张 PNG/JPG 水印，可通过参数调整位置与透明度。",
            "-i \"{input}\" -i \"{watermark}\" -filter_complex " +
            "\"[1:v]format=rgba,colorchannelmixer=aa={opacity}[wm];[0:v][wm]overlay={position}\" " +
            "-c:v libx264 -crf 23 -preset medium -c:a copy \"{output}\"",
            "mp4",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "watermark", Label = "水印图片路径", Kind = TemplateParameterKind.Text,
                    DefaultValue = string.Empty, Description = "填写 PNG/JPG 的完整路径。"
                },
                new TemplateParameter
                {
                    Name = "position", Label = "水印位置", Kind = TemplateParameterKind.Select,
                    DefaultValue = "W-w-20:H-h-20",
                    Options =
                    [
                        new("W-w-20:H-h-20", "右下角"),
                        new("20:20", "左上角"),
                        new("W-w-20:20", "右上角"),
                        new("20:H-h-20", "左下角"),
                        new("(W-w)/2:(H-h)/2", "居中"),
                    ]
                },
                new TemplateParameter
                {
                    Name = "opacity", Label = "不透明度", Kind = TemplateParameterKind.Select, DefaultValue = "1",
                    Options = [new("0.3", "30%"), new("0.5", "50%"), new("0.8", "80%"), new("1", "不透明")]
                }
            ],
            tags: ["水印", "叠加"]);

        Add("特殊处理", "画面 · 烧录字幕（硬字幕）",
            "把字幕永久烧进画面，任何播放器都能显示。支持 srt/ass。" +
            "需要 FFmpeg 编译时启用 libass（Homebrew / winget / gyan.dev 的完整构建都有，部分精简构建没有）。",
            "-i \"{input}\" -vf \"subtitles=filename='{subtitle}':force_style='FontSize={fontsize}'\" " +
            "-c:v libx264 -crf 23 -preset medium -c:a copy \"{output}\"",
            "mp4",
            requiredFilters: ["subtitles"],
            parameters:
            [
                new TemplateParameter
                {
                    Name = "subtitle", Label = "字幕文件路径", Kind = TemplateParameterKind.Text,
                    DefaultValue = string.Empty,
                    Description = "srt / ass 文件的完整路径。路径中不要包含单引号。"
                },
                new TemplateParameter
                {
                    Name = "fontsize", Label = "字号", Kind = TemplateParameterKind.Select, DefaultValue = "24",
                    Options = [new("16", "小"), new("24", "中"), new("32", "大"), new("40", "特大")]
                }
            ],
            tags: ["字幕", "硬字幕"]);

        Add("特殊处理", "画面 · 按时间裁剪片段",
            "截取视频中的一段，起止时间使用 时:分:秒 格式。",
            "-ss {start} -i \"{input}\" -t {duration} -c:v libx264 -crf 20 -preset medium -c:a aac -b:a 128k " +
            "-movflags +faststart \"{output}\"",
            "mp4",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "start", Label = "开始时间", Kind = TemplateParameterKind.Text, DefaultValue = "00:00:00",
                    Description = "格式 时:分:秒，例如 00:01:30。"
                },
                new TemplateParameter
                {
                    Name = "duration", Label = "时长", Kind = TemplateParameterKind.Text, DefaultValue = "00:00:30",
                    Description = "格式 时:分:秒，例如 00:00:30。"
                }
            ],
            tags: ["裁剪", "剪辑"]);

        Add("特殊处理", "画面 · 导出为图片序列（JPG）",
            "按指定帧率抽帧导出为一组 JPG。文件名会自动带上 4 位序号，" +
            "例如输出名填 result 会生成 result_0001.jpg、result_0002.jpg ……",
            "-i \"{input}\" -vf fps={fps} -q:v {jpgquality} \"{output_pattern}\"",
            "jpg",
            parameters:
            [
                new TemplateParameter
                {
                    Name = "fps", Label = "抽帧频率", Kind = TemplateParameterKind.Select, DefaultValue = "1",
                    Options = [new("0.5", "每 2 秒 1 张"), new("1", "每秒 1 张"), new("2", "每秒 2 张"), new("5", "每秒 5 张")]
                },
                new TemplateParameter
                {
                    Name = "jpgquality", Label = "图片质量", Kind = TemplateParameterKind.Select, DefaultValue = "3",
                    Description = "数值越小画质越高（JPEG qscale）。",
                    Options = [new("2", "高"), new("3", "中（推荐）"), new("8", "小")]
                }
            ],
            tags: ["抽帧", "图片"]);

        Add("特殊处理", "画面 · 生成封面缩略图",
            "从指定时间点截取一张图片作为视频封面。",
            "-ss {seek} -i \"{input}\" -frames:v 1 -vf \"scale={width}:-2\" -q:v 2 -pix_fmt yuvj420p \"{output}\"",
            "jpg",
            parameters:
            [
                new TemplateParameter
                {
                    // 不能叫 time：{time} 是内置占位符（当前时间 HHmmss）
                    Name = "seek", Label = "截取时间", Kind = TemplateParameterKind.Text, DefaultValue = "00:00:01",
                    Description = "格式 时:分:秒，例如 00:00:05。"
                },
                new TemplateParameter
                {
                    Name = "width", Label = "宽度 (px)", Kind = TemplateParameterKind.Select, DefaultValue = "1280",
                    Options = [new("640", "640"), new("1280", "1280"), new("1920", "1920")]
                }
            ],
            tags: ["封面", "缩略图"]);

        Add("特殊处理", "网络优化 · 仅重建索引（faststart）",
            "不动画面和声音，只把索引移到文件开头，让视频可以先播后下。",
            "-i \"{input}\" -c copy -movflags +faststart \"{output}\"",
            "mp4",
            inputFormats: ["mp4", "mov", "m4v"],
            tags: ["faststart", "网络", "无损"]);

        Add("特殊处理", "网络优化 · 无损调整时间戳（修正音画不同步）",
            "重新生成时间戳，修复某些录制工具产生的音画不同步问题。",
            "-i \"{input}\" -c copy -fflags +genpts -avoid_negative_ts make_zero \"{output}\"",
            "mp4", tags: ["时间戳", "音画同步", "无损"]);

        Add("特殊处理", "字幕 · 封装为可选字幕轨（不重新编码）",
            "把字幕作为可开关的字幕轨封装进 MP4（mov_text）。速度极快、画质无损，" +
            "而且不依赖 libass，任何 FFmpeg 构建都能用。播放器里可以自由开关字幕。",
            "-i \"{input}\" -i \"{subtitle}\" -map 0 -map 1:0 -c copy -c:s mov_text " +
            "-movflags +faststart \"{output}\"",
            "mp4",
            inputFormats: ["mp4", "mkv", "mov", "m4v", "webm", "ts"],
            parameters:
            [
                new TemplateParameter
                {
                    Name = "subtitle", Label = "字幕文件路径", Kind = TemplateParameterKind.Text,
                    DefaultValue = string.Empty, Description = "srt / ass 文件的完整路径。"
                }
            ],
            tags: ["字幕", "软字幕", "无损"]);

        Add("特殊处理", "多音轨 · 保留全部音轨与字幕",
            "把所有音轨和字幕轨一起封装到 MKV，不做任何重新编码，适合归档原始素材。",
            "-i \"{input}\" -map 0 -c copy \"{output}\"",
            "mkv",
            inputFormats: ["mp4", "mkv", "mov", "avi", "ts", "webm", "flv", "m4v"],
            tags: ["多音轨", "归档", "无损"]);

        Add("音频处理", "音频 · 直接复制音轨（不重新编码）",
            "只把音轨取出来重新封装，不做任何编码，速度和音质都是最优。",
            "-i \"{input}\" -vn -map 0:a:0 -c:a copy \"{output}\"",
            "m4a",
            inputFormats: ["mp4", "mkv", "mov", "flac", "wav", "ogg", "m4a", "aac", "ts"],
            tags: ["音频", "无损", "快速"]);

        return templates;
    }

    /// <summary>生成某一硬件族的 H.264 / H.265 模板（在 CreateAll 内部作为局部函数调用）</summary>
    private static void AddHardwareFamily(
        Action<string, string, string, string, string, string[]?, string?, HardwareAccelerationMode, string?, List<TemplateParameter>?, string[]?, bool, string[]?> add,
        string displayFamily, string family, string vendorDescription)
    {
        var hardQuality = new TemplateParameter
        {
            Name = "quality",
            Label = "质量",
            Description = "留空使用该编码器的推荐默认值。",
            Kind = TemplateParameterKind.Select,
            DefaultValue = string.Empty,
            Options =
            [
                new("", "自动（推荐）"),
                new("20", "高画质"),
                new("24", "均衡"),
                new("28", "小体积"),
                new("32", "极小体积"),
            ]
        };

        var audio = new TemplateParameter
        {
            Name = "audio_bitrate",
            Label = "音频码率",
            Kind = TemplateParameterKind.Select,
            DefaultValue = "128k",
            Options = [new("96k", "96 kbps"), new("128k", "128 kbps"), new("192k", "192 kbps"), new("256k", "256 kbps")]
        };

        add("硬件加速", $"{displayFamily} · H.264",
            $"使用 {vendorDescription} 的 {displayFamily} 硬件编码 H.264，速度快、CPU 占用低。",
            "-i \"{input}\" -c:v {encoder} {quality_args} {preset_args} {hwupload_args} " +
            "-c:a {audio_encoder} -b:a {audio_bitrate} -movflags +faststart \"{output}\"",
            "mp4", null, "h264", HardwareAccelerationMode.Required, family, [hardQuality, audio],
            ["硬件加速", "h264", family], false, null);

        add("硬件加速", $"{displayFamily} · H.265 / HEVC",
            $"使用 {vendorDescription} 的 {displayFamily} 硬件编码 H.265，画质/体积比更好。",
            "-i \"{input}\" -c:v {encoder} {quality_args} {preset_args} {hwupload_args} " +
            "-c:a {audio_encoder} -b:a {audio_bitrate} -tag:v hvc1 -movflags +faststart \"{output}\"",
            "mp4", null, "hevc", HardwareAccelerationMode.Required, family, [hardQuality, audio],
            ["硬件加速", "hevc", family], false, null);
    }

    /// <summary>生成一个分辨率缩放模板</summary>
    private static void AddScaleTemplate(
        Action<string, string, string, string, string, string[]?, string?, HardwareAccelerationMode, string?, List<TemplateParameter>?, string[]?, bool, string[]?> add,
        string name, int width, int height)
    {
        add("分辨率与画面", name,
            $"等比缩放到不超过 {width}×{height}，必要时用黑边补齐，保证画面不变形。",
            $"-i \"{{input}}\" -vf \"scale={width}:{height}:force_original_aspect_ratio=decrease," +
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1\" " +
            "-c:v libx264 -crf 23 -preset medium -c:a aac -b:a 128k -movflags +faststart \"{output}\"",
            "mp4", null, "h264", HardwareAccelerationMode.None, null, null,
            ["缩放", $"{height}p"], false, null);
    }
}

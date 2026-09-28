using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>
/// 单个编码器的参数画像。所有选项都会在「硬件检测」阶段用真实参数试跑验证，
/// 因此在某个平台上不可用的参数组合会被自动降级，而不是等到用户转换时才失败。
/// </summary>
public sealed record EncoderProfile
{
    /// <summary>FFmpeg 编码器名，如 h264_nvenc</summary>
    public required string Name { get; init; }

    /// <summary>界面显示名</summary>
    public required string DisplayName { get; init; }

    /// <summary>所属厂商/类型</summary>
    public required EncoderType Type { get; init; }

    /// <summary>族标识：nvenc / qsv / amf / videotoolbox / vaapi / vulkan / mf / software</summary>
    public required string Family { get; init; }

    /// <summary>目标编码格式：h264 / hevc / av1 / vp9 / prores / vp8</summary>
    public required string Codec { get; init; }

    /// <summary>是否为硬件编码器</summary>
    public bool IsHardware => Type != EncoderType.Software;

    /// <summary>质量参数（{q} 会被替换为质量数值）；null 表示该编码器不接受质量参数</summary>
    public string? QualityArgs { get; init; }

    /// <summary>默认质量数值</summary>
    public int DefaultQuality { get; init; } = 23;

    /// <summary>质量数值的有效范围说明，用于界面提示</summary>
    public string QualityHint { get; init; } = "数值越小画质越好";

    /// <summary>预设参数</summary>
    public string? PresetArgs { get; init; }

    /// <summary>
    /// 送入编码器前必须附加的滤镜（如 VAAPI 需要 format=nv12,hwupload）。
    /// null 表示编码器可直接接受系统内存帧。
    /// </summary>
    public string? UploadFilter { get; init; }

    /// <summary>按需附加的设备初始化参数（如 -vaapi_device /dev/dri/renderD128）</summary>
    public string? DeviceArgs { get; init; }

    /// <summary>输出容器附加参数（如 HEVC in MP4 需要 -tag:v hvc1）</summary>
    public string? MuxerArgs { get; init; }

    /// <summary>同编码格式内的优先级，越小越优先选用</summary>
    public int SortPreference { get; init; }

    /// <summary>该编码器仅在这些平台上存在</summary>
    public bool SupportsCurrentPlatform { get; init; } = true;
}

/// <summary>编码器目录：内置所有 FFmpeg 8/9 常见编码器的参数画像</summary>
public static class EncoderCatalog
{
    private static readonly List<EncoderProfile> All =
    [
        // ── NVIDIA NVENC ────────────────────────────────────────────────
        new()
        {
            Name = "h264_nvenc", DisplayName = "NVIDIA NVENC H.264", Type = EncoderType.Nvenc,
            Family = "nvenc", Codec = "h264", SortPreference = 10,
            QualityArgs = "-rc vbr -cq {q} -b:v 0", DefaultQuality = 24, QualityHint = "CQ 值，18(高)~32(小体积)",
            PresetArgs = "-preset p5 -tune hq",
            SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },
        new()
        {
            Name = "hevc_nvenc", DisplayName = "NVIDIA NVENC H.265/HEVC", Type = EncoderType.Nvenc,
            Family = "nvenc", Codec = "hevc", SortPreference = 10,
            QualityArgs = "-rc vbr -cq {q} -b:v 0", DefaultQuality = 28, QualityHint = "CQ 值，22(高)~35(小体积)",
            PresetArgs = "-preset p5 -tune hq",
            MuxerArgs = "-tag:v hvc1",
            SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },
        new()
        {
            Name = "av1_nvenc", DisplayName = "NVIDIA NVENC AV1", Type = EncoderType.Nvenc,
            Family = "nvenc", Codec = "av1", SortPreference = 10,
            QualityArgs = "-rc vbr -cq {q} -b:v 0", DefaultQuality = 30, QualityHint = "CQ 值，24(高)~38(小体积)",
            PresetArgs = "-preset p5 -tune hq",
            SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },

        // ── Intel Quick Sync Video ──────────────────────────────────────
        new()
        {
            Name = "h264_qsv", DisplayName = "Intel Quick Sync H.264", Type = EncoderType.Qsv,
            Family = "qsv", Codec = "h264", SortPreference = 20,
            QualityArgs = "-global_quality {q}", DefaultQuality = 24, QualityHint = "ICQ 值，18(高)~32(小体积)",
            PresetArgs = "-preset medium",
                    SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },
        new()
        {
            Name = "hevc_qsv", DisplayName = "Intel Quick Sync H.265/HEVC", Type = EncoderType.Qsv,
            Family = "qsv", Codec = "hevc", SortPreference = 20,
            QualityArgs = "-global_quality {q}", DefaultQuality = 28, QualityHint = "ICQ 值，22(高)~35(小体积)",
            PresetArgs = "-preset medium",
            MuxerArgs = "-tag:v hvc1",
                    SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },
        new()
        {
            Name = "av1_qsv", DisplayName = "Intel Quick Sync AV1", Type = EncoderType.Qsv,
            Family = "qsv", Codec = "av1", SortPreference = 20,
            QualityArgs = "-global_quality {q}", DefaultQuality = 30, QualityHint = "ICQ 值，24(高)~38(小体积)",
            PresetArgs = "-preset medium",
                    SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },
        new()
        {
            Name = "vp9_qsv", DisplayName = "Intel Quick Sync VP9", Type = EncoderType.Qsv,
            Family = "qsv", Codec = "vp9", SortPreference = 20,
            QualityArgs = "-global_quality {q}", DefaultQuality = 30,
            PresetArgs = "-preset medium",
                    SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },

        // ── AMD AMF ─────────────────────────────────────────────────────
        new()
        {
            Name = "h264_amf", DisplayName = "AMD AMF H.264", Type = EncoderType.Amf,
            Family = "amf", Codec = "h264", SortPreference = 30,
            QualityArgs = "-rc cqp -qp_i {q} -qp_p {q}", DefaultQuality = 24, QualityHint = "QP 值，18(高)~32(小体积)",
            PresetArgs = "-quality balanced",
            SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },
        new()
        {
            Name = "hevc_amf", DisplayName = "AMD AMF H.265/HEVC", Type = EncoderType.Amf,
            Family = "amf", Codec = "hevc", SortPreference = 30,
            QualityArgs = "-rc cqp -qp_i {q} -qp_p {q}", DefaultQuality = 28, QualityHint = "QP 值，22(高)~35(小体积)",
            PresetArgs = "-quality balanced",
            MuxerArgs = "-tag:v hvc1",
            SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },
        new()
        {
            Name = "av1_amf", DisplayName = "AMD AMF AV1", Type = EncoderType.Amf,
            Family = "amf", Codec = "av1", SortPreference = 30,
            QualityArgs = "-rc cqp -qp_i {q} -qp_p {q}", DefaultQuality = 30,
            PresetArgs = "-quality balanced",
            SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },

        // ── Apple VideoToolbox ──────────────────────────────────────────
        new()
        {
            Name = "h264_videotoolbox", DisplayName = "Apple VideoToolbox H.264", Type = EncoderType.VideoToolbox,
            Family = "videotoolbox", Codec = "h264", SortPreference = 5,
            QualityArgs = "-q:v {q}", DefaultQuality = 60, QualityHint = "质量 0~100，越大画质越好",
            PresetArgs = null,
            SupportsCurrentPlatform = AppPaths.IsMacOS,
        },
        new()
        {
            Name = "hevc_videotoolbox", DisplayName = "Apple VideoToolbox H.265/HEVC", Type = EncoderType.VideoToolbox,
            Family = "videotoolbox", Codec = "hevc", SortPreference = 5,
            QualityArgs = "-q:v {q}", DefaultQuality = 60, QualityHint = "质量 0~100，越大画质越好",
            PresetArgs = null,
            MuxerArgs = "-tag:v hvc1",
            SupportsCurrentPlatform = AppPaths.IsMacOS,
        },
        new()
        {
            Name = "prores_videotoolbox", DisplayName = "Apple VideoToolbox ProRes", Type = EncoderType.VideoToolbox,
            Family = "videotoolbox", Codec = "prores", SortPreference = 5,
            QualityArgs = null,
            PresetArgs = "-profile:v 3",
            SupportsCurrentPlatform = AppPaths.IsMacOS,
        },

        // ── Linux VA-API（Intel / AMD 通用）─────────────────────────────
        new()
        {
            Name = "h264_vaapi", DisplayName = "VA-API H.264 (Intel/AMD)", Type = EncoderType.Vaapi,
            Family = "vaapi", Codec = "h264", SortPreference = 15,
            QualityArgs = "-qp {q}", DefaultQuality = 24, QualityHint = "QP 值，18(高)~32(小体积)",
            PresetArgs = null,
            UploadFilter = "format=nv12,hwupload",
            DeviceArgs = "-vaapi_device /dev/dri/renderD128",
            SupportsCurrentPlatform = AppPaths.IsLinux,
        },
        new()
        {
            Name = "hevc_vaapi", DisplayName = "VA-API H.265/HEVC (Intel/AMD)", Type = EncoderType.Vaapi,
            Family = "vaapi", Codec = "hevc", SortPreference = 15,
            QualityArgs = "-qp {q}", DefaultQuality = 28, QualityHint = "QP 值，22(高)~35(小体积)",
            PresetArgs = null,
            UploadFilter = "format=nv12,hwupload",
            DeviceArgs = "-vaapi_device /dev/dri/renderD128",
            MuxerArgs = "-tag:v hvc1",
            SupportsCurrentPlatform = AppPaths.IsLinux,
        },
        new()
        {
            Name = "av1_vaapi", DisplayName = "VA-API AV1 (Intel/AMD)", Type = EncoderType.Vaapi,
            Family = "vaapi", Codec = "av1", SortPreference = 15,
            QualityArgs = "-qp {q}", DefaultQuality = 30,
            PresetArgs = null,
            UploadFilter = "format=nv12,hwupload",
            DeviceArgs = "-vaapi_device /dev/dri/renderD128",
            SupportsCurrentPlatform = AppPaths.IsLinux,
        },

        // ── Vulkan Video（跨厂商，FFmpeg 8+）────────────────────────────
        new()
        {
            Name = "h264_vulkan", DisplayName = "Vulkan Video H.264", Type = EncoderType.Vulkan,
            Family = "vulkan", Codec = "h264", SortPreference = 40,
            QualityArgs = "-qp {q}", DefaultQuality = 24,
            PresetArgs = null,
            SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },
        new()
        {
            Name = "hevc_vulkan", DisplayName = "Vulkan Video H.265/HEVC", Type = EncoderType.Vulkan,
            Family = "vulkan", Codec = "hevc", SortPreference = 40,
            QualityArgs = "-qp {q}", DefaultQuality = 28,
            PresetArgs = null,
            MuxerArgs = "-tag:v hvc1",
            SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },
        new()
        {
            Name = "av1_vulkan", DisplayName = "Vulkan Video AV1", Type = EncoderType.Vulkan,
            Family = "vulkan", Codec = "av1", SortPreference = 40,
            QualityArgs = "-qp {q}", DefaultQuality = 30,
            PresetArgs = null,
            SupportsCurrentPlatform = AppPaths.IsWindows || AppPaths.IsLinux,
        },

        // ── Windows Media Foundation ────────────────────────────────────
        new()
        {
            Name = "h264_mf", DisplayName = "Media Foundation H.264", Type = EncoderType.MediaFoundation,
            Family = "mf", Codec = "h264", SortPreference = 60,
            QualityArgs = "-rate_control quality -quality {q}", DefaultQuality = 70,
            PresetArgs = null,
            SupportsCurrentPlatform = AppPaths.IsWindows,
        },
        new()
        {
            Name = "hevc_mf", DisplayName = "Media Foundation H.265/HEVC", Type = EncoderType.MediaFoundation,
            Family = "mf", Codec = "hevc", SortPreference = 60,
            QualityArgs = "-rate_control quality -quality {q}", DefaultQuality = 70,
            PresetArgs = null,
            MuxerArgs = "-tag:v hvc1",
            SupportsCurrentPlatform = AppPaths.IsWindows,
        },

        // ── 软件编码（始终可用，作为兜底）────────────────────────────────
        new()
        {
            Name = "libx264", DisplayName = "软件编码 H.264 (x264)", Type = EncoderType.Software,
            Family = "software", Codec = "h264", SortPreference = 90,
            QualityArgs = "-crf {q}", DefaultQuality = 23, QualityHint = "CRF 值，18(高)~32(小体积)",
            PresetArgs = "-preset medium",
        },
        new()
        {
            Name = "libx265", DisplayName = "软件编码 H.265 (x265)", Type = EncoderType.Software,
            Family = "software", Codec = "hevc", SortPreference = 90,
            QualityArgs = "-crf {q}", DefaultQuality = 28, QualityHint = "CRF 值，22(高)~35(小体积)",
            PresetArgs = "-preset medium",
            MuxerArgs = "-tag:v hvc1",
        },
        new()
        {
            Name = "libsvtav1", DisplayName = "软件编码 AV1 (SVT-AV1)", Type = EncoderType.Software,
            Family = "software", Codec = "av1", SortPreference = 90,
            QualityArgs = "-crf {q} -b:v 0", DefaultQuality = 32, QualityHint = "CRF 值，24(高)~42(小体积)",
            PresetArgs = "-preset 8",
        },
        new()
        {
            Name = "libaom-av1", DisplayName = "软件编码 AV1 (libaom)", Type = EncoderType.Software,
            Family = "software", Codec = "av1", SortPreference = 95,
            QualityArgs = "-crf {q} -b:v 0", DefaultQuality = 32,
            PresetArgs = "-cpu-used 4",
        },
        new()
        {
            Name = "libvpx-vp9", DisplayName = "软件编码 VP9 (libvpx)", Type = EncoderType.Software,
            Family = "software", Codec = "vp9", SortPreference = 90,
            QualityArgs = "-crf {q} -b:v 0", DefaultQuality = 32,
            PresetArgs = "-row-mt 1",
        },
        new()
        {
            Name = "libvpx", DisplayName = "软件编码 VP8 (libvpx)", Type = EncoderType.Software,
            Family = "software", Codec = "vp8", SortPreference = 90,
            QualityArgs = "-crf {q} -b:v 1M", DefaultQuality = 10,
            PresetArgs = null,
        },
        new()
        {
            Name = "aac", DisplayName = "AAC 音频 (内置)", Type = EncoderType.Software,
            Family = "software", Codec = "aac", SortPreference = 90,
            QualityArgs = "-b:a {q}k", DefaultQuality = 192,
            PresetArgs = null,
        },
        new()
        {
            Name = "libmp3lame", DisplayName = "MP3 音频 (LAME)", Type = EncoderType.Software,
            Family = "software", Codec = "mp3", SortPreference = 90,
            QualityArgs = "-b:a {q}k", DefaultQuality = 192,
            PresetArgs = null,
        },
        new()
        {
            Name = "libopus", DisplayName = "Opus 音频", Type = EncoderType.Software,
            Family = "software", Codec = "opus", SortPreference = 90,
            QualityArgs = "-b:a {q}k", DefaultQuality = 128,
            PresetArgs = null,
        },
        new()
        {
            Name = "flac", DisplayName = "FLAC 无损音频", Type = EncoderType.Software,
            Family = "software", Codec = "flac", SortPreference = 90,
            QualityArgs = null,
            PresetArgs = null,
        },
    ];

    /// <summary>全部编码器画像</summary>
    public static IReadOnlyList<EncoderProfile> Profiles => All;

    /// <summary>查找编码器画像</summary>
    public static EncoderProfile? Find(string encoderName) =>
        All.FirstOrDefault(p => string.Equals(p.Name, encoderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>按族名找出该族所有编码器画像</summary>
    public static IEnumerable<EncoderProfile> ByFamily(string family) =>
        All.Where(p => string.Equals(p.Family, family, StringComparison.OrdinalIgnoreCase));

    /// <summary>按目标编码格式列出候选编码器（按优先级排序）</summary>
    public static IEnumerable<EncoderProfile> ByCodec(string codec) =>
        All.Where(p => string.Equals(p.Codec, codec, StringComparison.OrdinalIgnoreCase))
           .OrderBy(p => p.SortPreference);

    /// <summary>某个编码格式对应的软件编码器（兜底用）</summary>
    public static EncoderProfile? SoftwareFallback(string codec) =>
        All.Where(p => p.Type == EncoderType.Software &&
                       string.Equals(p.Codec, codec, StringComparison.OrdinalIgnoreCase))
           .OrderBy(p => p.SortPreference)
           .FirstOrDefault();

    /// <summary>把族名或编码器名规范化成「一组成员」</summary>
    public static IReadOnlyList<string> ExpandFamily(string nameOrFamily)
    {
        var byFamily = ByFamily(nameOrFamily).Select(p => p.Name).ToList();
        if (byFamily.Count > 0) return byFamily;
        return [nameOrFamily];
    }

    /// <summary>所有族的显示名</summary>
    public static string FamilyDisplayName(string family) => family.ToLowerInvariant() switch
    {
        "nvenc" => "NVIDIA NVENC",
        "qsv" => "Intel Quick Sync",
        "amf" => "AMD AMF",
        "videotoolbox" => "Apple VideoToolbox",
        "vaapi" => "Linux VA-API",
        "vulkan" => "Vulkan Video",
        "mf" => "Windows Media Foundation",
        "software" => "软件编码",
        _ => family
    };
}

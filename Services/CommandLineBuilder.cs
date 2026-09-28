using System.Text;
using System.Text.RegularExpressions;
using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>构建 FFmpeg 命令时的上下文</summary>
public sealed class CommandContext
{
    /// <summary>输入文件路径</summary>
    public string InputPath { get; set; } = string.Empty;

    /// <summary>输出文件路径</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>用户设置的模板参数值（键为占位符名）</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已解析的编码器画像（决定 {quality_args} 等）</summary>
    public EncoderProfile? Encoder { get; set; }

    /// <summary>音频编码器名</summary>
    public string AudioEncoder { get; set; } = "aac";

    /// <summary>质量数值（覆盖画像默认值）</summary>
    public int? Quality { get; set; }

    /// <summary>输入媒体信息（用于 {width}/{height} 等占位符）</summary>
    public MediaInfo? Media { get; set; }

    /// <summary>线程数（0 = 不指定）</summary>
    public int Threads { get; set; }

    /// <summary>硬件解码参数（如 "-hwaccel videotoolbox"），空表示不使用</summary>
    public string HardwareDecodeArgs { get; set; } = string.Empty;

    /// <summary>附加到命令末尾的全局参数</summary>
    public string GlobalArguments { get; set; } = string.Empty;
}

/// <summary>命令构建结果</summary>
public sealed record BuiltCommand
{
    /// <summary>经占位符替换后的原始命令行文本（用于展示/复制）</summary>
    public required string CommandLine { get; init; }

    /// <summary>实际传给进程的参数列表（不经过 shell，避免转义问题）</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>使用的编码器名</summary>
    public string? EncoderName { get; init; }

    /// <summary>构建过程中的告警</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>未被替换的占位符</summary>
    public IReadOnlyList<string> UnresolvedPlaceholders { get; init; } = [];
}

/// <summary>
/// 把模板文本构建为可直接交给 ProcessStartInfo.ArgumentList 的参数列表。
/// 关键设计：
/// <list type="bullet">
/// <item><b>先分词、后替换占位符</b>，因此包含空格或引号的路径不会破坏命令结构，也不会出现 shell 注入或双重转义。</item>
/// <item><b>参数组占位符</b>（<c>{quality_args}</c> / <c>{preset_args}</c> / <c>{hwupload_args}</c> 等）
/// 会展开成<b>多个参数</b>，而不是塞进一个参数里。否则 <c>-cq 24 -b:v 0</c>
/// 会被当成单个参数传给 FFmpeg 而报错。展开为空时该位置不会产生空参数。</item>
/// </list>
/// </summary>
public static class CommandLineBuilder
{
    private static readonly Regex PlaceholderRegex = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex WholePlaceholderRegex = new(@"^\{([A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.Compiled);

    /// <summary>
    /// 这些占位符代表「一组命令行参数」，展开后可能包含多个 token（也可能一个都没有）。
    /// 它们必须整块替换在 token 列表上，而不能只做字符串替换。
    /// </summary>
    public static readonly HashSet<string> ArgumentGroupPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "quality_args", "preset_args", "hwupload_args", "device_args", "muxer_args",
        "hwaccel", "global_args", "extra_args"
    };

    /// <summary>构建命令</summary>
    public static BuiltCommand Build(CommandTemplate template, CommandContext context)
    {
        var warnings = new List<string>();
        var unresolved = new List<string>();

        // 1) 先分词（此时占位符仍是普通字符）
        var tokens = Tokenize(template.CommandArgs);

        // 2) 合并硬件上传滤镜到已有的 -vf 中（VAAPI 等需要）
        if (context.Encoder?.UploadFilter is { Length: > 0 } uploadFilter)
        {
            tokens = MergeUploadFilter(tokens, uploadFilter, template.CommandArgs, warnings);
        }

        // 3) 在 token 级别替换占位符：参数组占位符展开为多个参数，空值直接消失
        var resolver = BuildResolver(context, unresolved);
        var arguments = new List<string>(tokens.Count + 4);

        foreach (var token in tokens)
        {
            var whole = WholePlaceholderRegex.Match(token);
            if (whole.Success)
            {
                var name = whole.Groups[1].Value;

                if (ArgumentGroupPlaceholders.Contains(name))
                {
                    if (resolver.TryGetValue(name, out var group) && !string.IsNullOrWhiteSpace(group))
                    {
                        arguments.AddRange(Tokenize(group));
                    }
                    continue; // 空值 → 不产生参数（旧实现会留下一个空字符串参数导致 FFmpeg 报错）
                }

                if (resolver.TryGetValue(name, out var single))
                {
                    if (single.Length > 0) arguments.Add(single);
                    continue;
                }

                unresolved.Add(name);
                arguments.Add(token);
                continue;
            }

            arguments.Add(PlaceholderRegex.Replace(token, m =>
            {
                var key = m.Groups[1].Value;

                if (ArgumentGroupPlaceholders.Contains(key))
                {
                    // 参数组占位符混在别的文本里：只能做文本替换，且必须提醒作者改用独立占位符
                    if (resolver.TryGetValue(key, out var group))
                    {
                        warnings.Add($"占位符 {{{key}}} 与其它文本写在了同一个参数里，" +
                                     "它会被当成一个整体传入，可能导致 FFmpeg 解析失败。" +
                                     "请把它单独写成一个参数（前后加空格）。");
                        return group;
                    }
                }

                if (resolver.TryGetValue(key, out var value)) return value;

                unresolved.Add(key);
                return m.Value;
            }));
        }

        // 补充硬件解码参数：模板里没有显式写 -hwaccel 时，按「优先使用硬件解码」设置
        // 自动注入到输入文件之前。模板若自己用了 {hwaccel} 占位符，则已在上一步展开。
        if (!string.IsNullOrWhiteSpace(context.HardwareDecodeArgs) &&
            !arguments.Any(a => a.StartsWith("-hwaccel", StringComparison.Ordinal)))
        {
            var decodeTokens = Tokenize(context.HardwareDecodeArgs);
            if (decodeTokens.Count > 0)
            {
                var inputIndex = arguments.IndexOf(context.InputPath);
                // 插到 -i 之前；找不到输入文件时放到最前面，效果相同
                var decodeInsertAt = inputIndex > 0 ? inputIndex - 1 : 0;
                arguments.InsertRange(decodeInsertAt, decodeTokens);
            }
        }

        // 补充设备参数（如 -vaapi_device）
        var deviceArgs = Expand(context.Encoder?.DeviceArgs, resolver, unresolved);
        if (!string.IsNullOrWhiteSpace(deviceArgs) && !arguments.Contains(Tokenize(deviceArgs).FirstOrDefault() ?? string.Empty))
        {
            var deviceTokens = Tokenize(deviceArgs);
            var insertAt = arguments.FindIndex(t => t.StartsWith('-') && t is not "-y" and not "-n");
            if (insertAt < 0) insertAt = 0;
            arguments.InsertRange(insertAt, deviceTokens);
        }

        // 补充容器参数（如 -tag:v hvc1）——仅当模板未显式指定 tag 时
        var muxerArgs = Expand(context.Encoder?.MuxerArgs, resolver, unresolved);
        if (!string.IsNullOrWhiteSpace(muxerArgs) && !arguments.Any(a => a.StartsWith("-tag", StringComparison.Ordinal)))
        {
            var muxerTokens = Tokenize(muxerArgs);
            var outputIndex = arguments.LastIndexOf(context.OutputPath);
            if (outputIndex < 0) outputIndex = arguments.Count;
            arguments.InsertRange(outputIndex, muxerTokens);
        }

        // 全局参数（用户配置）追加到输出文件之前
        if (!string.IsNullOrWhiteSpace(context.GlobalArguments))
        {
            var globalTokens = Tokenize(context.GlobalArguments);
            var outputIndex = arguments.LastIndexOf(context.OutputPath);
            if (outputIndex < 0) outputIndex = arguments.Count;
            arguments.InsertRange(outputIndex, globalTokens);
        }

        // 未解析的占位符去重
        var distinctUnresolved = unresolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctUnresolved.Count > 0)
        {
            warnings.Add($"以下占位符没有对应取值，将原样传给 FFmpeg，可能导致失败：{string.Join(", ", distinctUnresolved.Select(p => "{" + p + "}"))}");
        }

        var commandLine = RenderCommandLine(arguments);

        return new BuiltCommand
        {
            CommandLine = commandLine,
            Arguments = arguments,
            EncoderName = context.Encoder?.Name,
            Warnings = warnings,
            UnresolvedPlaceholders = distinctUnresolved
        };
    }

    /// <summary>把参数列表渲染成可读/可复制的命令行文本</summary>
    public static string RenderCommandLine(IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        foreach (var arg in arguments)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(QuoteForDisplay(arg));
        }
        return sb.ToString();
    }

    /// <summary>列出模板中使用到的所有占位符名</summary>
    public static IReadOnlyList<string> ExtractPlaceholders(string commandArgs) =>
        string.IsNullOrEmpty(commandArgs)
            ? []
            : PlaceholderRegex.Matches(commandArgs).Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>所有由系统提供的占位符（不需要模板声明参数）</summary>
    public static readonly HashSet<string> BuiltInPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "output", "output_pattern",
        "input_dir", "input_name", "input_filename", "input_ext",
        "output_dir", "output_name", "output_ext",
        "duration", "width", "height", "fps", "vcodec", "acodec", "sample_rate", "channels",
        "encoder", "audio_encoder", "crf", "quality", "q",
        "quality_args", "preset_args", "hwupload_args", "device_args", "muxer_args", "extra_args",
        "threads", "hwaccel", "global_args", "date", "time", "datetime", "random",
    };

    // ────────────────────────────────────────────────────────────────────
    // 分词
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 类 POSIX 分词：支持单引号、双引号与反斜杠转义。
    /// 模板里写的 <c>"..."</c> 只是为了让带空格的路径保持为一个参数，
    /// 分词后引号本身不会保留，因此不需要对外层再做转义。
    /// </summary>
    public static List<string> Tokenize(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var current = new StringBuilder();
        var hasToken = false;
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                else current.Append(c);
                continue;
            }

            if (inDouble)
            {
                if (c == '\\' && i + 1 < input.Length && (input[i + 1] == '"' || input[i + 1] == '\\'))
                {
                    current.Append(input[++i]);
                }
                else if (c == '"')
                {
                    inDouble = false;
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '\\' when i + 1 < input.Length:
                    current.Append(input[++i]);
                    hasToken = true;
                    break;
                case '\'':
                    inSingle = true;
                    hasToken = true;
                    break;
                case '"':
                    inDouble = true;
                    hasToken = true;
                    break;
                case ' ' or '\t' or '\r' or '\n':
                    if (hasToken || current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }
                    break;
                default:
                    current.Append(c);
                    hasToken = true;
                    break;
            }
        }

        if (inSingle || inDouble)
        {
            // 引号未闭合：把已有内容作为最后一个参数，避免整条命令丢失
            if (current.Length > 0 || hasToken) result.Add(current.ToString());
            return result;
        }

        if (hasToken || current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>为展示目的给参数加引号</summary>
    private static string QuoteForDisplay(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        var needsQuote = arg.Any(c => c is ' ' or '\t' or '"' or '\'' or ';' or '&' or '|' or '>' or '<');
        if (!needsQuote) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>VAAPI / Vulkan 等需要把帧上传到显存时，把上传滤镜并入已有的 -vf 链</summary>
    private static List<string> MergeUploadFilter(List<string> tokens, string uploadFilter, string templateText, List<string> warnings)
    {
        if (templateText.Contains("{hwupload_args}", StringComparison.OrdinalIgnoreCase))
        {
            // 模板自行控制上传滤镜，不自动注入
            return tokens;
        }

        var vfIndex = tokens.FindIndex(t => t is "-vf" or "-filter:v" or "-filter");
        if (vfIndex >= 0 && vfIndex + 1 < tokens.Count)
        {
            var chain = tokens[vfIndex + 1];
            if (!chain.Contains("hwupload", StringComparison.OrdinalIgnoreCase))
            {
                tokens[vfIndex + 1] = chain.TrimEnd(',') + "," + uploadFilter;
            }
            return tokens;
        }

        if (tokens.Any(t => t is "-filter_complex" or "-lavfi"))
        {
            warnings.Add($"当前编码器需要显存上传滤镜（{uploadFilter}），但模板使用了 -filter_complex/-lavfi，无法自动合并。" +
                         "请在模板中显式使用 {hwupload_args} 占位符。");
            return tokens;
        }

        // 没有滤镜时，在输出文件之前插入 -vf
        var outputIdx = tokens.FindIndex(t => t.StartsWith("-c:") || t.StartsWith("-c ") || t == "-c");
        var insertAt = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is "-c:v" or "-codec:v" or "-vcodec") { insertAt = i; break; }
        }
        if (insertAt < 0) insertAt = tokens.Count;
        tokens.InsertRange(insertAt, ["-vf", uploadFilter]);
        return tokens;
    }

    private static string Expand(string? text, Dictionary<string, string> resolver, List<string> unresolved)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return PlaceholderRegex.Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            if (resolver.TryGetValue(key, out var value)) return value;
            unresolved.Add(key);
            return m.Value;
        });
    }

    // ────────────────────────────────────────────────────────────────────
    // 占位符解析
    // ────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> BuildResolver(CommandContext context, List<string> unresolved)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var input = context.InputPath ?? string.Empty;
        var output = context.OutputPath ?? string.Empty;
        var encoder = context.Encoder;

        map["input"] = input;
        map["output"] = output;
        map["input_dir"] = SafePath(Path.GetDirectoryName(input));
        map["input_name"] = SafePath(Path.GetFileNameWithoutExtension(input));
        map["input_filename"] = SafePath(Path.GetFileName(input));
        map["input_ext"] = Path.GetExtension(input).TrimStart('.');
        map["output_dir"] = SafePath(Path.GetDirectoryName(output));
        map["output_name"] = SafePath(Path.GetFileNameWithoutExtension(output));
        map["output_ext"] = Path.GetExtension(output).TrimStart('.');
        // 图片序列等场景需要带序号的文件名模式，例如 out_0001.jpg
        map["output_pattern"] = string.IsNullOrEmpty(output)
            ? string.Empty
            : Path.Combine(
                Path.GetDirectoryName(output) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(output)}_%04d{Path.GetExtension(output)}");

        var media = context.Media;
        map["duration"] = media != null ? media.Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "0";
        map["width"] = media != null && media.Width > 0 ? media.Width.ToString() : string.Empty;
        map["height"] = media != null && media.Height > 0 ? media.Height.ToString() : string.Empty;
        map["fps"] = media != null && media.FrameRate > 0 ? media.FrameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        map["vcodec"] = media?.VideoCodec ?? string.Empty;
        map["acodec"] = media?.AudioCodec ?? string.Empty;
        map["sample_rate"] = media is { AudioSampleRate: > 0 } ? media.AudioSampleRate.ToString() : "44100";
        map["channels"] = media is { AudioChannels: > 0 } ? media.AudioChannels.ToString() : "2";

        map["encoder"] = encoder?.Name ?? "libx264";
        map["audio_encoder"] = context.AudioEncoder;
        map["threads"] = context.Threads > 0 ? context.Threads.ToString() : string.Empty;
        map["hwaccel"] = context.HardwareDecodeArgs;

        var defaultQuality = context.Quality ?? encoder?.DefaultQuality ?? 23;
        map["crf"] = defaultQuality.ToString();
        map["quality"] = defaultQuality.ToString();
        map["q"] = defaultQuality.ToString();

        // 先套用用户参数（空值表示「使用默认」，不覆盖内置占位符）
        foreach (var (key, value) in context.Parameters)
        {
            if (string.Equals(key, "input", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "output", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.IsNullOrEmpty(value)) continue;
            map[key] = value;
        }

        // 再根据最终的质量值展开编码器相关占位符，
        // 这样模板里声明一个 quality 参数就能真正驱动 {quality_args}
        var quality = int.TryParse(map["q"], out var parsedQuality) ? parsedQuality : defaultQuality;
        map["crf"] = quality.ToString();
        map["quality"] = quality.ToString();
        map["q"] = quality.ToString();

        map["quality_args"] = Expand(encoder?.QualityArgs, map, unresolved).Replace("{q}", quality.ToString());
        map["preset_args"] = Expand(encoder?.PresetArgs, map, unresolved);
        map["hwupload_args"] = string.IsNullOrEmpty(encoder?.UploadFilter)
            ? string.Empty
            : Expand(encoder.UploadFilter, map, unresolved);

        // 以下两项由 Build 统一处理，避免重复插入
        map["device_args"] = string.Empty;
        map["muxer_args"] = string.Empty;

        // 动态占位符
        var now = DateTime.Now;
        map["date"] = now.ToString("yyyyMMdd");
        map["time"] = now.ToString("HHmmss");
        map["datetime"] = now.ToString("yyyyMMdd_HHmmss");
        map["random"] = Guid.NewGuid().ToString("N")[..8];

        return map;
    }

    private static string SafePath(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // 目录分隔符在参数中无需转义，但去掉可能的尾部斜杠以便拼接
        return value;
    }
}

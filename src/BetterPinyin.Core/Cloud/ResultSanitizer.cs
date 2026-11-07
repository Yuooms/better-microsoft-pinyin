using System.Text;

namespace BetterPinyin.Core.Cloud;

/// <summary>
/// 清洗参数。
/// </summary>
/// <param name="MinLength">最短长度（按码位计）。</param>
/// <param name="MaxLength">最长长度（按码位计）。</param>
/// <remarks>
/// 「云联想」与「扩展云联想」共用同一份清洗结果，因此取较宽的上限；
/// 真正的长度约束由读音判定里的音节数完成，这里只做粗筛。
/// </remarks>
public readonly record struct SanitizeOptions(int MinLength, int MaxLength)
{
    /// <summary>默认（云联想）：2~8 字。</summary>
    public static SanitizeOptions Default { get; } = new(2, 8);

    /// <summary>扩展云联想：2~12 字，允许更长的机构名与长尾词。</summary>
    public static SanitizeOptions Extended { get; } = new(2, 12);
}

/// <summary>
/// 候选文本清洗。
/// </summary>
/// <remarks>
/// 云接口会返回整句搜索词（如「伊斯塔露 原神」）、英文、带数字的型号名、单字等，
/// 这些直接塞进输入法候选会非常吵。这里按固定顺序过滤，规则刻意保守：
/// 宁可少显示，也不要显示让用户困惑的东西。
/// </remarks>
public static class ResultSanitizer
{
    /// <summary>候选词最短长度。</summary>
    public const int MinLength = 2;

    /// <summary>候选词最长长度。</summary>
    public const int MaxLength = 8;

    /// <summary>
    /// 清洗候选列表。
    /// </summary>
    /// <param name="raw">原始候选文本。</param>
    /// <param name="query">用户输入串，用于剔除与输入完全相同的回显。</param>
    /// <param name="options">长度约束；传 null 使用默认值。</param>
    /// <param name="blocked">被用户遗忘的词，直接丢弃；传 null 表示不过滤。</param>
    /// <returns>清洗后的候选，保持原始顺序并去重。</returns>
    public static IReadOnlyList<string> Sanitize(
        IEnumerable<string> raw,
        string query,
        SanitizeOptions? options = null,
        IReadOnlySet<string>? blocked = null)
    {
        var limits = options ?? SanitizeOptions.Default;
        var result = new List<string>(16);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in raw)
        {
            var text = Normalize(item);
            if (text.Length == 0)
            {
                continue;
            }

            var length = CountRunes(text);
            if (length < limits.MinLength || length > limits.MaxLength)
            {
                continue;
            }

            if (!IsAllChinese(text))
            {
                continue;
            }

            if (string.Equals(text, query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seen.Add(text))
            {
                continue;
            }

            if (blocked is not null && blocked.Contains(text))
            {
                continue;
            }

            result.Add(text);
        }

        return result;
    }

    /// <summary>
    /// 规范化单条候选：去掉零宽字符、空白，并只保留第一个词。
    /// </summary>
    /// <param name="value">原始文本。</param>
    /// <returns>规范化后的文本。</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var span = value.AsSpan().Trim();

        // 零宽字符会让长度判断与去重失效，先剔除。
        Span<char> buffer = stackalloc char[span.Length];
        var length = 0;
        foreach (var ch in span)
        {
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\uFEFF')
            {
                continue;
            }

            buffer[length++] = ch;
        }

        var cleaned = new string(buffer[..length]).Trim();

        // 「伊斯塔露 原神」这类整句只取第一个词；全角空格同样处理。
        var spaceIndex = cleaned.AsSpan().IndexOfAny(' ', '\u3000');
        if (spaceIndex > 0)
        {
            cleaned = cleaned[..spaceIndex];
        }

        return cleaned.Trim();
    }

    /// <summary>
    /// 判断字符串是否全为汉字（含扩展 A、兼容区与扩展 B 及以后）。
    /// </summary>
    /// <param name="text">待判断文本。</param>
    /// <returns>全部是汉字返回 true。</returns>
    public static bool IsAllChinese(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsCjk(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>按 Unicode 码位计长度，扩展 B 区不按两个 char 计。</summary>
    /// <param name="text">文本。</param>
    /// <returns>码位数。</returns>
    public static int CountRunes(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private static bool IsCjk(int codePoint) => codePoint
        is >= 0x4E00 and <= 0x9FFF
        or >= 0x3400 and <= 0x4DBF
        or >= 0xF900 and <= 0xFAFF
        or >= 0x20000 and <= 0x2FA1F;
}

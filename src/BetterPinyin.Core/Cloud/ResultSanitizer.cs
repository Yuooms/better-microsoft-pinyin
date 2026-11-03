namespace BetterPinyin.Core.Cloud;

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
    /// <returns>清洗后的候选，保持原始顺序并去重。</returns>
    public static IReadOnlyList<string> Sanitize(IEnumerable<string> raw, string query)
    {
        var result = new List<string>(16);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in raw)
        {
            var text = Normalize(item);
            if (text.Length == 0)
            {
                continue;
            }

            if (text.Length < MinLength || text.Length > MaxLength)
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

        // 「伊斯塔露 原神」这类整句只取第一个词。
        var spaceIndex = cleaned.IndexOf(' ');
        if (spaceIndex > 0)
        {
            cleaned = cleaned[..spaceIndex];
        }

        return cleaned.Trim();
    }

    /// <summary>
    /// 判断字符串是否全为汉字（含扩展 A 区）。
    /// </summary>
    /// <param name="text">待判断文本。</param>
    /// <returns>全部是汉字返回 true。</returns>
    public static bool IsAllChinese(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            var isCjk = ch is >= '\u4E00' and <= '\u9FFF'
                        or >= '\u3400' and <= '\u4DBF'
                        or >= '\uF900' and <= '\uFAFF';
            if (!isCjk)
            {
                return false;
            }
        }

        return true;
    }
}

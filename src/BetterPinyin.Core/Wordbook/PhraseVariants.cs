using BetterPinyin.Core.Pinyin;

namespace BetterPinyin.Core.Wordbook;

/// <summary>
/// 自定义短语的「简拼变体」：同一条短语同时写一条全拼和一条首字母。
/// </summary>
/// <remarks>
/// <para>
/// 微软拼音的自定义短语只按「拼音」字段做纯字符串匹配：写 <c>yisitalu</c> 就只有打全拼才出，
/// 写 <c>ystl</c> 就只有打简拼才出，输入法自己不会从全拼推出简拼。实测（Win10 19045）确认：
/// 把 <c>ystl</c> 写进短语库后，打 <c>ystl</c> 第一位就是该词。
/// </para>
/// <para>
/// 所以这里给每条短语补一条首字母记录。两条记录共用同一个输出文本与位置，
/// 在词库页会合并成一行显示（<c>yisitalu · ystl</c>），删除与编辑也会一起处理。
/// </para>
/// </remarks>
public static class PhraseVariants
{
    /// <summary>
    /// 求一条全拼对应的简拼。切不出音节、或简拼与全拼相同（单音节词）时返回 null。
    /// </summary>
    /// <param name="pinyin">无调全拼，例如 <c>yisitalu</c>。</param>
    /// <param name="table">音节表；传 null 用默认表。</param>
    /// <returns>简拼；不值得单独写一条时返回 null。</returns>
    public static string? InitialsOf(string pinyin, SyllableTable? table = null)
    {
        if (string.IsNullOrWhiteSpace(pinyin))
        {
            return null;
        }

        table ??= SyllableTable.Default;

        // normalize: false 很关键：模糊音归一（n→l、f→h）是给简拼索引用的，
        // 当触发码写给输入法时会变成别的字母，用户按原字母反而打不出来。
        if (!table.TryGetInitials(pinyin, out var initials, normalize: false))
        {
            return null;
        }

        // 单音节（"wo" → "w"）的简拼只有一个字母，写进去会跟一大片输入抢候选，不值得。
        return initials.Length >= 2 && !string.Equals(initials, pinyin, StringComparison.Ordinal)
            ? initials
            : null;
    }

    /// <summary>
    /// 把一条短语展开成「全拼 + 简拼」两条。
    /// </summary>
    /// <param name="phrase">原短语。</param>
    /// <param name="withInitials">是否补写简拼。</param>
    /// <returns>要写入的记录；不补写时只含原短语。</returns>
    public static IReadOnlyList<MspyPhrase> Expand(MspyPhrase phrase, bool withInitials)
    {
        if (!withInitials)
        {
            return new[] { phrase };
        }

        var initials = InitialsOf(phrase.Pinyin);
        return initials is null
            ? new[] { phrase }
            : new[] { phrase, phrase with { Pinyin = initials } };
    }
}

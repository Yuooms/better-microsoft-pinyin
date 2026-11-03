namespace BetterPinyin.Core.Pinyin;

/// <summary>输入串与候选词读音的匹配方式。</summary>
public enum PinyinMatchKind
{
    /// <summary>不匹配。</summary>
    None = 0,

    /// <summary>音节数相等且逐音节完全匹配。</summary>
    Strict = 1,

    /// <summary>音节数相等，末音节只打了一部分（例如 <c>yisital</c> 对「伊斯塔露」）。</summary>
    StrictPartialTail = 2,

    /// <summary>输入串是各音节声母（简拼）。</summary>
    Abbreviation = 3,

    /// <summary>输入覆盖了候选的前若干个音节，候选还有多余音节。</summary>
    Extended = 4,

    /// <summary>词库不可用，无从校验，放行并交给其它规则过滤。</summary>
    Permissive = 5,
}

/// <summary>
/// 一次匹配的结果。
/// </summary>
/// <param name="Kind">匹配方式。</param>
/// <param name="LemmaSyllables">候选词的音节数。</param>
/// <param name="ExtraSyllables">仅 <see cref="PinyinMatchKind.Extended"/>：候选多出的音节数。</param>
public readonly record struct PinyinMatch(
    PinyinMatchKind Kind,
    int LemmaSyllables,
    int ExtraSyllables)
{
    /// <summary>不匹配。</summary>
    public static PinyinMatch None { get; } = new(PinyinMatchKind.None, 0, 0);

    /// <summary>能否进入「云联想（严格）」槽位。</summary>
    public bool IsStrict => Kind is PinyinMatchKind.Strict
        or PinyinMatchKind.StrictPartialTail
        or PinyinMatchKind.Abbreviation
        or PinyinMatchKind.Permissive;

    /// <summary>能否进入「扩展云联想」槽位。</summary>
    public bool IsExtended => Kind == PinyinMatchKind.Extended;
}

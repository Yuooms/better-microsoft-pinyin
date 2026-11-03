namespace BetterPinyin.Core.Pinyin;

/// <summary>
/// 词语读音字典。
/// </summary>
/// <remarks>
/// 读音格式为音节以 <c>'</c> 分隔的全拼，例如 <c>yi'si'ta'lu</c>。
/// 具体实现由内置词库提供；没有词库时使用 <see cref="NullPinyinDictionary"/>，
/// 此时拼音校验自动降级为"不校验"，其余过滤规则照常生效。
/// </remarks>
public interface IPinyinDictionary
{
    /// <summary>尝试取词语读音。</summary>
    /// <param name="text">词语。</param>
    /// <param name="reading">读音（音节以 <c>'</c> 分隔）。</param>
    /// <returns>取到返回 true。</returns>
    bool TryGetReading(string text, out string reading);
}

/// <summary>
/// 空字典：永远取不到读音。用于词库尚未就绪时的降级。
/// </summary>
public sealed class NullPinyinDictionary : IPinyinDictionary
{
    /// <summary>共享实例。</summary>
    public static NullPinyinDictionary Instance { get; } = new();

    /// <inheritdoc />
    public bool TryGetReading(string text, out string reading)
    {
        reading = string.Empty;
        return false;
    }
}

/// <summary>拼音匹配的方式。</summary>
public enum PinyinMatchKind
{
    /// <summary>不匹配。</summary>
    None = 0,

    /// <summary>输入串是全拼读音的前缀（允许最后一个音节不完整）。</summary>
    FullOrPrefix = 1,

    /// <summary>输入串恰好是每个音节的声母（简拼）。</summary>
    Abbreviation = 2,
}

/// <summary>
/// 校验「候选词的读音」是否与「用户输入的串」对应。
/// </summary>
/// <remarks>
/// 这是云联想的质量闸门：云端返回的词必须读得通，否则会把「输入 yisital
/// 却给出毫不相干的词」这种噪声塞进候选栏。
/// <para>
/// 两条规则：
/// <list type="number">
/// <item>全拼/未打完：输入串是完整读音的前缀，例如 <c>yisital</c> 对应 <c>yi'si'ta'lu</c>。</item>
/// <item>简拼：输入串每个字符分别是各音节的声母，且音节数相等，例如 <c>ystl</c>。</item>
/// </list>
/// </para>
/// <para>
/// 模糊音开启时，双方先做一次一致化的近似归一（zh→z、ch→c、sh→s、ng→n、n→l、f→h）。
/// 这是工程近似而非语言学建模：它让模糊音用户的输入能匹配上，代价是极少数近音词会被误放行。
/// </para>
/// </remarks>
public sealed class PinyinMatcher
{
    private readonly IPinyinDictionary _dictionary;

    /// <summary>创建匹配器。</summary>
    /// <param name="dictionary">读音字典，传 null 使用空字典（等于关闭读音校验）。</param>
    /// <param name="fuzzyEnabled">是否启用模糊音归一。</param>
    public PinyinMatcher(IPinyinDictionary? dictionary = null, bool fuzzyEnabled = true)
    {
        _dictionary = dictionary ?? NullPinyinDictionary.Instance;
        FuzzyEnabled = fuzzyEnabled;
    }

    /// <summary>是否启用模糊音归一。</summary>
    public bool FuzzyEnabled { get; }

    /// <summary>是否具备读音校验能力（词库可用时为 true）。</summary>
    public bool IsAvailable => _dictionary is not NullPinyinDictionary;

    /// <summary>
    /// 判断候选词是否符合输入串。
    /// </summary>
    /// <param name="query">用户输入串（小写字母）。</param>
    /// <param name="candidate">候选词。</param>
    /// <returns>匹配方式；不匹配返回 <see cref="PinyinMatchKind.None"/>。</returns>
    public PinyinMatchKind Match(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return PinyinMatchKind.None;
        }

        if (!_dictionary.TryGetReading(candidate, out var reading) || string.IsNullOrWhiteSpace(reading))
        {
            // 没有词库时无从校验，放行并交给其它规则过滤（长度、纯汉字、去重）。
            return PinyinMatchKind.FullOrPrefix;
        }

        var syllables = reading.Split('\'', StringSplitOptions.RemoveEmptyEntries);
        if (syllables.Length == 0)
        {
            return PinyinMatchKind.FullOrPrefix;
        }

        var normalizedQuery = Normalize(query.ToLowerInvariant());
        var normalizedFlat = Normalize(string.Concat(syllables).ToLowerInvariant());

        // 规则一：输入串是完整读音的前缀。
        if (normalizedQuery.Length <= normalizedFlat.Length &&
            normalizedFlat.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return PinyinMatchKind.FullOrPrefix;
        }

        // 规则二：输入串是各音节声母（简拼），且音节数必须相等。
        if (normalizedQuery.Length == syllables.Length)
        {
            var allMatched = true;
            for (var i = 0; i < syllables.Length; i++)
            {
                var initial = Normalize(syllables[i][..1].ToLowerInvariant());
                if (initial.Length == 0 || initial[0] != normalizedQuery[i])
                {
                    allMatched = false;
                    break;
                }
            }

            if (allMatched)
            {
                return PinyinMatchKind.Abbreviation;
            }
        }

        return PinyinMatchKind.None;
    }

    private string Normalize(string value)
    {
        if (!FuzzyEnabled || value.Length == 0)
        {
            return value;
        }

        // 顺序敏感：先合并翘舌音，再合并后鼻音，最后合并 n/l 与 f/h。
        var result = value
            .Replace("zh", "z", StringComparison.Ordinal)
            .Replace("ch", "c", StringComparison.Ordinal)
            .Replace("sh", "s", StringComparison.Ordinal)
            .Replace("ng", "n", StringComparison.Ordinal)
            .Replace('n', 'l')
            .Replace('f', 'h');

        return result;
    }
}

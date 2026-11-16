using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Pinyin;

namespace BetterPinyin.Core.Ranking;

/// <summary>
/// 一次查询最终要展示的内容：一条「云联想」加若干条「扩展云联想」。
/// </summary>
/// <param name="Strict">云联想（严格按音节数匹配），可能为空。</param>
/// <param name="Extended">扩展云联想，按 Bing 原序排列。</param>
/// <param name="LocalCandidateCount">本地候选个数；读不到为 -1。</param>
/// <param name="LocalCountKnown">本地候选个数是否可信（UIA 读取质量为 Exact）。</param>
/// <param name="Diagnostic">一句话说明，直接进诊断面板。</param>
public sealed record SuggestionSet(
    CloudSuggestion? Strict,
    IReadOnlyList<CloudSuggestion> Extended,
    int LocalCandidateCount,
    bool LocalCountKnown,
    string Diagnostic)
{
    /// <summary>空结果。</summary>
    public static SuggestionSet Empty { get; } =
        new(null, Array.Empty<CloudSuggestion>(), -1, false, string.Empty);

    /// <summary>是否没有任何可展示项。</summary>
    public bool IsEmpty => Strict is null && Extended.Count == 0;

    /// <summary>可展示项总数。</summary>
    public int Count => (Strict is null ? 0 : 1) + Extended.Count;
}

/// <summary>
/// 分区参数。
/// </summary>
/// <param name="StrictEnabled">是否启用云联想。</param>
/// <param name="StrictDedupeLocal">云联想命中本地已有候选时是否隐藏。</param>
/// <param name="ExtendedEnabled">是否启用扩展云联想。</param>
/// <param name="ExtendedMaxCount">扩展最多几条（1~4）。</param>
/// <param name="MaxExtraSyllables">扩展词最多比输入多几个音节，防止把整句长尾词捞进来。</param>
public readonly record struct SuggestionSelectionOptions(
    bool StrictEnabled = true,
    bool StrictDedupeLocal = true,
    bool ExtendedEnabled = true,
    int ExtendedMaxCount = 3,
    int MaxExtraSyllables = 4)
{
    /// <summary>
    /// 全部取默认值。
    /// </summary>
    /// <remarks>
    /// 不要写 <c>new SuggestionSelectionOptions()</c>：结构体的无参构造会把所有字段清零，
    /// 两个开关都会变成关闭。必须用这个属性或显式传参。
    /// </remarks>
    public static SuggestionSelectionOptions Default { get; } = new(true, true, true, 3, 4);
}

/// <summary>
/// 把清洗后的云端候选分区成「云联想」与「扩展云联想」。
/// </summary>
/// <remarks>
/// 两条规则来自产品定义，不要随意放宽：
/// <list type="number">
/// <item>云联想只取 Bing 顺序里**第一条**整词匹配。它被本地候选去重掉时就置空，
/// 不再往下找——用户要的是「云端补一个我还没有的词」，而不是换一个同音字。</item>
/// <item>扩展云联想只收「输入是前缀、候选还有富余音节」的词，并排除已在本地候选里的，
/// 避免把用户已经能选到的词再列一遍。</item>
/// </list>
/// </remarks>
public static class SuggestionSelector
{
    /// <summary>
    /// 执行分区。
    /// </summary>
    /// <param name="query">用户输入串。</param>
    /// <param name="candidates">已清洗的云端候选，保持 Bing 原序。</param>
    /// <param name="localCandidates">输入法当前显示的候选，用于去重；拿不到传 null。</param>
    /// <param name="matcher">读音匹配器。</param>
    /// <param name="options">分区参数。</param>
    /// <param name="localCandidateCount">本地候选个数；未知传 -1。</param>
    /// <param name="localCountKnown">本地候选个数是否可信。</param>
    /// <param name="providerId">来源标识。</param>
    /// <param name="wordbook">
    /// 用户词库里与本次输入相关的词。它们只补空位：云端有整词匹配时严格槽仍归云端，
    /// 云端没有（或断网）时才顶上；扩展槽按顺序追加在云端之后。
    /// </param>
    /// <returns>分区结果。</returns>
    public static SuggestionSet Select(
        string query,
        IReadOnlyList<string> candidates,
        IReadOnlyList<string>? localCandidates,
        PinyinMatcher matcher,
        SuggestionSelectionOptions options,
        int localCandidateCount = -1,
        bool localCountKnown = false,
        string providerId = "bing",
        IReadOnlyList<string>? wordbook = null)
    {
        if (candidates.Count == 0 && (wordbook is null || wordbook.Count == 0))
        {
            return SuggestionSet.Empty;
        }

        var local = new HashSet<string>(StringComparer.Ordinal);
        if (localCandidates is not null)
        {
            foreach (var item in localCandidates)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    local.Add(item.Trim());
                }
            }
        }

        // 每个候选只算一次读音，严格槽与扩展槽共用，避免重复 DP。
        var matches = new PinyinMatch[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            matches[i] = matcher.Match(query, candidates[i]);
        }

        CloudSuggestion? strict = null;
        var strictDeduped = false;

        if (options.StrictEnabled)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!matches[i].IsStrict)
                {
                    continue;
                }

                var text = candidates[i];
                if (options.StrictDedupeLocal && local.Contains(text))
                {
                    // 用户要的词已经在候选里，再显示一遍没有意义，也不换下一个同音词。
                    strictDeduped = true;
                    break;
                }

                strict = Create(text, providerId, i, matches[i], query);
                break;
            }
        }

        // 云端没有整词匹配时，用用户词库顶上。这是「断网也能出自己学过的词」的唯一来源。
        if (options.StrictEnabled && strict is null && wordbook is not null)
        {
            for (var i = 0; i < wordbook.Count; i++)
            {
                var text = wordbook[i];
                if (options.StrictDedupeLocal && local.Contains(text))
                {
                    strictDeduped = true;
                    continue;
                }

                var match = matcher.Match(query, text);
                if (!match.IsStrict)
                {
                    continue;
                }

                strict = Create(text, WordbookProviderId, i, match, query);
                break;
            }
        }

        var extended = new List<CloudSuggestion>(Math.Min(options.ExtendedMaxCount, 4));
        if (options.ExtendedEnabled && options.ExtendedMaxCount > 0)
        {
            for (var i = 0; i < candidates.Count && extended.Count < options.ExtendedMaxCount; i++)
            {
                if (!matches[i].IsExtended || matches[i].ExtraSyllables > options.MaxExtraSyllables)
                {
                    continue;
                }

                var text = candidates[i];
                if (strict is not null && string.Equals(text, strict.Text, StringComparison.Ordinal))
                {
                    continue;
                }

                if (local.Contains(text))
                {
                    continue;
                }

                extended.Add(Create(text, providerId, i, matches[i], query));
            }
        }

        // 用户词库补充扩展槽：只在还没填满时追加，且不覆盖云端已有结果。
        if (options.ExtendedEnabled && wordbook is not null && extended.Count < options.ExtendedMaxCount)
        {
            for (var i = 0; i < wordbook.Count && extended.Count < options.ExtendedMaxCount; i++)
            {
                var text = wordbook[i];
                if (strict is not null && string.Equals(text, strict.Text, StringComparison.Ordinal))
                {
                    continue;
                }

                if (local.Contains(text) ||
                    extended.Exists(e => string.Equals(e.Text, text, StringComparison.Ordinal)))
                {
                    continue;
                }

                var match = matcher.Match(query, text);
                if (!match.IsExtended || match.ExtraSyllables > options.MaxExtraSyllables)
                {
                    continue;
                }

                extended.Add(Create(text, WordbookProviderId, i, match, query));
            }
        }

        var diagnostic = BuildDiagnostic(strict, strictDeduped, extended.Count, local.Count, options);
        return new SuggestionSet(strict, extended, localCandidateCount, localCountKnown, diagnostic);
    }

    /// <summary>
    /// 合并两次分区结果：严格槽取前者，扩展槽取后者。
    /// </summary>
    /// <param name="strictSource">提供严格槽的分区结果。</param>
    /// <param name="extendedSource">提供扩展槽的分区结果。</param>
    /// <returns>可直接展示的合并结果。</returns>
    /// <remarks>
    /// 双源模式下两个槽分别来自不同词源，各自只调用了一次 <see cref="Select"/>，
    /// 因此这里要自己去重（两次调用之间没有共享「严格槽已占用」的信息）。
    /// 诊断文案重新拼，避免出现「扩展=关闭；扩展=2」这种自相矛盾的拼接。
    /// </remarks>
    public static SuggestionSet Combine(SuggestionSet strictSource, SuggestionSet extendedSource)
    {
        var strict = strictSource.Strict;
        var extended = new List<CloudSuggestion>(extendedSource.Extended.Count);

        foreach (var item in extendedSource.Extended)
        {
            if (strict is not null && string.Equals(item.Text, strict.Text, StringComparison.Ordinal))
            {
                continue;
            }

            if (extended.Exists(e => string.Equals(e.Text, item.Text, StringComparison.Ordinal)))
            {
                continue;
            }

            extended.Add(item);
        }

        var localCount = strictSource.LocalCandidateCount >= 0
            ? strictSource.LocalCandidateCount
            : extendedSource.LocalCandidateCount;
        var localKnown = strictSource.LocalCountKnown || extendedSource.LocalCountKnown;
        var diagnostic = $"严格={strict?.Text ?? "无"}；扩展={extended.Count}；本地={localCount}";

        return new SuggestionSet(strict, extended, localCount, localKnown, diagnostic);
    }

    /// <summary>用户词库来源标识。</summary>
    public const string WordbookProviderId = "wordbook";

    private static CloudSuggestion Create(string text, string providerId, int index, in PinyinMatch match, string query)
    {
        var rankScore = 1.0 / (1.0 + (index * 0.6));
        var matchScore = match.Kind == PinyinMatchKind.Abbreviation ? 0.9 : 1.0;
        var lengthScore = text.Length >= 3 ? 1.0 : 0.6;
        var score = (0.55 * rankScore) + (0.25 * matchScore) + (0.20 * lengthScore);

        // Reading 记的是本次实际请求的拼音串：学进词库时要的是全拼，不是原始简拼。
        return new CloudSuggestion(text, providerId, index, Math.Round(score, 4), query);
    }

    private static string BuildDiagnostic(
        CloudSuggestion? strict,
        bool strictDeduped,
        int extendedCount,
        int localCount,
        SuggestionSelectionOptions options)
    {
        var strictText = !options.StrictEnabled
            ? "关闭"
            : strict is not null
                ? strict.Text
                : strictDeduped
                    ? "无（已在本地候选）"
                    : "无（云端无整词匹配）";

        var extendedText = options.ExtendedEnabled ? extendedCount.ToString() : "关闭";
        return $"严格={strictText}；扩展={extendedText}；本地={localCount}";
    }
}

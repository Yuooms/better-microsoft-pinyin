using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Pinyin;

namespace BetterPinyin.Core.Ranking;

/// <summary>
/// 把云端返回的原始候选整理成最终要展示的列表。
/// </summary>
/// <remarks>
/// 打分只做三件事：偏好来源排序靠前的结果、偏好读得通的读音、
/// 略微偏好更长的词（单字在本地候选里一定有，不需要云端补）。
/// 权重是工程取值，可通过设置调整上限，不建议在代码里频繁微调。
/// </remarks>
public sealed class CandidateRanker
{
    private readonly PinyinMatcher _matcher;

    /// <summary>创建排序器。</summary>
    /// <param name="matcher">拼音匹配器。</param>
    /// <param name="maxResults">最多返回多少条。</param>
    public CandidateRanker(PinyinMatcher? matcher = null, int maxResults = 3)
    {
        _matcher = matcher ?? new PinyinMatcher();
        MaxResults = Math.Clamp(maxResults, 1, 9);
    }

    /// <summary>最多返回条数。</summary>
    public int MaxResults { get; }

    /// <summary>
    /// 合并、过滤并排序。
    /// </summary>
    /// <param name="query">用户输入串。</param>
    /// <param name="candidates">已清洗的云端候选。</param>
    /// <param name="localCandidates">输入法当前显示的候选，用于去重；拿不到时传 null。</param>
    /// <param name="providerId">来源标识。</param>
    /// <returns>最终候选，已按分数降序排列。</returns>
    public IReadOnlyList<CloudSuggestion> Rank(
        string query,
        IReadOnlyList<string> candidates,
        IReadOnlyList<string>? localCandidates = null,
        string providerId = "bing")
    {
        if (candidates.Count == 0)
        {
            return Array.Empty<CloudSuggestion>();
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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scored = new List<CloudSuggestion>(candidates.Count);

        for (var index = 0; index < candidates.Count; index++)
        {
            var text = candidates[index];

            // 本地已有这个词，或者本地首选就是它——不必再显示，避免"云联想没有带来新信息"。
            if (local.Contains(text))
            {
                continue;
            }

            if (!seen.Add(text))
            {
                continue;
            }

            var matchKind = _matcher.Match(query, text);
            if (matchKind == PinyinMatchKind.None)
            {
                continue;
            }

            var rankScore = 1.0 / (1.0 + (index * 0.6));
            var matchScore = matchKind == PinyinMatchKind.Abbreviation ? 0.9 : 1.0;
            var lengthScore = text.Length >= 3 ? 1.0 : 0.6;

            var score = (0.55 * rankScore) + (0.25 * matchScore) + (0.20 * lengthScore);

            scored.Add(new CloudSuggestion(
                text,
                providerId,
                index,
                Math.Round(score, 4)));
        }

        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.ProviderRank.CompareTo(b.ProviderRank);
        });

        return scored.Count <= MaxResults
            ? scored
            : scored.GetRange(0, MaxResults);
    }
}

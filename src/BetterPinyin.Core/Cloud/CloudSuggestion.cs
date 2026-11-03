namespace BetterPinyin.Core.Cloud;

/// <summary>
/// 一条云联想候选（业务模型）。
/// </summary>
/// <param name="Text">候选词文本。</param>
/// <param name="ProviderId">来源标识，例如 <c>bing-osjson</c>。</param>
/// <param name="ProviderRank">在来源结果中的原始名次（从 0 开始）。</param>
/// <param name="Score">本项目的最终打分（0~1），越大越靠前。</param>
/// <param name="Reading">读音（已知时填写，未知为 null）。</param>
public sealed record CloudSuggestion(
    string Text,
    string ProviderId,
    int ProviderRank,
    double Score = 0,
    string? Reading = null)
{
    /// <summary>转换为跨层传输用的载荷。</summary>
    /// <returns>传输载荷。</returns>
    public Contracts.SuggestionPayload ToPayload() =>
        new(Text, Reading, ProviderId, ProviderRank, Score);
}

/// <summary>
/// 云联想词源。
/// </summary>
/// <remarks>
/// 实现方只负责"给定输入串，返回原始候选文本"，清洗、校验、排序、缓存都在外层完成，
/// 这样新增词源（例如第二个搜索引擎）时不需要重写任何质量逻辑。
/// </remarks>
public interface ICloudSuggestionProvider
{
    /// <summary>来源标识，用于日志与打分。</summary>
    string Id { get; }

    /// <summary>
    /// 查询联想词。
    /// </summary>
    /// <param name="query">输入串（通常是用户敲的拼音）。</param>
    /// <param name="cancellationToken">取消令牌；用户继续打字时应立即取消上一次查询。</param>
    /// <returns>原始候选文本，已按来源相关度排序；失败时返回空列表而不是抛异常。</returns>
    Task<IReadOnlyList<string>> GetSuggestionsAsync(string query, CancellationToken cancellationToken);
}

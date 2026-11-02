namespace BetterPinyin.Contracts;

/// <summary>
/// 一条云联想候选。跨进程传输时使用本类型，避免把业务模型泄漏到契约层。
/// </summary>
/// <param name="Text">候选词文本，已清洗、已通过拼音校验。</param>
/// <param name="Reading">该词的拼音读音，例如 <c>yi'si'ta'lu</c>；未知时为 null。</param>
/// <param name="ProviderId">来源标识，例如 <c>bing-osjson</c>。</param>
/// <param name="ProviderRank">在来源结果中的原始名次（从 0 开始）。</param>
/// <param name="Score">本项目的最终打分（0~1），用于排序。</param>
public sealed record SuggestionPayload(
    string Text,
    string? Reading,
    string ProviderId,
    int ProviderRank,
    double Score)
{
    /// <summary>作为展示文本时的兜底，确保不为空。</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Text) ? string.Empty : Text;
}

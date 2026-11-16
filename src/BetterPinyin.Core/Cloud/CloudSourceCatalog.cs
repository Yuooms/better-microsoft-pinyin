namespace BetterPinyin.Core.Cloud;

/// <summary>
/// 云联想来源清单。
/// </summary>
/// <remarks>
/// 「来源」分两层：家族（<c>bing</c> / <c>sogou</c>）由设置选择，
/// 端点（<c>bing-osjson</c> 等）由客户端内部决定。
/// 设置里只存家族 id，端点级信息随 <see cref="CloudProviderAnswer.SourceId"/> 返回。
/// </remarks>
public static class CloudSourceCatalog
{
    /// <summary>必应搜索建议。</summary>
    public const string BingId = "bing";

    /// <summary>搜狗网页输入法（二进制帧）。</summary>
    public const string SogouId = "sogou";

    /// <summary>默认来源。</summary>
    public const string DefaultId = BingId;

    /// <summary>
    /// 归一化设置里的来源取值。
    /// </summary>
    /// <param name="id">设置里读到的原始值，可能为空或已被用户改坏。</param>
    /// <returns>合法的来源 id。</returns>
    /// <remarks>
    /// 老配置没有这个键、用户手改过 settings.json、或将来删掉某个来源时，
    /// 一律回落到 Bing——它是默认值，也是永远可用的那一个。
    /// </remarks>
    public static string Normalize(string? id) =>
        string.Equals(id, SogouId, StringComparison.OrdinalIgnoreCase) ? SogouId : BingId;

    /// <summary>来源的显示名，用于设置页与诊断。</summary>
    /// <param name="id">来源 id。</param>
    /// <returns>中文显示名。</returns>
    public static string DisplayName(string? id) =>
        Normalize(id) == SogouId ? "搜狗" : "必应 Bing";
}

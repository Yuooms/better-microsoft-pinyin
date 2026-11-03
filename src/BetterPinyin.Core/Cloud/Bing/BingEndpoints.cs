namespace BetterPinyin.Core.Cloud.Bing;

/// <summary>
/// Bing 搜索建议的响应格式。
/// </summary>
public enum BingResponseFormat
{
    /// <summary>OpenSearch JSON：<c>["查询词", ["候选1", "候选2"]]</c>。</summary>
    OpenSearchJson = 0,

    /// <summary>搜索框 JSON：<c>{"AS":{"Results":[{"Suggests":[{"Txt":"候选"}]}]}}</c>。</summary>
    SearchBoxJson = 1,
}

/// <summary>
/// 一个 Bing 建议端点。
/// </summary>
/// <param name="Id">端点标识，用于日志与打分。</param>
/// <param name="Host">主机名，例如 <c>api.bing.com</c>。</param>
/// <param name="Path">路径，例如 <c>/osjson.aspx</c>。</param>
/// <param name="Format">响应格式，决定用哪个解析器。</param>
/// <param name="HasContentFilter">该端点是否带内容过滤（会削减弱相关结果，故相对降权）。</param>
/// <param name="Description">人类可读说明。</param>
public sealed record BingEndpoint(
    string Id,
    string Host,
    string Path,
    BingResponseFormat Format,
    bool HasContentFilter,
    string Description)
{
    /// <summary>
    /// 组装请求地址。
    /// </summary>
    /// <param name="query">输入串。</param>
    /// <param name="market">市场标识，例如 <c>zh-CN</c>。</param>
    /// <returns>完整 URL。</returns>
    public string BuildUrl(string query, string market)
    {
        var escaped = Uri.EscapeDataString(query);
        return Format == BingResponseFormat.OpenSearchJson
            ? $"https://{Host}{Path}?query={escaped}&market={Uri.EscapeDataString(market)}"
            : $"https://{Host}{Path}?type=cb&q={escaped}";
    }
}

/// <summary>
/// Bing 端点清单。
/// </summary>
/// <remarks>
/// 多端点并存是为了适配不同网络环境，而不是"国内一套、国外一套"的硬编码：
/// 运行时由探测结果决定优先级，代码里不出现任何地区判断。
/// </remarks>
public static class BingEndpointCatalog
{
    /// <summary>主源：OpenSearch 格式，结果最完整。</summary>
    public static BingEndpoint Primary { get; } = new(
        "bing-osjson",
        "api.bing.com",
        "/osjson.aspx",
        BingResponseFormat.OpenSearchJson,
        HasContentFilter: false,
        "Bing 主接口，返回最完整");

    /// <summary>备源：搜索框格式，与主源解析路径不同，可交叉验证。</summary>
    public static BingEndpoint Secondary { get; } = new(
        "bing-qsonhs",
        "api.bing.com",
        "/qsonhs.aspx",
        BingResponseFormat.SearchBoxJson,
        HasContentFilter: false,
        "Bing 备接口，解析路径不同");

    /// <summary>国内直连端点：实测延迟最低，但存在内容过滤。</summary>
    public static BingEndpoint China { get; } = new(
        "bing-cn",
        "cn.bing.com",
        "/qsonhs.aspx",
        BingResponseFormat.SearchBoxJson,
        HasContentFilter: true,
        "国内直连，延迟最低但结果经过过滤");

    /// <summary>海外兜底端点。</summary>
    public static BingEndpoint Global { get; } = new(
        "bing-www",
        "www.bing.com",
        "/qsonhs.aspx",
        BingResponseFormat.SearchBoxJson,
        HasContentFilter: false,
        "海外兜底");

    /// <summary>
    /// 按优先级排列的全部云端点。
    /// </summary>
    public static IReadOnlyList<BingEndpoint> CloudEndpoints { get; } = new[]
    {
        Primary,
        Secondary,
        China,
        Global,
    };
}

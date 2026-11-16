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
/// <param name="TimeoutMilliseconds">
/// 端点级超时，null 表示沿用全局设置。只能收紧、不能放宽：
/// 解析时与全局超时取较小值，避免个别端点把用户设定的上限顶开。
/// </param>
public sealed record BingEndpoint(
    string Id,
    string Host,
    string Path,
    BingResponseFormat Format,
    bool HasContentFilter,
    string Description,
    int? TimeoutMilliseconds = null)
{
    /// <summary>
    /// 算出本次请求实际使用的超时。
    /// </summary>
    /// <param name="clientTimeoutMilliseconds">全局单次请求超时。</param>
    /// <returns>端点级超时与全局超时中的较小值。</returns>
    /// <remarks>
    /// 取小值是有意的：端点级超时用来给「可疑端点」设一道更紧的闸，
    /// 不是用来突破用户在设置里定的上限。用户把全局超时调到 300ms，
    /// 末位端点也不该再赖到 500ms。
    /// </remarks>
    public int ResolveTimeout(int clientTimeoutMilliseconds) =>
        TimeoutMilliseconds is int own && own < clientTimeoutMilliseconds
            ? own
            : clientTimeoutMilliseconds;

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
/// <para>
/// 多端点并存是为了适配不同网络环境，而不是"国内一套、国外一套"的硬编码：
/// 运行时由探测结果决定优先级，代码里不出现任何地区判断。
/// </para>
/// <para>
/// 原首选端点 <c>api.bing.com/osjson.aspx</c> 自 2026-11 起恒返回 <c>204 No Content</c>：
/// 换参数、UA、Cookie、直连与代理、以及 <c>cn.bing.com</c> 均一样，而 qsonhs 正常。
/// 但无法确认这是永久下线还是阶段性故障，所以没有删除，而是降到末位兜底——
/// 正常情况下永远走不到它，真恢复了也能自动用上。
/// </para>
/// </remarks>
public static class BingEndpointCatalog
{
    /// <summary>首选端点：搜索框格式，当前唯一稳定的入口。</summary>
    public static BingEndpoint Secondary { get; } = new(
        "bing-qsonhs",
        "api.bing.com",
        "/qsonhs.aspx",
        BingResponseFormat.SearchBoxJson,
        HasContentFilter: false,
        "Bing 首选接口");

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
    /// 旧端点：2026-11 实测恒返回 204，降为最后兜底。
    /// </summary>
    /// <remarks>
    /// 放在链尾的代价只在"前三个端点全挂"时才会付出；它若恢复，下次查询会自然用上。
    /// 超时压到 500ms：这一档只在真正断网、前三个端点全超时的时候才会走到，
    /// 那时用户已经等了三个满超时，再给它完整的 1.5s 只会让人更久看不到候选。
    /// </remarks>
    public static BingEndpoint LegacyOpenSearch { get; } = new(
        "bing-osjson",
        "api.bing.com",
        "/osjson.aspx",
        BingResponseFormat.OpenSearchJson,
        HasContentFilter: false,
        "旧接口，2026-11 起返回 204，仅作最后兜底",
        TimeoutMilliseconds: 500);

    /// <summary>
    /// 按优先级排列的全部云端点。
    /// </summary>
    public static IReadOnlyList<BingEndpoint> CloudEndpoints { get; } = new[]
    {
        Secondary,
        China,
        Global,
        LegacyOpenSearch,
    };
}

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using BetterPinyin.Core.Cloud.Bing;
using BetterPinyin.Core.Cloud.Sogou;

namespace BetterPinyin.Core.Network;

/// <summary>
/// 单个端点的体检结果。
/// </summary>
/// <param name="Id">端点标识，例如 <c>bing-qsonhs</c> 或 <c>sogou</c>。</param>
/// <param name="DisplayName">界面上显示的名字。</param>
/// <param name="Reachable">是否能解析出结果（仅 HTTP 200 不算通过）。</param>
/// <param name="LatencyMilliseconds">耗时。</param>
/// <param name="ResultCount">解析出的候选条数。</param>
/// <param name="Error">失败原因，成功时为 null。</param>
/// <param name="HasContentFilter">该端点是否带内容过滤（仅 Bing 有这个概念）。</param>
public sealed record EndpointProbeResult(
    string Id,
    string DisplayName,
    bool Reachable,
    int LatencyMilliseconds,
    int ResultCount,
    string? Error,
    bool HasContentFilter = false);

/// <summary>
/// 端点体检。
/// </summary>
/// <remarks>
/// 刻意采用「真实查询」而不是 TCP 探测：网络通但接口被劫持、返回登录页、
/// 或返回结构变化时，只有真正解析一次才能发现。判定标准是"HTTP 200 且能解析出至少一条候选"。
/// </remarks>
public sealed class EndpointProbe
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private readonly HttpClient _httpClient;

    /// <summary>创建体检器。</summary>
    /// <param name="httpClient">共享 HTTP 客户端。</param>
    public EndpointProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 并发体检全部来源。
    /// </summary>
    /// <param name="sampleQuery">探测用的输入串，建议选一个各端点都有结果的常见词。</param>
    /// <param name="timeoutMilliseconds">单端点超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按「可用优先、耗时升序」排列的结果。</returns>
    public async Task<IReadOnlyList<EndpointProbeResult>> ProbeAllAsync(
        string sampleQuery = "weiruan",
        int timeoutMilliseconds = 2500,
        CancellationToken cancellationToken = default)
    {
        var tasks = BingEndpointCatalog.CloudEndpoints
            .Select(endpoint => ProbeAsync(endpoint, sampleQuery, timeoutMilliseconds, cancellationToken))
            .Append(ProbeSogouAsync(sampleQuery, timeoutMilliseconds, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OrderByDescending(r => r.Reachable).ThenBy(r => r.LatencyMilliseconds).ToArray();
    }

    /// <summary>体检单个 Bing 端点。</summary>
    /// <param name="endpoint">端点。</param>
    /// <param name="sampleQuery">探测用的输入串。</param>
    /// <param name="timeoutMilliseconds">超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>体检结果。</returns>
    public async Task<EndpointProbeResult> ProbeAsync(
        BingEndpoint endpoint,
        string sampleQuery = "weiruan",
        int timeoutMilliseconds = 2500,
        CancellationToken cancellationToken = default)
    {
        var name = $"{endpoint.Host}{endpoint.Path}";
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMilliseconds);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.BuildUrl(sampleQuery, "zh-CN"));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new EndpointProbeResult(
                    endpoint.Id, name, false, (int)stopwatch.ElapsedMilliseconds, 0,
                    $"HTTP {(int)response.StatusCode}", endpoint.HasContentFilter);
            }

            var items = BingResponseParser.Parse(body, endpoint.Format);
            return new EndpointProbeResult(
                endpoint.Id,
                name,
                items.Count > 0,
                (int)stopwatch.ElapsedMilliseconds,
                items.Count,
                items.Count > 0 ? null : "无法解析出候选",
                endpoint.HasContentFilter);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(
                endpoint.Id, name, false, (int)stopwatch.ElapsedMilliseconds, 0, "超时", endpoint.HasContentFilter);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(
                endpoint.Id, name, false, (int)stopwatch.ElapsedMilliseconds, 0, ex.GetType().Name, endpoint.HasContentFilter);
        }
    }

    /// <summary>
    /// 体检搜狗。
    /// </summary>
    /// <remarks>
    /// 不复用 <see cref="SogouSuggestionClient"/>：它把所有失败都吞成空列表，
    /// 体检需要区分「HTTP 403」「超时」「帧解析不出东西」，所以这里自己发一次请求。
    /// </remarks>
    private async Task<EndpointProbeResult> ProbeSogouAsync(
        string sampleQuery,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        const string id = "sogou";
        const string name = "shouji.sogou.com（搜狗）";

        var frame = SogouFrame.Build(sampleQuery);
        if (frame.Length == 0)
        {
            return new EndpointProbeResult(id, name, false, 0, 0, "示例输入无法组成请求帧");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMilliseconds);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SogouFrame.Endpoint)
            {
                Content = new ByteArrayContent(frame),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new EndpointProbeResult(
                    id, name, false, (int)stopwatch.ElapsedMilliseconds, 0, $"HTTP {(int)response.StatusCode}");
            }

            var items = SogouResponseParser.Parse(body);
            return new EndpointProbeResult(
                id,
                name,
                items.Count > 0,
                (int)stopwatch.ElapsedMilliseconds,
                items.Count,
                items.Count > 0 ? null : "无法解析出候选");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(id, name, false, (int)stopwatch.ElapsedMilliseconds, 0, "超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(id, name, false, (int)stopwatch.ElapsedMilliseconds, 0, ex.GetType().Name);
        }
    }
}

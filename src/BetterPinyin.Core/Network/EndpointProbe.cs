using System.Diagnostics;
using System.Net.Http;
using BetterPinyin.Core.Cloud.Bing;

namespace BetterPinyin.Core.Network;

/// <summary>
/// 单个端点的体检结果。
/// </summary>
/// <param name="Endpoint">端点。</param>
/// <param name="Reachable">是否能解析出结果（仅 HTTP 200 不算通过）。</param>
/// <param name="LatencyMilliseconds">耗时。</param>
/// <param name="ResultCount">解析出的候选条数。</param>
/// <param name="Error">失败原因，成功时为 null。</param>
public sealed record EndpointProbeResult(
    BingEndpoint Endpoint,
    bool Reachable,
    int LatencyMilliseconds,
    int ResultCount,
    string? Error);

/// <summary>
/// 端点体检。
/// </summary>
/// <remarks>
/// 刻意采用「真实查询」而不是 TCP 探测：网络通但接口被劫持、返回登录页、
/// 或返回结构变化时，只有真正解析一次才能发现。判定标准是"HTTP 200 且能解析出至少一条候选"。
/// </remarks>
public sealed class EndpointProbe
{
    private readonly HttpClient _httpClient;

    /// <summary>创建体检器。</summary>
    /// <param name="httpClient">共享 HTTP 客户端。</param>
    public EndpointProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 并发体检全部端点。
    /// </summary>
    /// <param name="sampleQuery">探测用的输入串，建议选一个各端点都有结果的常见词。</param>
    /// <param name="timeoutMilliseconds">单端点超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按耗时升序排列的结果。</returns>
    public async Task<IReadOnlyList<EndpointProbeResult>> ProbeAllAsync(
        string sampleQuery = "weiruan",
        int timeoutMilliseconds = 2500,
        CancellationToken cancellationToken = default)
    {
        var tasks = BingEndpointCatalog.CloudEndpoints
            .Select(endpoint => ProbeAsync(endpoint, sampleQuery, timeoutMilliseconds, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OrderByDescending(r => r.Reachable).ThenBy(r => r.LatencyMilliseconds).ToArray();
    }

    /// <summary>体检单个端点。</summary>
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
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMilliseconds);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.BuildUrl(sampleQuery, "zh-CN"));
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new EndpointProbeResult(endpoint, false, (int)stopwatch.ElapsedMilliseconds, 0, $"HTTP {(int)response.StatusCode}");
            }

            var items = BingResponseParser.Parse(body, endpoint.Format);
            return new EndpointProbeResult(
                endpoint,
                items.Count > 0,
                (int)stopwatch.ElapsedMilliseconds,
                items.Count,
                items.Count > 0 ? null : "无法解析出候选");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(endpoint, false, (int)stopwatch.ElapsedMilliseconds, 0, "超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            stopwatch.Stop();
            return new EndpointProbeResult(endpoint, false, (int)stopwatch.ElapsedMilliseconds, 0, ex.GetType().Name);
        }
    }
}

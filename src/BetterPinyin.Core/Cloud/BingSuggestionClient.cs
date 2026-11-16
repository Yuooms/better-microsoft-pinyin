using System.Net;
using System.Net.Http;
using BetterPinyin.Core.Cloud.Bing;

namespace BetterPinyin.Core.Cloud;

/// <summary>
/// 基于 Bing 搜索建议的云联想词源。
/// </summary>
/// <remarks>
/// 按端点顺序尝试：前一个失败或返回空就换下一个，任一成功即返回。
/// 失败一律吞掉并返回空列表——用户的输入流畅性优先于"报告错误"。
/// </remarks>
public sealed class BingSuggestionClient : ICloudSuggestionProvider
{
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<BingEndpoint> _endpoints;
    private readonly string _market;
    private readonly int _timeoutMilliseconds;

    /// <summary>创建客户端。</summary>
    /// <param name="httpClient">共享的 HTTP 客户端。</param>
    /// <param name="market">市场标识，默认 <c>zh-CN</c>。</param>
    /// <param name="endpoints">端点列表，默认取 <see cref="BingEndpointCatalog.CloudEndpoints"/>。</param>
    /// <param name="timeoutMilliseconds">单次请求超时。</param>
    public BingSuggestionClient(
        HttpClient httpClient,
        string market = "zh-CN",
        IReadOnlyList<BingEndpoint>? endpoints = null,
        int timeoutMilliseconds = 1500)
    {
        _httpClient = httpClient;
        _market = market;
        _endpoints = endpoints ?? BingEndpointCatalog.CloudEndpoints;
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    /// <inheritdoc />
    public string Id => "bing";

    /// <inheritdoc />
    public async Task<CloudProviderAnswer> QueryAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return CloudProviderAnswer.Empty(Id);
        }

        foreach (var endpoint in _endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TryQueryAsync(endpoint, query, cancellationToken).ConfigureAwait(false);
            if (result.Count > 0)
            {
                // 端点 id 用局部变量带出去：它只属于这一次查询，不放进字段，
                // 否则双源并发时同一个客户端会被别的查询覆盖。
                return new CloudProviderAnswer(result, endpoint.Id);
            }
        }

        return CloudProviderAnswer.Empty(Id);
    }

    private async Task<IReadOnlyList<string>> TryQueryAsync(
        BingEndpoint endpoint,
        string query,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 端点级超时只能收紧全局值，具体规则见 BingEndpoint.ResolveTimeout。
        timeoutSource.CancelAfter(endpoint.ResolveTimeout(_timeoutMilliseconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.BuildUrl(query, _market));
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Array.Empty<string>();
            }

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            return BingResponseParser.Parse(body, endpoint.Format);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户继续打字导致的取消，向上传播以便上层停止后续端点尝试。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 单端点超时，换下一个。
            return Array.Empty<string>();
        }
        catch (HttpRequestException)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }
}

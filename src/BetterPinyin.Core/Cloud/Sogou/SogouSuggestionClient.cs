using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace BetterPinyin.Core.Cloud.Sogou;

/// <summary>
/// 搜狗云输入词源。
/// </summary>
/// <remarks>
/// <para>
/// 只有一个端点，失败即返回空：重试与端点切换只会放大被限流的概率，
/// 而严格槽还能回退到 Bing，用户不会因此看不到候选。
/// </para>
/// <para>
/// 只走 HTTPS，不做明文回退——请求体就是用户正在输入的拼音。
/// </para>
/// </remarks>
public sealed class SogouSuggestionClient : ICloudSuggestionProvider
{
    /// <summary>来源标识。</summary>
    public const string SourceId = "sogou";

    private const int MaxResponseBytes = 64 * 1024;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private readonly HttpClient _httpClient;
    private readonly int _timeoutMilliseconds;

    /// <summary>创建客户端。</summary>
    /// <param name="httpClient">共享的 HTTP 客户端。</param>
    /// <param name="timeoutMilliseconds">单次请求超时。</param>
    public SogouSuggestionClient(HttpClient httpClient, int timeoutMilliseconds = 1500)
    {
        _httpClient = httpClient;
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public async Task<CloudProviderAnswer> QueryAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var frame = SogouFrame.Build(query);
        if (frame.Length == 0)
        {
            return CloudProviderAnswer.Empty(SourceId);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeoutMilliseconds);

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

            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentLength is 0 or > MaxResponseBytes)
            {
                return CloudProviderAnswer.Empty(SourceId);
            }

            var body = await response.Content
                .ReadAsByteArrayAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            if (body.Length == 0 || body.Length > MaxResponseBytes)
            {
                return CloudProviderAnswer.Empty(SourceId);
            }

            return new CloudProviderAnswer(SogouResponseParser.Parse(body), SourceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户继续打字导致的取消，向上传播以便上层停止后续查询。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 单次超时。
            return CloudProviderAnswer.Empty(SourceId);
        }
        catch (HttpRequestException)
        {
            return CloudProviderAnswer.Empty(SourceId);
        }
        catch (InvalidOperationException)
        {
            return CloudProviderAnswer.Empty(SourceId);
        }
        catch (Exception)
        {
            // 兜底：解析或底层 IO 的任何意外都不该打断用户输入。
            return CloudProviderAnswer.Empty(SourceId);
        }
    }
}

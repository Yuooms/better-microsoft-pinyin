using System.Net;
using System.Net.Http;

namespace BetterPinyin.App;

/// <summary>
/// 进程内共享的 HTTP 客户端。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>用单例复用连接，避免每次查询都重新握手。</item>
/// <item><see cref="HttpClient.Timeout"/> 设为无限：单次超时由调用方用 CancellationToken 控制，
/// 否则两套超时叠加会让实际等待时间不可预测。</item>
/// <item>不自动跟随重定向以外的隐式行为，也不挂 Cookie 容器——这些请求不带任何身份。</item>
/// </list>
/// </remarks>
internal static class Http
{
    private static readonly Lazy<HttpClient> LazyClient = new(Create, isThreadSafe: true);

    /// <summary>共享客户端。</summary>
    public static HttpClient Client => LazyClient.Value;

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxConnectionsPerServer = 4,
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return client;
    }
}

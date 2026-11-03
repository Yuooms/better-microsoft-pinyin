using System.Net.Http;
using System.Text;
using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Ranking;

namespace BetterPinyin.Tests;

/// <summary>
/// 测试入口。默认只跑离线单元测试，加 <c>--live</c> 额外跑一次真实联网验证。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine("  Better Microsoft Pinyin · 核心逻辑测试");
        Console.WriteLine("════════════════════════════════════════════════════════");

        CoreTests.RunAll();

        if (args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine();
            Console.WriteLine("[附加] 真实联网验证");
            await LiveCheckAsync().ConfigureAwait(false);
        }

        return Check.Report();
    }

    /// <summary>
    /// 用真实 Bing 接口跑一遍完整管线，确认端到端可用。
    /// </summary>
    private static async Task LiveCheckAsync()
    {
        using var httpClient = new HttpClient();
        var client = new BingSuggestionClient(httpClient, timeoutMilliseconds: 2000);
        var matcher = new PinyinMatcher(fuzzyEnabled: true);
        var ranker = new CandidateRanker(matcher, maxResults: 3);

        foreach (var query in new[] { "yisitalu", "yisital", "ystl", "weiruan" })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var raw = await client.GetSuggestionsAsync(query, timeout.Token).ConfigureAwait(false);
                var cleaned = ResultSanitizer.Sanitize(raw, query);
                var ranked = ranker.Rank(query, cleaned);

                var rawText = raw.Count == 0 ? "(无)" : string.Join(" / ", raw.Take(4));
                var finalText = ranked.Count == 0 ? "(无)" : string.Join(" / ", ranked.Select(r => $"{r.Text}({r.Score:0.00})"));

                Console.WriteLine($"    {query,-10} 原始 {raw.Count,2} 条  来源={client.LastEndpointId ?? "-"}");
                Console.WriteLine($"    {"",-10}   清洗后 {cleaned.Count} 条：{string.Join(" / ", cleaned.Take(4))}");
                Console.WriteLine($"    {"",-10}   最终：{finalText}");
                if (raw.Count > 0 && cleaned.Count == 0)
                {
                    Console.WriteLine($"    {"",-10}   原始样本被全部过滤：{rawText}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    {query,-10} 请求失败：{ex.GetType().Name} {ex.Message}");
            }
        }
    }
}

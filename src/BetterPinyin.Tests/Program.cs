using System.Net.Http;
using System.Text;
using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Lexicon;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Ranking;
using BetterPinyin.Core.Wordbook;

namespace BetterPinyin.Tests;

/// <summary>
/// 测试入口。默认只跑离线单元测试，加 <c>--live</c> 额外跑一次真实联网验证，
/// 加 <c>--probe &lt;拼音&gt;</c> 可直接观察匹配与分区结果。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine("  Better Microsoft Pinyin · 核心逻辑测试");
        Console.WriteLine("════════════════════════════════════════════════════════");

        var lexicon = BinaryLexicon.LoadDefault(SyllableTable.Default, out var lexiconError);
        Console.WriteLine(lexicon.IsLoaded
            ? $"词库：{lexicon.WordCount} 词，{(lexicon is BinaryLexicon binary ? binary.ByteLength / 1024 : 0)} KB"
            : $"词库：未加载（{lexiconError}）");
        Console.WriteLine();

        if (args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            Probe(args, lexicon);
            return 0;
        }

        if (args.Any(a => a.Equals("--udp", StringComparison.OrdinalIgnoreCase)))
        {
            return UdpSpike.Run(args);
        }

        if (args.Any(a => a.Equals("--learned", StringComparison.OrdinalIgnoreCase)))
        {
            return LearnedDump(args, lexicon);
        }

        try
        {
            CoreTests.RunAll();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ 测试中断：{ex.GetType().Name} {ex.Message}");
            Check.True(false, $"测试不应抛异常：{ex.Message}");
        }

        if (args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine();
            Console.WriteLine("[附加] 真实联网验证");
            await LiveCheckAsync(lexicon).ConfigureAwait(false);
        }

        return Check.Report();
    }

    /// <summary>对一组候选分别打印匹配结果与分区结果，用于手工核对边界。</summary>
    /// <summary>打印微软拼音自学习词库的解析结果，只读，不写入。</summary>
    private static int LearnedDump(string[] args, IPinyinLexicon lexicon)
    {
        var store = new MspyLearningStore();
        Console.WriteLine($"文件：{store.FilePath}");

        if (!store.Exists)
        {
            Console.WriteLine("尚未生成（输入法还没学到任何词）。");
            return 1;
        }

        if (!store.TryRead(out var words, out var error))
        {
            Console.WriteLine($"读取失败：{error}");
            return 1;
        }

        var filter = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var shown = filter is null
            ? words
            : words.Where(w => w.Text.Contains(filter, StringComparison.Ordinal)).ToList();

        Console.WriteLine($"共 {words.Count} 条（只读解析），显示 {shown.Count} 条\n");
        foreach (var word in shown.Take(40))
        {
            var pinyin = MspyLearningStore.ResolvePinyin(word.Text, word.Jianpin, lexicon);
            Console.WriteLine($"  {word.Text,-8} {pinyin,-26} {word.Jianpin}");
        }

        if (shown.Count > 40)
        {
            Console.WriteLine($"  … 其余 {shown.Count - 40} 条");
        }

        return 0;
    }

    private static void Probe(string[] args, IPinyinLexicon lexicon)
    {
        var query = args.FirstOrDefault(a =>
            !a.StartsWith("--", StringComparison.Ordinal) && !a.Contains('=', StringComparison.Ordinal))
            ?? "yisitalu";
        var matcher = new PinyinMatcher(lexicon);

        Console.WriteLine($"输入：{query}");
        Console.WriteLine($"音节切分：{(SyllableTable.Default.TrySegment(query, true, out var count) ? $"{count} 个音节" : "无法切分（可能是简拼）")}");
        Console.WriteLine($"简拼展开：{string.Join(" / ", lexicon.ExpandAbbreviation(query, 4))}");
        Console.WriteLine();

        foreach (var candidate in args.Where(a => a.StartsWith("candidate=", StringComparison.OrdinalIgnoreCase)))
        {
            var text = candidate["candidate=".Length..];
            var match = matcher.Match(query, text);
            Console.WriteLine($"  {text,-12} {match.Kind,-18} 音节={match.LemmaSyllables} 多余={match.ExtraSyllables}");
            Console.WriteLine($"  {"",-12} 读音：{Describe(text, lexicon)}");
        }
    }

    private static string Describe(string text, IPinyinLexicon lexicon)
    {
        var parts = new List<string>(text.Length);
        foreach (var ch in text)
        {
            if (!lexicon.TryGetReadings(ch, out var slots))
            {
                parts.Add($"{ch}=?");
                continue;
            }

            var names = new List<string>(slots.Length);
            foreach (var raw in slots)
            {
                if (raw != 0 && raw - 1 < SyllableTable.Default.Count)
                {
                    names.Add(SyllableTable.Default.GetCanonical(raw - 1));
                }
            }

            parts.Add($"{ch}={string.Join("/", names)}");
        }

        return string.Join("  ", parts);
    }

    /// <summary>
    /// 用真实 Bing 接口跑一遍完整管线，确认端到端可用。
    /// </summary>
    private static async Task LiveCheckAsync(IPinyinLexicon lexicon)
    {
        using var httpClient = new HttpClient();
        var client = new BingSuggestionClient(httpClient, timeoutMilliseconds: 2000);
        var matcher = new PinyinMatcher(lexicon);
        var options = new SuggestionSelectionOptions(
            StrictEnabled: true,
            StrictDedupeLocal: true,
            ExtendedEnabled: true,
            ExtendedMaxCount: 3);

        foreach (var query in new[] { "yisitalu", "ystl", "rnw", "ruonawa", "lianxiang", "weiruan" })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));

                var expansions = lexicon.ExpandAbbreviation(query, 3);
                var isAbbreviation = expansions.Count > 0 &&
                    !SyllableTable.Default.TrySegment(query, true, out _);
                var request = isAbbreviation ? expansions[0] : query;

                var raw = await client.GetSuggestionsAsync(request, timeout.Token).ConfigureAwait(false);
                var cleaned = ResultSanitizer.Sanitize(raw, request, SanitizeOptions.Extended);
                // 与控制器保持一致：分区用实际请求串，而不是用户的简拼原文。
                var set = SuggestionSelector.Select(request, cleaned, null, matcher, options);

                Console.WriteLine($"    {query,-10} 查询={request}  原始 {raw.Count,2} 条  来源={client.LastEndpointId ?? "-"}");
                Console.WriteLine($"    {"",-10}   严格：{set.Strict?.Text ?? "(无)"}");
                Console.WriteLine($"    {"",-10}   扩展：{(set.Extended.Count == 0 ? "(无)" : string.Join(" / ", set.Extended.Select(e => e.Text)))}");
                if (isAbbreviation)
                {
                    Console.WriteLine($"    {"",-10}   简拼展开：{string.Join(" / ", expansions)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    {query,-10} 请求失败：{ex.GetType().Name} {ex.Message}");
            }
        }
    }
}

using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Cloud.Bing;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Ranking;
using BetterPinyin.Core.Settings;

namespace BetterPinyin.Tests;

/// <summary>
/// 测试用读音字典。
/// </summary>
internal sealed class FakePinyinDictionary : IPinyinDictionary
{
    private readonly Dictionary<string, string> _readings = new(StringComparer.Ordinal)
    {
        ["伊斯塔露"] = "yi'si'ta'lu",
        ["伊斯塔"] = "yi'si'ta",
        ["意思"] = "yi'si",
        ["以色列"] = "yi'se'lie",
        ["中国"] = "zhong'guo",
    };

    public bool TryGetReading(string text, out string reading) => _readings.TryGetValue(text, out reading!);
}

/// <summary>
/// 核心逻辑的单元测试。
/// </summary>
internal static class CoreTests
{
    public static void RunAll()
    {
        Console.WriteLine("[1/5] Bing 响应解析");
        Parser();

        Console.WriteLine("[2/5] 候选清洗");
        Sanitizer();

        Console.WriteLine("[3/5] 拼音校验");
        Matcher();

        Console.WriteLine("[4/5] 排序合并");
        Ranker();

        Console.WriteLine("[5/5] 设置持久化");
        Settings();
    }

    private static void Parser()
    {
        var openSearch = BingResponseParser.Parse(
            """["yisitalu",["伊斯塔露","伊斯塔路是谁","伊斯塔露 原神"]]""",
            BingResponseFormat.OpenSearchJson);
        Check.Equal(3, openSearch.Count, "OpenSearch 应解析出 3 条");
        Check.Equal("伊斯塔露", openSearch[0], "OpenSearch 首条");

        var searchBox = BingResponseParser.Parse(
            """{"AS":{"Query":"yisitalu","Results":[{"Type":"AS","Suggests":[{"Txt":"伊斯塔露"},{"Txt":"伊斯塔"}]}]}}""",
            BingResponseFormat.SearchBoxJson);
        Check.Equal(2, searchBox.Count, "SearchBox 应解析出 2 条");
        Check.Equal("伊斯塔露", searchBox[0], "SearchBox 首条");

        // 真实接口会带 JSONP 包装与尾部注释，必须能剥掉。
        var wrapped = BingResponseParser.Parse(
            """if(typeof cb == 'function') cb({"AS":{"Results":[{"Suggests":[{"Txt":"阿波罗"}]}]}})/* pageview_candidate */);""",
            BingResponseFormat.SearchBoxJson);
        Check.Equal(1, wrapped.Count, "带 JSONP 包装应仍能解析");
        Check.Equal("阿波罗", wrapped.Count > 0 ? wrapped[0] : string.Empty, "JSONP 包装内的候选");

        var garbage = BingResponseParser.Parse("<html>error</html>", BingResponseFormat.OpenSearchJson);
        Check.Equal(0, garbage.Count, "非 JSON 响应应返回空");

        var empty = BingResponseParser.Parse("[]", BingResponseFormat.OpenSearchJson);
        Check.Equal(0, empty.Count, "空数组应返回空");
    }

    private static void Sanitizer()
    {
        var cleaned = ResultSanitizer.Sanitize(
            new[]
            {
                "伊斯塔露 原神",
                "元素tl",
                "一",
                "伊斯塔露",
                "伊斯塔露",
                "yisitalu",
                "\u200B伊斯塔路",
            },
            "yisitalu");

        Check.Contains(cleaned, "伊斯塔露", "应保留纯汉字候选");
        Check.Contains(cleaned, "伊斯塔路", "应剔除零宽字符后保留");
        Check.NotContains(cleaned, "元素tl", "含拉丁字母的候选应丢弃");
        Check.NotContains(cleaned, "一", "单字候选应丢弃");
        Check.NotContains(cleaned, "伊斯塔露 原神", "整句应截断为第一个词");
        Check.Equal(2, cleaned.Count, "重复项应合并，且回显输入串应丢弃");
        Check.Equal("伊斯塔露", cleaned[0], "截断后的首条");

        Check.Equal("伊斯塔露", ResultSanitizer.Normalize("  伊斯塔露 "), "规范化应去首尾空白");
        Check.True(ResultSanitizer.IsAllChinese("伊斯塔露"), "中文判定");
        Check.False(ResultSanitizer.IsAllChinese("伊斯塔abc"), "混合串不是纯中文");
    }

    private static void Matcher()
    {
        var matcher = new PinyinMatcher(new FakePinyinDictionary(), fuzzyEnabled: true);

        Check.Equal(PinyinMatchKind.FullOrPrefix, matcher.Match("yisitalu", "伊斯塔露"), "全拼应匹配");
        Check.Equal(PinyinMatchKind.FullOrPrefix, matcher.Match("yisital", "伊斯塔露"), "末音节未打完应匹配");
        Check.Equal(PinyinMatchKind.Abbreviation, matcher.Match("ystl", "伊斯塔露"), "简拼应匹配");
        Check.Equal(PinyinMatchKind.None, matcher.Match("abc", "伊斯塔露"), "拼音不符应不匹配");
        Check.Equal(PinyinMatchKind.None, matcher.Match("yisitalu", "以色列"), "读音不符应不匹配");

        // 模糊音：zh→z 归一后 zongguo 可匹配 zhongguo。
        var fuzzy = new PinyinMatcher(new FakePinyinDictionary(), fuzzyEnabled: true);
        Check.Equal(PinyinMatchKind.FullOrPrefix, fuzzy.Match("zongguo", "中国"), "模糊音应允许 z/zh 互通");

        var strict = new PinyinMatcher(new FakePinyinDictionary(), fuzzyEnabled: false);
        Check.Equal(PinyinMatchKind.None, strict.Match("zongguo", "中国"), "关闭模糊音后应严格匹配");

        // 没有词库时不能拦掉所有候选，应放行交给其它规则。
        var noDictionary = new PinyinMatcher(NullPinyinDictionary.Instance);
        Check.False(noDictionary.IsAvailable, "空字典应报告不可用");
        Check.Equal(PinyinMatchKind.FullOrPrefix, noDictionary.Match("whatever", "任何词"), "空字典应放行");
    }

    private static void Ranker()
    {
        var matcher = new PinyinMatcher(new FakePinyinDictionary());
        var ranker = new CandidateRanker(matcher, maxResults: 3);

        var ranked = ranker.Rank(
            "yisita",
            new[] { "伊斯塔露", "伊斯塔", "以色列", "意思" },
            localCandidates: new[] { "伊斯塔露" },
            providerId: "bing-osjson");

        Check.NotContains(ranked.Select(r => r.Text), "伊斯塔露", "本地已有的候选应被去重");
        Check.NotContains(ranked.Select(r => r.Text), "以色列", "读音不符的候选应被剔除");
        Check.Contains(ranked.Select(r => r.Text), "伊斯塔", "读得通的候选应保留");
        Check.NotContains(ranked.Select(r => r.Text), "意思", "输入串长于候选读音时应剔除（覆盖不全）");
        Check.True(ranked.All(r => r.Score > 0 && r.Score <= 1), "分数应落在 0~1");

        // 输入比候选读音长，说明用户还在继续打，不能把这个短词当成完整匹配。
        var uncovered = ranker.Rank("yisitalu", new[] { "伊斯塔" });
        Check.Equal(0, uncovered.Count, "输入串覆盖不全时不应给出该候选");

        var limited = new CandidateRanker(matcher, maxResults: 1)
            .Rank("yisi", new[] { "伊斯塔", "意思" });
        Check.Equal(1, limited.Count, "maxResults 应生效");

        var emptyLocal = ranker.Rank("yisitalu", Array.Empty<string>());
        Check.Equal(0, emptyLocal.Count, "空输入应返回空结果");
    }

    private static void Settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BetterPinyinTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new SettingsStore(path);
            var defaults = store.Load();
            Check.True(defaults.CloudAssistEnabled, "默认应启用云联想");
            Check.Equal("Tab", defaults.AcceptKey, "默认采纳键应为 Tab");
            Check.True(store.LastError is null, "文件不存在时不应报错");

            defaults.AcceptKey = "Semicolon";
            defaults.MaxSuggestions = 3;
            defaults.BlockedProcesses.Add("mstsc");
            Check.True(store.Save(defaults), "保存应成功");

            var reloaded = new SettingsStore(path).Load();
            Check.Equal("Semicolon", reloaded.AcceptKey, "采纳键应持久化");
            Check.Equal(3, reloaded.MaxSuggestions, "候选数量应持久化");
            Check.Contains(reloaded.BlockedProcesses, "mstsc", "黑名单应持久化");

            File.WriteAllText(path, "{ 这不是合法 JSON");
            var recovered = new SettingsStore(path).Load();
            Check.True(recovered.CloudAssistEnabled, "配置损坏时应退回默认值");
            Check.True(new SettingsStore(path).LastError is null, "读取本身不应抛异常");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论。
            }
        }
    }
}

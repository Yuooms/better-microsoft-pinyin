using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using BetterPinyin.Contracts;
using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Cloud.Bing;
using BetterPinyin.Core.Cloud.Sogou;
using BetterPinyin.Core.Lexicon;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Ranking;
using BetterPinyin.Core.Settings;
using BetterPinyin.Core.Statistics;
using BetterPinyin.Core.Updates;
using BetterPinyin.Core.Wordbook;
using BetterPinyin.Interop.Input;
using BetterPinyin.Interop.Startup;
using BetterPinyin.Interop.Windowing;
using Microsoft.Win32;

namespace BetterPinyin.Tests;

/// <summary>
/// 测试用词库：只收录测试需要的字，读音用空格分隔。
/// </summary>
internal sealed class FakeLexicon : IPinyinLexicon
{
    private readonly Dictionary<char, ushort[]> _readings;

    private FakeLexicon(Dictionary<char, ushort[]> readings) => _readings = readings;

    public bool IsLoaded => true;

    public int WordCount => _readings.Count;

    public static FakeLexicon Create()
    {
        var source = new Dictionary<char, string>
        {
            ['伊'] = "yi", ['斯'] = "si", ['塔'] = "ta", ['露'] = "lu",
            ['意'] = "yi", ['思'] = "si", ['以'] = "yi", ['色'] = "se", ['列'] = "lie",
            ['中'] = "zhong", ['国'] = "guo", ['西'] = "xi", ['安'] = "an",
            ['方'] = "fang", ['案'] = "an", ['反'] = "fan", ['感'] = "gan",
            ['银'] = "yin", ['行'] = "xing hang", ['绿'] = "lv", ['装'] = "zhuang",
            ['花'] = "hua", ['儿'] = "er", ['若'] = "ruo", ['娜'] = "na", ['瓦'] = "wa",
            ['壁'] = "bi", ['纸'] = "zhi", ['英'] = "ying", ['文'] = "wen",
            ['联'] = "lian", ['想'] = "xiang", ['电'] = "dian", ['脑'] = "nao",
            ['管'] = "guan", ['家'] = "jia", ['那'] = "na", ['谁'] = "shui", ['路'] = "lu", ['是'] = "shi",
            ['日'] = "ri", ['内'] = "nei", ['公'] = "gong", ['约'] = "yue",
        };

        var table = SyllableTable.Default;
        var readings = new Dictionary<char, ushort[]>(source.Count);
        foreach (var pair in source)
        {
            var ids = pair.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => table.TryGetCanonicalId(s, out var id)
                    ? (ushort)(id + 1)
                    : throw new InvalidOperationException($"音节表缺少 {s}"))
                .ToArray();
            readings[pair.Key] = ids;
        }

        return new FakeLexicon(readings);
    }

    public bool TryGetReadings(char value, out ReadOnlySpan<ushort> syllableIds)
    {
        if (_readings.TryGetValue(value, out var ids))
        {
            syllableIds = ids;
            return true;
        }

        syllableIds = default;
        return false;
    }

    public IReadOnlyList<string> ExpandAbbreviation(string initials, int max) => Array.Empty<string>();
}

/// <summary>
/// 核心逻辑的单元测试。
/// </summary>
internal static class CoreTests
{
    public static void RunAll()
    {
        Run("[1/20] Bing 响应解析", Parser);
        Run("[2/20] 候选清洗", Sanitizer);
        Run("[3/20] 拼音匹配", Matcher);
        Run("[4/20] 严格 / 扩展分区", Selector);
        Run("[5/20] 排序合并（旧链路）", Ranker);
        Run("[6/20] 设置持久化", Settings);
        Run("[7/20] 词库词表", Catalog);
        Run("[8/20] 用户词库", Wordbook);
        Run("[9/20] 自学习词库", Learning);
        Run("[10/20] 自学习词库删除", LearningDelete);
        Run("[11/20] 自学习词库备份", LearningBackup);
        Run("[12/20] 开机启动登记", StartupRegistrationTest);
        Run("[13/20] 输入统计", Statistics);
        Run("[14/20] 组字状态机", CompositionTrackerStates);
        Run("[15/20] 版本检查与更新脚本", Updates);
        Run("[16/20] 搜狗组帧与解帧", Sogou);
        Run("[17/20] 搜狗客户端（桩 HTTP）", SogouClient);
        Run("[18/20] 双源合并", SelectorCombine);
        Run("[19/20] Bing 端点与超时", BingEndpoints);
        Run("[20/20] 进程完整性级别", Integrity);
    }

    /// <summary>逐组执行，一组崩掉不影响其它组出结论。</summary>
    private static void Statistics()
    {
        var now = new DateTime(2026, 11, 13, 9, 30, 0);
        var aggregator = new StatisticsAggregator(clock: () => now);

        // 一段完整组字：两次按键、三个字母、以提交结束
        aggregator.BeginComposition("Code");
        aggregator.AddKeystroke();
        aggregator.AddLetters(2);
        aggregator.AddKeystroke();
        aggregator.AddLetters(1);
        aggregator.EndComposition(committed: true);

        var today = aggregator.Snapshot().Single();
        Check.Equal("2026-11-13", today.Date, "统计日期");
        Check.Equal(1, today.Totals.Compositions, "组字次数");
        Check.Equal(3, today.Totals.Letters, "拼音字母数");
        Check.Equal(2, today.Totals.Keystrokes, "组字期间按键数");
        Check.Equal(1, today.Totals.Committed, "提交次数");
        Check.Equal(0, today.Totals.Cancelled, "取消次数");
        Check.Equal(1, today.GetHour(9).Compositions, "9 点小时的组字次数");
        Check.Equal(3, today.GetHour(9).Letters, "9 点小时的字母数");
        Check.Equal(1, today.Apps.Count, "按程序聚合出 1 条");
        Check.Equal("Code", today.Apps[0].Process, "程序名");
        Check.Equal(3, today.Apps[0].Letters, "该程序的字母数");

        // 云词采纳：字数按文本长度记，但不保存文本
        aggregator.AddCloudAccept("若娜瓦");
        var afterAccept = aggregator.Snapshot().Single();
        Check.Equal(1, afterAccept.Totals.CloudAccepts, "云词采纳次数");
        Check.Equal(3, afterAccept.Totals.CloudCharacters, "云词上屏字数");

        // 不在组字状态的按键不计入
        var keystrokesBefore = afterAccept.Totals.Keystrokes;
        aggregator.AddKeystroke();
        Check.Equal(keystrokesBefore, aggregator.Snapshot().Single().Totals.Keystrokes,
            "非组字期间的按键不计入");

        // 跨天：23:59 开始、00:01 提交，组字记在前一天、提交记在后一天
        // 第一天的字母数要按「已有 + 跨天前新增的 2 个」算，不能写成绝对值。
        var lettersOnFirstDayBefore = aggregator.Snapshot().First(d => d.Date == "2026-11-13").Totals.Letters;
        now = new DateTime(2026, 11, 13, 23, 59, 0);
        aggregator.BeginComposition("Notepad");
        aggregator.AddLetters(2);
        now = new DateTime(2026, 11, 14, 0, 1, 0);
        aggregator.AddLetters(3);
        aggregator.EndComposition(committed: true);

        var days = aggregator.Snapshot();
        Check.Equal(2, days.Count, "跨天后有两个日期桶");
        var first = days[0];
        var second = days[1];
        Check.Equal("2026-11-13", first.Date, "第一天日期");
        Check.Equal("2026-11-14", second.Date, "第二天日期");
        Check.Equal(2, first.Totals.Compositions, "跨天组字记在开始那天");
        Check.Equal(lettersOnFirstDayBefore + 2, first.Totals.Letters, "跨天前的字母记在第一天");
        Check.Equal(3, second.Totals.Letters, "跨天后的字母记在第二天");
        Check.Equal(1, second.Totals.Committed, "提交记在结束那天");
        Check.Equal(0, second.Totals.Compositions, "第二天没有新的组字");
        Check.Equal(1, first.GetHour(23).Compositions, "23 点桶");
        Check.Equal(3, second.GetHour(0).Letters, "0 点桶");

        // 取消：与提交互斥
        now = new DateTime(2026, 11, 14, 10, 0, 0);
        aggregator.BeginComposition("Code");
        aggregator.AddLetters(1);
        aggregator.EndComposition(committed: false);
        var final = aggregator.Snapshot().Last();
        Check.Equal(1, final.Totals.Committed, "提交次数不变");
        Check.Equal(1, final.Totals.Cancelled, "取消次数 +1");

        // 重复日期合并
        var duplicated = new List<DailyStatistics>
        {
            new() { Date = "2026-11-01", Totals = new StatisticsTotals { Letters = 5 } },
            new() { Date = "2026-11-01", Totals = new StatisticsTotals { Letters = 7 } },
        };
        var merged = new StatisticsAggregator(duplicated);
        Check.Equal(12, merged.Snapshot().Single().Totals.Letters, "重复日期应合并求和");

        // 程序长尾归并
        var manyApps = new StatisticsAggregator(maxAppsPerDay: 3);
        for (var i = 0; i < 6; i++)
        {
            manyApps.BeginComposition($"App{i}");
            manyApps.EndComposition(committed: true);
        }

        var trimmedApps = manyApps.Snapshot().Single().Apps;
        Check.Equal(3, trimmedApps.Count, "程序条数受上限约束");
        Check.True(trimmedApps.Any(a => a.Process == StatisticsAggregator.OtherProcess), "长尾并入「其它」");
        Check.Equal(6, trimmedApps.Sum(a => a.Compositions), "归并后总数不变");

        // 保留期裁剪：保留最近 N 个有记录的日子
        var fortyDays = Enumerable.Range(0, 40)
            .Select(i => new DailyStatistics
            {
                Date = new DateTime(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd"),
            })
            .ToList();
        var kept = StatisticsStore.TrimByRetention(fortyDays, 30);
        Check.Equal(30, kept.Count, "保留期裁剪到 30 条");
        Check.Equal("2026-01-11", kept[0].Date, "裁掉的是最旧的 10 天");
        Check.Equal("2026-02-09", kept[^1].Date, "保留到今天为止最新的那条");

        // 落盘与读回
        var path = Path.Combine(Path.GetTempPath(), "bp-stats-" + Guid.NewGuid().ToString("N") + ".json");
        using var store = new StatisticsStore(path);
        Check.True(store.Save(aggregator.Snapshot(), retentionDays: 0), "保存统计");
        var reloaded = store.Load();
        Check.Equal(2, reloaded.Count, "读回天数");
        Check.Equal(second.Totals.Letters + 1, reloaded.Last().Totals.Letters, "读回字母数");

        var json = File.ReadAllText(path);
        Check.False(json.Contains("若娜瓦"), "统计文件里不应出现输入内容");
        Check.True(json.Contains("CloudCharacters"), "文件里应只有计数字段");

        // 防抖落盘也必须按保留期裁剪：用户从不打开设置页时，
        // 如果只有手动保存才裁剪，文件会一年年长下去。
        var retentionPath = Path.Combine(Path.GetTempPath(), "bp-stats-" + Guid.NewGuid().ToString("N") + ".json");
        using (var retentionStore = new StatisticsStore(retentionPath))
        {
            var manyDays = Enumerable.Range(0, 40)
                .Select(i => new DailyStatistics
                {
                    Date = new DateTime(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd"),
                    Totals = new StatisticsTotals { Compositions = 1 },
                })
                .ToList();

            retentionStore.ScheduleSave(manyDays, retentionDays: 30);
            Check.True(retentionStore.Flush(), "防抖刷盘");
            Check.Equal(30, retentionStore.Load().Count, "防抖落盘也应裁剪到保留期");

            // 之后不带保留期参数保存时，沿用上一次记下的值，
            // 而不是退回 0（= 永不裁剪）。
            Check.True(retentionStore.Save(manyDays), "沿用保留期保存");
            Check.Equal(30, retentionStore.Load().Count, "沿用上次保留期");
        }

        if (File.Exists(retentionPath))
        {
            File.Delete(retentionPath);
        }

        // 清空：删文件且不留备份
        Check.True(store.DeleteAll(), "清空统计");
        Check.False(File.Exists(path), "清空后文件应消失");
        Check.Equal(0, store.Load().Count, "清空后读回为空");

        // 损坏文件不应抛异常
        var brokenPath = Path.Combine(Path.GetTempPath(), "bp-stats-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(brokenPath, "{ 这不是合法 JSON");
        using var broken = new StatisticsStore(brokenPath);
        Check.Equal(0, broken.Load().Count, "损坏文件返回空");
        Check.True(broken.LastError is not null, "损坏文件记录错误");
        File.Delete(brokenPath);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void CompositionTrackerStates()
    {
        var tracker = new CompositionTracker();
        var ended = new List<CompositionEndedEventArgs>();
        tracker.CompositionEnded += (_, e) => ended.Add(e);

        tracker.Feed((int)'R', isKeyDown: true, ModifierKeys.None);
        tracker.Feed((int)'U', isKeyDown: true, ModifierKeys.None);
        tracker.Feed((int)'O', isKeyDown: true, ModifierKeys.None);
        Check.Equal("ruo", tracker.RawInput, "按键重建拼音串");

        tracker.Feed(0x20, isKeyDown: true, ModifierKeys.None);
        Check.Equal(1, ended.Count, "空格应结束组字");
        Check.Equal("ruo", ended[0].RawInput, "结束事件带出完整拼音串");
        Check.Equal(3, ended[0].Length, "结束事件带出长度");
        Check.Equal(CompositionEndReason.Committed, ended[0].Reason, "空格算提交");

        tracker.Feed((int)'A', isKeyDown: true, ModifierKeys.None);
        tracker.Feed(0x08, isKeyDown: true, ModifierKeys.None);
        Check.Equal(2, ended.Count, "退格退空应结束组字");
        Check.Equal(string.Empty, ended[1].RawInput, "退格退空后没有残留");
        Check.Equal(CompositionEndReason.Cancelled, ended[1].Reason, "退格算取消");

        tracker.Feed((int)'B', isKeyDown: true, ModifierKeys.None);
        var versionBefore = tracker.Version;
        tracker.Reset();
        Check.Equal(2, ended.Count, "Reset 不触发结束事件");
        Check.True(tracker.Version > versionBefore, "Reset 递增代次");
        Check.Equal(string.Empty, tracker.RawInput, "Reset 清空缓冲");
    }

    /// <summary>版本检查：Release 解析、版本比较与替换脚本。</summary>
    private static void Updates()
    {
        // 只扔一份接近真实的 releases/latest 响应进去，验证我们关心的字段。
        const string json = """
        {
          "tag_name": "v0.2.0",
          "name": "v0.2.0",
          "html_url": "https://github.com/Yuooms/better-microsoft-pinyin/releases/tag/v0.2.0",
          "body": "修复若干问题",
          "published_at": "2026-11-20T08:00:00Z",
          "prerelease": false,
          "assets": [
            { "name": "source.zip", "browser_download_url": "https://example.com/source.zip", "size": 12 },
            { "name": "BetterMicrosoftPinyin-v0.2.0-win-x64.zip", "browser_download_url": "https://example.com/win.zip", "size": 75932534 }
          ]
        }
        """;

        var release = ReleaseParser.Parse(json);
        Check.True(release is not null, "合法 Release 应能解析");
        Check.Equal("v0.2.0", release!.Tag, "tag 原样保留");
        Check.Equal("0.2.0", release.Version.ToString(3), "版本号应从 tag 解析");
        Check.Equal(2, release.Assets.Count, "附件数量应被完整读出");
        Check.Equal("https://example.com/win.zip", release.FindWindowsPackage()?.DownloadUrl, "应挑出 Windows 包");
        Check.Equal(75932534L, release.FindWindowsPackage()?.Size, "附件大小应读出");
        Check.True(release.IsNewerThan(new Version(0, 1, 0)), "0.2.0 应比 0.1.0 新");
        Check.False(release.IsNewerThan(new Version(0, 2, 0)), "同版本不算更新");
        Check.False(release.IsNewerThan(new Version(0, 3, 0)), "旧版本不算更新");
        Check.True(release.PublishedAt is not null, "发布时间应能解析");

        // 预发布不该催用户升级。
        var beta = ReleaseParser.Parse("""{ "tag_name": "v0.3.0-beta.1", "prerelease": true }""");
        Check.True(beta is not null, "预发布也应能解析");
        Check.True(beta!.IsPrerelease, "带 -beta 的应标为预发布");
        Check.False(beta.IsNewerThan(new Version(0, 1, 0)), "预发布不应提示更新");

        // prerelase 字段漏写时，靠 tag 里的 - 也能判断出来。
        var rc = ReleaseParser.Parse("""{ "tag_name": "v0.3.0-rc1" }""");
        Check.True(rc is { IsPrerelease: true }, "tag 里带 - 即视为预发布");

        Check.True(ReleaseParser.Parse("这不是 JSON") is null, "坏 JSON 应返回 null");
        Check.True(ReleaseParser.Parse(string.Empty) is null, "空响应应返回 null");
        Check.True(ReleaseParser.Parse("""{ "name": "没有 tag" }""") is null, "缺 tag 应返回 null");

        // tag 解析：允许 v 前缀、允许两位版本，预发布只取数字部分。
        Check.Equal("1.2.3", ReleaseParser.ParseTag("v1.2.3")?.ToString(3), "应去掉 v 前缀");
        Check.Equal("1.2.0", ReleaseParser.ParseTag("1.2")?.ToString(3), "两位版本补成三段");
        Check.Equal("0.2.0", ReleaseParser.ParseTag("v0.2.0-rc1")?.ToString(3), "预发布只取数字部分");
        Check.True(ReleaseParser.ParseTag("nightly") is null, "认不出的 tag 返回 null");

        // 替换脚本：参数化路径 + 纯 ASCII + CRLF，三条都是踩过的坑。
        var script = UpdateScript.Build();
        Check.True(script.Contains("robocopy", StringComparison.Ordinal), "脚本应调用 robocopy");
        Check.True(script.Contains("%WAITPID%", StringComparison.Ordinal), "脚本应等待旧进程退出");
        Check.True(script.Contains("start \"\" \"%DST%\\%EXE%\"", StringComparison.Ordinal), "脚本应重启程序");
        Check.True(script.Contains("\r\n", StringComparison.Ordinal), "脚本行尾必须是 CRLF");
        Check.True(script.All(character => character < 128), "脚本必须是纯 ASCII，否则中文路径会乱码");
    }

    private static void Run(string name, Action action)
    {
        Console.WriteLine(name);
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Check.True(false, $"{name} 抛出 {ex.GetType().Name}：{ex.Message}");
        }
    }

    private static void Wordbook()
    {
        var table = SyllableTable.Default;
        Check.True(table.TryGetInitials("yisitalu", out var initials), "全拼应能切分");
        Check.Equal("ystl", initials, "声母串");

        var book = new UserWordbook();
        Check.True(book.Learn("伊斯塔露", "yisitalu", "ystl", out var word), "应能学到新词");
        Check.Equal(1, word.Hits, "首次 hits=1");
        Check.Equal("ystl", word.Initials, "词条自带声母串");
        Check.Equal(1, book.Count, "词条数");

        Check.True(book.Learn("伊斯塔露", "yisitalu", "yisitalu", out var again), "重复学习应成功");
        Check.Equal(2, again.Hits, "再次 hits=2");
        Check.Equal(1, book.Count, "重复学习不新增条目");

        Check.Equal(1, book.MatchInitials("yst", 5).Count, "简拼前缀应命中");
        Check.Equal(0, book.MatchPinyin("yisita", 5).Count, "全拼必须完全相等");
        Check.Equal(1, book.MatchPinyin("yisitalu", 5).Count, "全拼完全相等才命中");

        book.SetAdjustment("伊斯塔露", "yisitalu", WordAdjustment.Hidden);
        Check.Equal(0, book.MatchInitials("ystl", 5).Count, "遗忘后不再命中");
        Check.Equal(1, book.Count, "遗忘后仍保留条目");
        Check.True(book.IsPinyinHidden("yisitalu"), "拼音进入隐藏集合");

        book.SetAdjustment("伊斯塔露", "yisitalu", WordAdjustment.Normal);
        Check.Equal(1, book.MatchInitials("ystl", 5).Count, "恢复后重新命中");

        book.SetAdjustment("中国", "zhongguo", WordAdjustment.Reduced);
        Check.Equal(WordAdjustment.Reduced, book.AdjustmentOf("中国"), "内置词覆盖生效");
        Check.Equal(WordAdjustment.Normal, book.AdjustmentOf("不存在"), "未收录词为默认");
        Check.Equal(WordAdjustment.Reduced, book.AdjustmentOfPinyin("zhongguo"), "拼音档位可查");

        Check.False(book.Learn("测试", "ceshi2", string.Empty, out _), "非法拼音应拒绝");
        Check.False(book.Learn("单", "dan", string.Empty, out _), "单字不记忆");

        // 采纳后要不要记账：扩展词默认只上屏，避免整句挤进自定义短语
        var policy = new AppSettings();
        Check.False(policy.PhraseWriteExtended, "扩展词默认不写入自定义短语");
        Check.True(WordbookCommitPolicy.ShouldRecord(isExtended: false, policy), "严格词默认记录");
        Check.False(WordbookCommitPolicy.ShouldRecord(isExtended: true, policy), "扩展词默认不记录");

        policy.PhraseWriteExtended = true;
        Check.True(WordbookCommitPolicy.ShouldRecord(isExtended: true, policy), "打开开关后扩展词记录");

        policy.WordbookLearnOnAccept = false;
        Check.False(WordbookCommitPolicy.ShouldRecord(isExtended: false, policy), "关掉学习后严格词也不记");

        var directory = Path.Combine(Path.GetTempPath(), $"bp-words-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "words.json");
        try
        {
            using var store = new WordbookStore(path);
            Check.True(store.Save(book), $"保存应成功：{store.LastError}");

            var loaded = store.Load();
            Check.Equal(book.Count, loaded.Count, "词条数往返一致");
            Check.Equal(2, loaded.Words[0].Hits, "hits 往返一致");
            Check.Equal(WordAdjustment.Reduced, loaded.AdjustmentOf("中国"), "覆盖往返一致");

            File.WriteAllText(path, "{ 这不是 json");
            var broken = store.Load();
            Check.Equal(0, broken.Count, "损坏文件回退空词库");
            Check.True(store.LastError is not null, "损坏时记录错误");

            var udp = new MspyPhraseStore(
                Path.Combine(directory, "test.lex"),
                Path.Combine(directory, "backup"));
            Check.True(
                udp.Merge(new[] { new MspyPhrase("yisitalu", 2, "伊斯塔露") }, false, out _),
                $"短语应写入成功：{udp.LastError}");

            var read = udp.Read(out var readError);
            Check.True(readError is null, $"短语应能读回：{readError}");
            Check.Equal(1, read.Count, "短语条数");
            Check.Equal("yisitalu", read[0].Pinyin, "拼音往返");
            Check.Equal(2, read[0].Position, "位置往返");
            Check.Equal("伊斯塔露", read[0].Text, "文本往返");

            // 记录头的字节布局是实测验证过的：位置是偏移 6 的 u32，偏移 10 固定 0x0006。
            // 这两处写错会让输入法整条忽略（短语不出现），所以锁死。
            var raw = File.ReadAllBytes(Path.Combine(directory, "test.lex"));
            Check.Equal("mschxudp", System.Text.Encoding.ASCII.GetString(raw, 0, 8), "文件魔数");
            Check.Equal(1, BitConverter.ToInt32(raw, 0x1C), "头部短语计数");
            Check.Equal((ushort)0x0010, BitConverter.ToUInt16(raw, 0x44), "记录头起始标记");
            Check.Equal(2, BitConverter.ToInt32(raw, 0x44 + 6), "位置在记录头偏移 6");
            Check.Equal((ushort)0x0006, BitConverter.ToUInt16(raw, 0x44 + 10), "记录头偏移 10 固定为 0x0006");

            udp.Merge(new[] { new MspyPhrase("ystl", 2, "伊斯塔露") }, false, out _);
            Check.Equal(2, udp.Read(out _).Count, "合并不丢已有条目");

            udp.Merge(new[] { new MspyPhrase("ystl", 3, "别的词") }, false, out var conflicts);
            Check.Equal(1, conflicts.Count, "同拼音不同文本判冲突");
            Check.Equal("伊斯塔露", udp.Read(out _).First(p => p.Pinyin == "ystl").Text, "冲突不覆盖");

            udp.Merge(new[] { new MspyPhrase("ystl", 4, "伊斯塔露") }, false, out _);
            Check.Equal(4, udp.Read(out _).First(p => p.Pinyin == "ystl").Position, "同文本可更新位置");

            // 简拼变体：输入法只按拼音做字符串匹配，简拼必须单独写一条。
            Check.Equal("ystl", PhraseVariants.InitialsOf("yisitalu"), "全拼转简拼");
            Check.Equal("zg", PhraseVariants.InitialsOf("zhongguo"), "两字词简拼");

            // 触发码必须保持原字母：这里若走简拼索引的模糊音归一，会变成 hls / wyls，
            // 用户按 fns 反而打不出来。
            Check.Equal("fns", PhraseVariants.InitialsOf("faniesi"), "f 不应被归一成 h");
            Check.Equal("wyns", PhraseVariants.InitialsOf("woyanisha"), "n 不应被归一成 l");
            Check.True(PhraseVariants.InitialsOf("wo") is null, "单音节不值得补简拼");
            Check.True(PhraseVariants.InitialsOf("xyz") is null, "切不出音节时不补");

            var pair = PhraseVariants.Expand(
                new MspyPhrase("yisitalu", 2, "伊斯塔露"),
                withInitials: true);
            Check.Equal(2, pair.Count, "展开为两条");
            Check.Equal("ystl", pair[1].Pinyin, "第二条是简拼");
            Check.Equal("伊斯塔露", pair[1].Text, "两条文本一致");
            Check.Equal(
                1,
                PhraseVariants.Expand(new MspyPhrase("yisitalu", 2, "伊斯塔露"), withInitials: false).Count,
                "关闭时只写全拼");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        var builtin = BinaryLexicon.LoadDefault(table, out _);
        var composite = new CompositePinyinLexicon(builtin, book);
        Check.True(composite.IsLoaded, "组合词库跟随内置");
        var expanded = composite.ExpandAbbreviation("ystl", 4);
        Check.True(expanded.Count > 0 && expanded[0] == "yisitalu", "用户词排在展开首位");

        var emptyBuiltin = new CompositePinyinLexicon(NullPinyinLexicon.Instance, book);
        Check.False(emptyBuiltin.IsLoaded, "内置缺失时组合词库仍为未加载");

        // 融合：词库只补空位，云端整词匹配仍然优先。
        var matcher = new PinyinMatcher(builtin, fuzzyEnabled: false);
        var options = SuggestionSelectionOptions.Default;
        Check.Equal(PinyinMatchKind.Strict, matcher.Match("yisitalu", "伊斯塔露").Kind, "直接匹配应为 Strict");

        var offline = SuggestionSelector.Select(
            "yisitalu",
            Array.Empty<string>(),
            null,
            matcher,
            options,
            providerId: "bing",
            wordbook: new[] { "伊斯塔露" });
        Check.True(offline.Strict is not null, $"云端为空时词库补严格槽（诊断：{offline.Diagnostic}，条数 {offline.Count}）");
        Check.Equal(SuggestionSelector.WordbookProviderId, offline.Strict!.ProviderId, "来源标记为词库");
        Check.Equal("yisitalu", offline.Strict.Reading, "Reading 记录实际请求串");

        var cloudWins = SuggestionSelector.Select(
            "yisitalu",
            new[] { "伊斯塔路" },
            null,
            matcher,
            options,
            providerId: "bing",
            wordbook: new[] { "伊斯塔露" });
        Check.Equal("伊斯塔路", cloudWins.Strict?.Text, "云端整词匹配优先于词库");
        Check.Equal("yisitalu", cloudWins.Strict?.Reading, "云端 Reading 同样是请求串");

        var none = SuggestionSelector.Select(
            "yisitalu",
            Array.Empty<string>(),
            null,
            matcher,
            options);
        Check.True(none.IsEmpty, "没有云端也没有词库时为空");
    }

    private static void Catalog()
    {
        var table = SyllableTable.Default;
        var lexicon = BinaryLexicon.LoadDefault(table, out var error);
        Check.True(lexicon.IsLoaded, $"词库应能加载：{error}");
        Check.Equal(150000, lexicon.WordCount, "词条数");

        var catalog = (IWordCatalog)lexicon;
        Check.True(catalog.HasWordTable, "v2 词库应带词表");
        Check.Equal(150000, catalog.Count, "词表条数");

        Check.True(catalog.TryGet(0, out var first), "应能取到首条");
        Check.True(first.Text.Length > 0, "首条词面非空");
        Check.True(first.Pinyin.Length > 0 && first.Pinyin.All(char.IsAsciiLetterLower), "首条拼音为小写字母");
        Check.Equal(0, first.Index, "首条下标为 0");
        Check.False(catalog.TryGet(-1, out _), "负下标应失败");
        Check.False(catalog.TryGet(catalog.Count, out _), "越界下标应失败");

        var byFull = catalog.Search("zhongguo", 10);
        Check.True(byFull.Count > 0 && byFull[0] == 1, "全拼前缀应命中「中国」");
        Check.True(catalog.Search("zg", 10).Contains(1), "声母前缀应命中「中国」");
        var byHan = catalog.Search("中国", 5);
        Check.True(byHan.Count > 0 && byHan[0] == 1, "汉字子串应命中「中国」");
        Check.Equal(0, catalog.Search("vvvv", 10).Count, "无关查询应为空");

        var head = catalog.Search(string.Empty, 5);
        Check.Equal(5, head.Count, "空查询返回前 5 条");
        Check.Equal(0, head[0], "空查询按下标升序");
        Check.Equal(4, head[4], "空查询按下标升序");

        var path = Path.Combine(AppContext.BaseDirectory, "lexicon.bin");

        // v1 兼容：只改版本号与区段数，前三个区段保持原样。
        var legacy = Path.Combine(Path.GetTempPath(), $"bp-lex-v1-{Guid.NewGuid():N}.bin");
        try
        {
            var bytes = File.ReadAllBytes(path);
            bytes[4] = 1;
            bytes[5] = 0;
            bytes[6] = 3;
            bytes[7] = 0;
            File.WriteAllBytes(legacy, bytes);

            var v1 = BinaryLexicon.TryLoad(legacy, table, out var v1Error);
            Check.True(v1 is not null, $"v1 文件应能加载：{v1Error}");
            Check.False(v1!.HasWordTable, "v1 没有词表");
            Check.Equal(0, v1.Count, "v1 词表条数为 0");
            Check.Equal(150000, v1.WordCount, "v1 词条计数仍可读");
            Check.True(v1.ExpandAbbreviation("ystl", 2).Count > 0, "v1 简拼展开照常");
        }
        finally
        {
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
            }
        }

        // 词表记录长度被改成不支持的值：只降级词表，整库仍可用。
        var broken = Path.Combine(Path.GetTempPath(), $"bp-lex-broken-{Guid.NewGuid():N}.bin");
        try
        {
            var bytes = File.ReadAllBytes(path);
            var entry = LexiconFormat.HeaderSize + (3 * LexiconFormat.SectionEntrySize);
            var sectionOffset = (int)BitConverter.ToUInt32(bytes, entry);
            BitConverter.GetBytes(20u).CopyTo(bytes, sectionOffset + 12);
            File.WriteAllBytes(broken, bytes);

            var partial = BinaryLexicon.TryLoad(broken, table, out var brokenError);
            Check.True(partial is not null, $"词表损坏应只降级词表：{brokenError}");
            if (partial is not null)
            {
                Check.False(partial.HasWordTable, "损坏后没有词表");
                Check.Equal(150000, partial.WordCount, "损坏后读音与简拼仍可用");
            }
        }
        finally
        {
            if (File.Exists(broken))
            {
                File.Delete(broken);
            }
        }
    }

    /// <summary>自学习词库（ChsPinyinUDL.dat）的解析、拼音推断与导出。</summary>
    private static void Learning()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bp-learn-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "ChsPinyinUDL.dat");
        Directory.CreateDirectory(directory);
        try
        {
            // 第三条故意标成空槽（0xAA），读取时应当被跳过。
            var data = BuildLearningFile(
                ("伊斯塔露", "yst", 4),
                ("若娜瓦", "rnw", 3),
                ("多余词", "dyc", 3));
            data[0x2400 + (2 * 60) + 11] = 0xAA;
            File.WriteAllBytes(path, data);

            var store = new MspyLearningStore(path);
            Check.True(store.TryRead(out var words, out var error), $"应能读出：{error}");
            Check.Equal(2, words.Count, "空槽应被跳过");
            Check.Equal("伊斯塔露", words[0].Text, "首条词面");
            Check.Equal("yst", words[0].Jianpin, "简拼");
            Check.Equal(4, words[0].Length, "字数");

            var lexicon = FakeLexicon.Create();
            Check.Equal(
                "yisitalu",
                MspyLearningStore.ResolvePinyin("伊斯塔露", "yst", lexicon),
                "拼音应按简拼首字母消歧");
            Check.Equal(
                "ruonawa",
                MspyLearningStore.ResolvePinyin("若娜瓦", "rnw", lexicon),
                "第二条拼音");

            // 文件头不对必须整份拒绝，而不是读出一堆乱码。
            var brokenPath = Path.Combine(directory, "broken.dat");
            File.WriteAllBytes(brokenPath, new byte[0x2400]);
            Check.False(new MspyLearningStore(brokenPath).TryRead(out _, out var brokenError), "空文件应被拒绝");
            Check.True(brokenError is not null, "拒绝时应给出原因");

            // 记录数超出文件容量同样拒绝，避免越界读。
            var oversized = BuildLearningFile(("伊斯塔露", "yst", 4));
            BitConverter.GetBytes(99u).CopyTo(oversized, 0x0C);
            File.WriteAllBytes(brokenPath, oversized);
            Check.False(new MspyLearningStore(brokenPath).TryRead(out _, out _), "记录数越界应拒绝");

            var csvPath = Path.Combine(directory, "learned.csv");
            Check.True(MspyLearningStore.ExportCsv(words, csvPath, lexicon), "导出 CSV");
            var lines = File.ReadAllLines(csvPath);
            Check.Equal(3, lines.Length, "CSV 应为表头 + 两条");
            Check.True(lines[1].Contains("伊斯塔露,yisitalu", StringComparison.Ordinal), "CSV 第一行内容");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 删除自学习词库里的条目：只改记录区，槽位数不变，其它条目原样保留。
    /// </summary>
    private static void LearningDelete()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bp-learn-del-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "ChsPinyinUDL.dat");
        var backupDirectory = Path.Combine(directory, "backup");
        Directory.CreateDirectory(directory);
        try
        {
            var data = BuildLearningFile(
                ("伊斯塔露", "yst", 4),
                ("若娜瓦", "rnw", 3),
                ("多余词", "dyc", 3));
            File.WriteAllBytes(path, data);

            var store = new MspyLearningStore(path, backupDirectory);
            Check.True(
                store.TryRemove(new[] { "若娜瓦" }, out var removed, out var error),
                $"删除应成功：{error}");
            Check.Equal(1, removed, "删除 1 条");

            var after = File.ReadAllBytes(path);
            Check.Equal(data.Length, after.Length, "文件长度不变");
            Check.True(after.AsSpan(0, 0x0C).SequenceEqual(data.AsSpan(0, 0x0C)), "文件头不被改动");
            Check.Equal(3u, BitConverter.ToUInt32(after, 0x0C), "槽位数保持 3");

            var slot = 0x2400 + 60;
            Check.Equal((byte)0xAA, after[slot + 11], "被删槽位标记为空");
            Check.True(
                after.AsSpan(slot, 11).ToArray().All(b => b == 0) &&
                after.AsSpan(slot + 12, 48).ToArray().All(b => b == 0),
                "被删槽位内容清零");
            Check.True(
                after.AsSpan(0x2400, 60).SequenceEqual(data.AsSpan(0x2400, 60)),
                "前一条不受影响");
            Check.True(
                after.AsSpan(0x2400 + 120, 60).SequenceEqual(data.AsSpan(0x2400 + 120, 60)),
                "后一条不受影响");

            Check.True(store.TryRead(out var rest, out _), "删除后仍可读");
            Check.Equal(2, rest.Count, "只剩 2 条");
            Check.True(rest.All(w => w.Text != "若娜瓦"), "被删的词不再出现");

            // 删除不存在的词不算失败，也不写文件。
            Check.True(store.TryRemove(new[] { "没有这个词" }, out var again, out _), "删不存在的词应成功");
            Check.Equal(0, again, "没有删到东西");
            Check.True(
                File.ReadAllBytes(path).AsSpan().SequenceEqual(after),
                "没删到东西时不应改动文件");

            // 删除前必须留下备份。
            var backups = Directory.Exists(backupDirectory)
                ? Directory.GetFiles(backupDirectory, "*.bak.dat")
                : Array.Empty<string>();
            Check.Equal(1, backups.Length, "应生成一份备份");
            Check.True(
                File.ReadAllBytes(backups[0]).SequenceEqual(data),
                "备份应是删除前的内容");

            // 小端计数被破坏时必须拒绝写入，而不是把文件写成垃圾。
            var broken = Path.Combine(directory, "broken.dat");
            var brokenData = BuildLearningFile(("伊斯塔露", "yst", 4));
            BitConverter.GetBytes(99u).CopyTo(brokenData, 0x0C);
            File.WriteAllBytes(broken, brokenData);
            Check.False(
                new MspyLearningStore(broken, backupDirectory).TryRemove(new[] { "伊斯塔露" }, out _, out var brokenError),
                "记录数越界应拒绝删除");
            Check.True(brokenError is not null, "拒绝时应给出原因");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 开机启动登记：写入、识别旧路径、覆盖、删除，以及拒绝不存在的程序。
    /// </summary>
    private static void StartupRegistrationTest()
    {
        var keyPath = $@"Software\BetterPinyin.Tests\{Guid.NewGuid():N}";
        var valueName = "Startup";
        var current = Environment.ProcessPath!;
        var other = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        try
        {
            Check.Equal(
                "\"C:\\Program Files\\A B\\x.exe\" --autostart",
                StartupRegistration.FormatCommand(@"C:\Program Files\A B\x.exe", "--autostart"),
                "路径带空格时要加引号");

            var registration = new StartupRegistration(current, "--autostart", keyPath, valueName);
            Check.True(registration.TryProbe(out var initial, out var probeError),
                $"应能读取启动项：{probeError}");
            Check.False(initial.Registered, "初始未登记");

            Check.True(registration.TryEnable(out var enableError), $"写入启动项应成功：{enableError}");
            Check.True(registration.TryProbe(out var enabled, out _), "写入后可读");
            Check.True(enabled.Registered, "已登记");
            Check.True(enabled.MatchesCurrent, "命令指向当前程序");
            Check.Equal($"\"{current}\" --autostart", enabled.Command, "命令内容");

            // 程序换位置：旧命令还在，但已经不匹配；再次 Enable 应该覆盖。
            var moved = new StartupRegistration(other, "--autostart", keyPath, valueName);
            Check.True(moved.TryProbe(out var stale, out _), "换路径后可读");
            Check.True(stale.Registered, "依旧是已登记");
            Check.False(stale.MatchesCurrent, "旧路径与当前不匹配");

            Check.True(moved.TryEnable(out _), "覆盖为新路径");
            Check.True(moved.TryProbe(out var updated, out _) && updated.MatchesCurrent, "命令已更新");

            Check.True(moved.TryDisable(out var disableError), $"删除启动项应成功：{disableError}");
            Check.True(moved.TryProbe(out var disabled, out _), "删除后可读");
            Check.False(disabled.Registered, "已删除");
            Check.True(moved.TryDisable(out _), "重复关闭不报错");

            // 不存在的 exe 不应登记，否则每次开机都会静默失败。
            var missing = new StartupRegistration(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"),
                null,
                keyPath,
                valueName);
            Check.False(missing.TryEnable(out var missingError), "不存在的路径拒绝登记");
            Check.True(missingError is not null, "拒绝时给出原因");
        }
        finally
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            baseKey.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// 自学习词库的备份：列表、差异、恢复、删除，以及长度不同的恢复。
    /// </summary>
    private static void LearningBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bp-learn-bak-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "ChsPinyinUDL.dat");
        var backupDirectory = Path.Combine(directory, "backup");
        Directory.CreateDirectory(directory);
        try
        {
            var original = BuildLearningFile(
                ("伊斯塔露", "yst", 4),
                ("若娜瓦", "rnw", 3),
                ("多余词", "dyc", 3));
            File.WriteAllBytes(path, original);

            // 备份文件名才是备份时刻：文件复制会连源文件的修改时间一起带过来，
            // 这里故意把源文件改成 3 天前，若按 mtime 解析就会露馅。
            File.SetLastWriteTime(path, DateTime.Now.AddDays(-3));

            var store = new MspyLearningStore(path, backupDirectory);
            Check.True(store.TryRemove(new[] { "若娜瓦" }, out var removed, out _), "删除应成功");
            Check.Equal(1, removed, "删掉 1 条");

            var backups = store.ListBackups();
            Check.Equal(1, backups.Count, "应有一份备份");
            Check.Equal(3, backups[0].WordCount, "备份里是删除前的 3 条");
            Check.Equal(DateTimeOffset.Now.Date, backups[0].CreatedAt.Date, "备份时间是今天");

            Check.True(MspyLearningStore.TryReadFile(backups[0].FilePath, out var backupWords, out _), "备份可读");
            Check.True(store.TryRead(out var current, out _), "当前可读");

            var diff = MspyLearningStore.Diff(backupWords, current);
            Check.Equal(1, diff.Missing.Count, "恢复会回来 1 条");
            Check.Equal("若娜瓦", diff.Missing[0], "回来的词");
            Check.Equal(0, diff.Extra.Count, "没有会消失的词");
            Check.False(diff.IsEmpty, "两份词库存在差异");
            Check.True(MspyLearningStore.Diff(backupWords, backupWords).IsEmpty, "同一份词库无差异");

            // 恢复：内容回到 3 条；恢复前会把「2 条」的现状也留一份。
            Check.True(store.TryRestore(backups[0].FilePath, out var restored, out var restoreError),
                $"恢复应成功：{restoreError}");
            Check.Equal(3, restored, "恢复后 3 条");
            Check.True(store.TryRead(out var afterRestore, out _), "恢复后可读");
            Check.True(afterRestore.Any(w => w.Text == "若娜瓦"), "被删的词回来了");

            var preRestore = store.ListBackups().FirstOrDefault(b => b.WordCount == 2);
            Check.True(preRestore is not null, "恢复前又备份了一份 2 条的现状");

            // 删除备份；备份目录外的文件一律拒绝。
            Check.True(store.TryDeleteBackup(preRestore!.FilePath, out var deleteError), $"删除备份应成功：{deleteError}");
            Check.False(File.Exists(preRestore.FilePath), "备份文件已删除");
            Check.False(store.TryDeleteBackup(path, out var outsideError), "不允许删除备份目录外的文件");
            Check.True(outsideError is not null, "拒绝时给出原因");

            // 长度不同的备份也要能恢复：先换成 1 条的文件，再恢复 3 条的备份。
            var shrink = BuildLearningFile(("伊斯塔露", "yst", 4));
            File.WriteAllBytes(path, shrink);
            Check.True(store.TryRestore(backups[0].FilePath, out var again, out var lengthError),
                $"长度不同的恢复应成功：{lengthError}");
            Check.Equal(3, again, "恢复 3 条");
            Check.Equal(original.Length, new FileInfo(path).Length, "文件长度回到备份大小");
            Check.True(File.ReadAllBytes(path).SequenceEqual(original), "内容与备份完全一致");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>拼一个只有头部与若干条记录的自学习词库文件。</summary>
    private static byte[] BuildLearningFile(params (string Text, string Jianpin, int Length)[] entries)
    {
        var data = new byte[0x2400 + (entries.Length * 60)];
        new byte[] { 0x55, 0xAA, 0x88, 0x81 }.CopyTo(data, 0);
        new byte[] { 0x02, 0x00, 0x60, 0x00 }.CopyTo(data, 4);
        new byte[] { 0x55, 0xAA, 0x55, 0xAA }.CopyTo(data, 8);
        BitConverter.GetBytes((uint)entries.Length).CopyTo(data, 0x0C);

        for (var i = 0; i < entries.Length; i++)
        {
            var (text, jianpin, length) = entries[i];
            var offset = 0x2400 + (i * 60);
            System.Text.Encoding.ASCII.GetBytes(jianpin).CopyTo(data, offset + 4);
            data[offset + 9] = 0x04;
            data[offset + 10] = (byte)length;
            data[offset + 11] = 0x5A;
            System.Text.Encoding.Unicode.GetBytes(text).CopyTo(data, offset + 12);
        }

        return data;
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
        Check.Equal("伊斯塔露", ResultSanitizer.Normalize("伊斯塔露\u3000原神"), "全角空格也应截断");
        Check.True(ResultSanitizer.IsAllChinese("伊斯塔露"), "中文判定");
        Check.False(ResultSanitizer.IsAllChinese("伊斯塔abc"), "混合串不是纯中文");

        // 扩展模式放宽到 12 字，严格模式仍是 8 字。
        var longText = new[] { "伊斯塔露英文名字大全" };
        Check.Equal(0, ResultSanitizer.Sanitize(longText, "x").Count, "默认上限应丢弃 10 字候选");
        Check.Equal(1, ResultSanitizer.Sanitize(longText, "x", SanitizeOptions.Extended).Count,
            "扩展模式应保留 10 字候选");

        // 简拼展开后，回显的是展开串（rineiwa），不是用户输入的 rnw。
        var expandedEcho = ResultSanitizer.Sanitize(
            new[] { "rineiwa", "日内瓦公约" },
            "rineiwa",
            SanitizeOptions.Extended);
        Check.Equal(1, expandedEcho.Count, "展开串的回显应被剔除");
        Check.Equal("日内瓦公约", expandedEcho[0], "回显剔除后应保留中文候选");
    }

    private static void Matcher()
    {
        var lexicon = FakeLexicon.Create();
        var fuzzy = new PinyinMatcher(lexicon, fuzzyEnabled: true);

        Expect(fuzzy, "yisitalu", "伊斯塔露", PinyinMatchKind.Strict);
        Expect(fuzzy, "yisital", "伊斯塔露", PinyinMatchKind.StrictPartialTail);
        Expect(fuzzy, "yisita", "伊斯塔露", PinyinMatchKind.Extended, extra: 1);
        Expect(fuzzy, "ystl", "伊斯塔露", PinyinMatchKind.Abbreviation);
        Expect(fuzzy, "yisitalu", "伊斯塔", PinyinMatchKind.None);

        // 输入的切分本来就不唯一：xian 既是「先」也是「西安」。
        Expect(fuzzy, "xian", "西安", PinyinMatchKind.Strict);
        Expect(fuzzy, "fangan", "方案", PinyinMatchKind.Strict);
        Expect(fuzzy, "fangan", "反感", PinyinMatchKind.Strict);

        // 多音字按读音集合匹配，不是取第一个读音。
        Expect(fuzzy, "yinhang", "银行", PinyinMatchKind.Strict);
        Expect(fuzzy, "yinxing", "银行", PinyinMatchKind.Strict);

        // ü 的两种写法等价。
        Expect(fuzzy, "lvse", "绿色", PinyinMatchKind.Strict);
        Expect(fuzzy, "luse", "绿色", PinyinMatchKind.Strict);

        // 模糊音变体拼写。
        Expect(fuzzy, "zuang", "装", PinyinMatchKind.Strict);
        Expect(fuzzy, "zongguo", "中国", PinyinMatchKind.Strict);

        // 儿化。
        Expect(fuzzy, "huar", "花儿", PinyinMatchKind.Strict);
        Expect(fuzzy, "huaer", "花儿", PinyinMatchKind.Strict);

        // 扩展词的边界：残段只许出现在候选最后一个音节。
        Expect(fuzzy, "ruonawa", "若娜瓦", PinyinMatchKind.Strict);
        Expect(fuzzy, "ruonaw", "若娜瓦壁纸", PinyinMatchKind.None);
        Expect(fuzzy, "yisital", "伊斯塔露英文", PinyinMatchKind.None);
        Expect(fuzzy, "lianxiang", "联想电脑管家", PinyinMatchKind.Extended, extra: 4);
        Expect(fuzzy, "ruonawa", "若娜瓦壁纸", PinyinMatchKind.Extended, extra: 2);

        var strict = new PinyinMatcher(lexicon, fuzzyEnabled: false);
        Check.Equal(PinyinMatchKind.None, strict.Match("zongguo", "中国").Kind, "关闭模糊音后 z/zh 不应互通");
        Check.Equal(PinyinMatchKind.None, strict.Match("zuang", "装").Kind, "关闭模糊音后变体拼写不应命中");
        Check.Equal(PinyinMatchKind.Strict, strict.Match("yinhang", "银行").Kind, "多音字与模糊音无关");

        // 没有词库时不能拦掉所有候选，应放行交给其它规则。
        var noLexicon = new PinyinMatcher(NullPinyinLexicon.Instance);
        Check.False(noLexicon.IsAvailable, "空词库应报告不可用");
        Check.Equal(PinyinMatchKind.Permissive, noLexicon.Match("whatever", "任何词").Kind, "空词库应放行");

        // 未收录的字不能猜读音，严格模式直接拒绝。
        Check.Equal(PinyinMatchKind.None, fuzzy.Match("ceshi", "测试").Kind, "未收录字应不匹配");
    }

    private static void Selector()
    {
        var matcher = new PinyinMatcher(FakeLexicon.Create());
        var options = new SuggestionSelectionOptions(
            StrictEnabled: true,
            StrictDedupeLocal: true,
            ExtendedEnabled: true,
            ExtendedMaxCount: 2);

        // 用户本来就能选到「若娜瓦」：严格槽必须留空，而且不能换成同音的「若纳瓦」。
        var deduped = SuggestionSelector.Select(
            "ruonawa",
            new[] { "若娜瓦", "若纳瓦", "若娜瓦壁纸", "若娜瓦英文", "若那瓦壁纸" },
            localCandidates: new[] { "若娜瓦" },
            matcher,
            options);

        Check.True(deduped.Strict is null, "本地已有严格词时应不显示");
        Check.Equal(2, deduped.Extended.Count, "扩展条数应受上限约束");
        Check.Equal("若娜瓦壁纸", deduped.Extended[0].Text, "扩展首条应保持 Bing 顺序");
        Check.NotContains(deduped.Extended.Select(e => e.Text), "若纳瓦", "同音整词不应进扩展槽");

        // 关掉去重后，严格槽应显示。
        var keepDuplicate = SuggestionSelector.Select(
            "ruonawa",
            new[] { "若娜瓦", "若纳瓦" },
            localCandidates: new[] { "若娜瓦" },
            matcher,
            options with { StrictDedupeLocal = false });
        Check.Equal("若娜瓦", keepDuplicate.Strict?.Text, "关闭去重后应显示严格词");

        // 严格槽只取第一条，扩展槽排除严格已选中的与本地已有的。
        var mixed = SuggestionSelector.Select(
            "yisitalu",
            new[] { "伊斯塔露", "伊斯塔路是谁", "伊斯塔露英文", "伊斯塔露" },
            localCandidates: Array.Empty<string>(),
            matcher,
            options with { ExtendedMaxCount = 3 });

        Check.Equal("伊斯塔露", mixed.Strict?.Text, "严格槽应取第一条整词匹配");
        Check.Equal(2, mixed.Extended.Count, "扩展槽应排除重复项");
        Check.Equal("伊斯塔路是谁", mixed.Extended[0].Text, "扩展槽保持 Bing 顺序");

        // 扩展关闭时只剩严格槽。
        var strictOnly = SuggestionSelector.Select(
            "yisita",
            new[] { "伊斯塔露", "伊斯塔" },
            null,
            matcher,
            options with { ExtendedEnabled = false });
        Check.True(strictOnly.Extended.Count == 0, "扩展关闭时不应产生扩展项");
        Check.Equal("伊斯塔", strictOnly.Strict?.Text, "严格槽应取整词匹配");

        // 两个都关掉时结果为空，且诊断可读。
        var none = SuggestionSelector.Select(
            "yisita",
            new[] { "伊斯塔" },
            null,
            matcher,
            options with { StrictEnabled = false, ExtendedEnabled = false });
        Check.True(none.IsEmpty, "两个槽都关闭时应为空");
        Check.True(none.Diagnostic.Contains("关闭", StringComparison.Ordinal), "诊断应说明已关闭");

        // 简拼回归：rnw 经词库展开成 rineiwa 后，必须按展开串校验才能匹配「日内瓦公约」。
        var genevaMatch = matcher.Match("rineiwa", "日内瓦公约");
        Check.Equal(PinyinMatchKind.Extended, genevaMatch.Kind, "展开串应命中扩展档");
        Check.Equal(2, genevaMatch.ExtraSyllables, "日内瓦公约比 rineiwa 多两个音节");

        var expanded = SuggestionSelector.Select(
            "rineiwa",
            new[] { "日内瓦", "日内瓦公约", "日内瓦会议" },
            localCandidates: new[] { "日内瓦" },
            matcher,
            options);
        Check.True(expanded.Strict is null, "本地已有「日内瓦」时严格槽应为空");
        Check.Contains(expanded.Extended.Select(e => e.Text), "日内瓦公约", "展开串应能进扩展槽");
        Check.Equal("日内瓦公约", expanded.Extended[0].Text, "扩展槽应保持 Bing 原序");

        // 反过来用 raw 校验必然为空，这是修复前的行为，留作护栏防止改回去。
        var rawAbbreviation = SuggestionSelector.Select(
            "rnw",
            new[] { "日内瓦公约" },
            localCandidates: Array.Empty<string>(),
            matcher,
            options);
        Check.True(rawAbbreviation.IsEmpty, "3 字母简拼不应直接匹配 5 音节词");
    }

    private static void Ranker()
    {
        var matcher = new PinyinMatcher(FakeLexicon.Create());
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
            Check.True(defaults.StrictEnabled, "默认应启用云联想（严格）");
            Check.True(!defaults.ExtendedEnabled, "扩展云联想默认关闭，需要的人自己打开");
            Check.Equal("Oem3", defaults.StrictAcceptKey, "默认采纳键应为 ·");
            Check.Equal(3, defaults.ExtendedMaxCount, "扩展默认 3 条");
            Check.True(!defaults.EnglishModeAssistEnabled, "英文模式默认关闭");
            Check.True(defaults.UpdateCheckEnabled, "默认开启启动时检查更新");
            Check.True(defaults.LastUpdateCheckUtc is null, "默认没有检查记录");
            Check.True(!defaults.PhraseWriteExtended, "扩展词默认不写入自定义短语");
            Check.Equal("bing", defaults.StrictProvider, "云联想来源默认 Bing");
            Check.Equal("sogou", CloudSourceCatalog.Normalize("SOGOU"), "来源取值应忽略大小写");
            Check.Equal("bing", CloudSourceCatalog.Normalize("baidu"), "未知来源应回落到 Bing");
            Check.Equal("bing", CloudSourceCatalog.Normalize(null), "空来源应回落到 Bing");
            Check.True(store.LastError is null, "文件不存在时不应报错");

            defaults.StrictAcceptKey = "Semicolon";
            defaults.ExtendedMaxCount = 2;
            defaults.OverlayPlacement = "Left";
            defaults.PhraseWriteExtended = true;
            defaults.BlockedProcesses.Add("mstsc");
            Check.True(store.Save(defaults), "保存应成功");

            var reloaded = new SettingsStore(path).Load();
            Check.Equal("Semicolon", reloaded.StrictAcceptKey, "采纳键应持久化");
            Check.Equal(2, reloaded.ExtendedMaxCount, "扩展条数应持久化");
            Check.Equal("Left", reloaded.OverlayPlacement, "浮窗位置应持久化");
            Check.True(reloaded.PhraseWriteExtended, "扩展词写入开关应持久化");
            Check.Contains(reloaded.BlockedProcesses, "mstsc", "黑名单应持久化");

            // 老配置里没有 StrictProvider：必须读到默认值，且不影响其它字段。
            File.WriteAllText(path, "{\"StrictEnabled\":false}");
            var legacy = new SettingsStore(path).Load();
            Check.Equal("bing", legacy.StrictProvider, "老配置的来源应回落到 Bing");
            Check.False(legacy.StrictEnabled, "老配置的其它字段仍应正常读取");

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

    // 2026-11-16 实测响应，作为离线回归样本；搜狗改协议时这几条会先失败。
    private const string SogouNihaoHex =
        "4B000500000000000000000000090000000003000400604F7D590400DB007500040002000500010400604FF7530400DB007500040002000500010400E56C8E560400DB00750004000200050001";

    private const string SogouYanglikunHex =
        "5D00050000000000000000000009000000000300060068673D4E645706006B01AD00A30006000400060009000106006867CB7A645706006B01AD00A30006000400060009000106006867CB7A066606006B01AD00A300060004000600090001";

    private const string SogouChinaHex =
        "43000500000000000000000000090000000001000E002D4E4E53BA4E116C71518C54FD560E008B017D001001CC006700760070000E00050008000B000E0012001400170001";

    /// <summary>把十六进制串还原成字节。</summary>
    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static void Sogou()
    {
        // 组帧：与实测抓包逐字节对齐。
        var frame = SogouFrame.Build("nihao");
        Check.Equal(15, frame.Length, "nihao 帧长");
        Check.Equal((byte)15, frame[0], "帧头是总长");
        Check.Equal((byte)5, frame[8], "第 8 字节是拼音长度");
        Check.Equal("nihao", Encoding.ASCII.GetString(frame, 9, 5), "拼音原样写入");

        byte checksum = 0;
        for (var i = 0; i < frame.Length - 1; i++)
        {
            checksum ^= frame[i];
        }

        Check.Equal(checksum, frame[^1], "末字节是前序异或");
        Check.Equal(
            "0F00050000000001056E6968616F6F",
            Convert.ToHexString(frame),
            "整帧应与实测一致");

        Check.Equal(0, SogouFrame.Build(string.Empty).Length, "空串不组帧");
        Check.Equal(0, SogouFrame.Build(null).Length, "null 不组帧");
        Check.Equal(0, SogouFrame.Build("你好").Length, "汉字不组帧");
        Check.Equal(0, SogouFrame.Build("ni hao").Length, "空格不组帧");
        Check.Equal(
            0,
            SogouFrame.Build(new string('a', SogouFrame.MaxQueryBytes + 1)).Length,
            "超长不组帧");
        Check.Equal(15, SogouFrame.Build("NIHAO").Length, "大写应转小写后组帧");

        // 解帧：三份实测响应。
        var nihao = SogouResponseParser.Parse(Hex(SogouNihaoHex));
        Check.Equal(3, nihao.Count, "nihao 应解析出 3 条");
        Check.Equal("你好", nihao[0], "nihao 首条");

        var yang = SogouResponseParser.Parse(Hex(SogouYanglikunHex));
        Check.Equal(3, yang.Count, "yanglikun 应解析出 3 条");
        Check.Equal("杨立昆", yang[^1], "yanglikun 最后一条");

        var china = SogouResponseParser.Parse(Hex(SogouChinaHex));
        Check.Equal(1, china.Count, "长词应解析出 1 条");
        Check.Equal("中华人民共和国", china[0], "长词文本");

        // 截断：任何前缀都不能抛异常，条数也不能离谱。
        var bytes = Hex(SogouNihaoHex);
        for (var length = 0; length < bytes.Length; length++)
        {
            try
            {
                var partial = SogouResponseParser.Parse(bytes.AsSpan(0, length));
                Check.True(partial.Count <= 3, $"截断到 {length} 字节时条数应合理");
            }
            catch (Exception ex)
            {
                Check.True(false, $"截断到 {length} 字节时不应抛异常：{ex.GetType().Name}");
            }
        }

        // 畸形帧。
        Check.Equal(0, SogouResponseParser.Parse(Array.Empty<byte>()).Count, "空响应应返空");
        Check.Equal(0, SogouResponseParser.Parse(new byte[] { 0x16 }).Count, "长度不符应整帧拒绝");
        Check.Equal(0, SogouResponseParser.Parse(new byte[0x30]).Count, "全零帧应返空");

        var badCount = Hex(SogouNihaoHex);
        badCount[0x12] = 0xFF;
        badCount[0x13] = 0xFF;
        Check.Equal(0, SogouResponseParser.Parse(badCount).Count, "候选数异常应返空");

        var badLength = Hex(SogouNihaoHex);
        badLength[0x14] = 0xFF;
        badLength[0x15] = 0xFF;
        Check.Equal(0, SogouResponseParser.Parse(badLength).Count, "词长越界应停止解析");

        // 与清洗链路配合：解析结果必须能全量通过汉字过滤。
        var cleaned = ResultSanitizer.Sanitize(nihao, "nihao", SanitizeOptions.Extended);
        Check.Equal(3, cleaned.Count, "实测候选应通过清洗");
    }

    private static void SogouClient()
    {
        var body = Hex(SogouNihaoHex);
        string? method = null;
        string? contentType = null;
        string? url = null;
        byte[]? requestBody = null;

        using var http = new HttpClient(new StubHandler(request =>
        {
            method = request.Method.Method;
            contentType = request.Content?.Headers.ContentType?.MediaType;
            url = request.RequestUri?.ToString();
            requestBody = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
        }));

        var client = new SogouSuggestionClient(http, timeoutMilliseconds: 2000);
        var answer = client.QueryAsync("nihao", CancellationToken.None).GetAwaiter().GetResult();

        Check.Equal("sogou", answer.SourceId, "来源标识");
        Check.Equal("sogou", client.Id, "接口上的来源标识");
        Check.Equal(3, answer.Items.Count, "桩响应应解析出 3 条");
        Check.Equal("POST", method, "应使用 POST");
        Check.Equal("application/octet-stream", contentType, "请求体类型");
        Check.Equal(SogouFrame.Endpoint, url, "请求地址");
        Check.True(
            requestBody is not null && requestBody.SequenceEqual(SogouFrame.Build("nihao")),
            "请求体应与组帧一致");

        using var failing = new HttpClient(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var failed = new SogouSuggestionClient(failing, 2000)
            .QueryAsync("nihao", CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(0, failed.Items.Count, "非 200 应返空");
        Check.Equal("sogou", failed.SourceId, "失败时仍保留来源标识");

        using var garbage = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }),
        }));
        var junk = new SogouSuggestionClient(garbage, 2000)
            .QueryAsync("nihao", CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(0, junk.Items.Count, "垃圾响应应返空");

        // 组帧失败时不该发请求：双拼/汉字输入会走到这里。
        var touched = false;
        using var guarded = new HttpClient(new StubHandler(_ =>
        {
            touched = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var skipped = new SogouSuggestionClient(guarded, 2000)
            .QueryAsync("你好", CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(0, skipped.Items.Count, "非字母输入应返空");
        Check.False(touched, "非字母输入不应触发 HTTP");
    }

    private static void SelectorCombine()
    {
        var matcher = new PinyinMatcher(FakeLexicon.Create());
        var options = new SuggestionSelectionOptions(
            StrictEnabled: true,
            StrictDedupeLocal: true,
            ExtendedEnabled: true,
            ExtendedMaxCount: 2);

        // 严格槽来自搜狗（只有整词），扩展槽来自 Bing。
        var strictSet = SuggestionSelector.Select(
            "yisitalu",
            new[] { "伊斯塔露" },
            localCandidates: Array.Empty<string>(),
            matcher,
            options with { ExtendedEnabled = false },
            providerId: "sogou");

        var extendedSet = SuggestionSelector.Select(
            "yisitalu",
            new[] { "伊斯塔露", "伊斯塔路是谁", "伊斯塔露英文" },
            localCandidates: Array.Empty<string>(),
            matcher,
            options,
            providerId: "bing-osjson");

        var combined = SuggestionSelector.Combine(strictSet, extendedSet);

        Check.Equal("伊斯塔露", combined.Strict?.Text, "严格槽应取前者");
        Check.Equal("sogou", combined.Strict?.ProviderId, "严格槽来源");
        Check.Equal(2, combined.Extended.Count, "扩展槽应取后者");
        Check.True(combined.Extended.All(e => e.ProviderId == "bing-osjson"), "扩展槽来源");
        Check.NotContains(combined.Extended.Select(e => e.Text), "伊斯塔露", "扩展槽应剔除与严格槽重复的词");
        Check.True(
            combined.Diagnostic.Contains("严格=伊斯塔露", StringComparison.Ordinal),
            "诊断应包含严格槽文本");

        // 严格槽为空时不应吞掉扩展槽。
        var emptyStrict = SuggestionSelector.Select(
            "yisitalu",
            Array.Empty<string>(),
            null,
            matcher,
            options with { ExtendedEnabled = false },
            providerId: "sogou");

        var merged = SuggestionSelector.Combine(emptyStrict, extendedSet);
        Check.True(merged.Strict is null, "严格槽应保持为空");
        Check.Equal(2, merged.Extended.Count, "扩展槽应保留");
    }

    /// <summary>端点清单的护栏：失效端点不得再排到前面，端点级超时只能收紧。</summary>
    private static void BingEndpoints()
    {
        var endpoints = BingEndpointCatalog.CloudEndpoints;
        var ids = endpoints.Select(e => e.Id).ToArray();

        Check.Equal(4, ids.Length, "Bing 端点数量");
        Check.Equal("bing-qsonhs", ids[0], "首选端点应为 qsonhs");
        Check.Equal("bing-osjson", ids[^1], "osjson 已失效，只能作为最后兜底");
        Check.True(
            endpoints.Take(3).All(e => e.Format == BingResponseFormat.SearchBoxJson),
            "前三顺位都应是搜索框格式");

        // 端点级超时只能收紧：末位端点带 500ms，其余沿用全局。
        var legacy = endpoints[^1];
        Check.Equal(500, legacy.TimeoutMilliseconds ?? 0, "末位端点应有用 500ms 的收紧超时");
        Check.True(
            legacy.ResolveTimeout(1500) == 500,
            "全局 1500ms 时应压到 500ms");
        Check.True(
            legacy.ResolveTimeout(300) == 300,
            "全局比端点级更短时不得放宽到 500ms");
        Check.True(
            endpoints.Take(3).All(e => e.TimeoutMilliseconds is null),
            "正常端点不该有自己的超时，应跟随设置");

        // 行为验证：慢端点在端点级超时到点后必须放弃，不能拖到全局超时。
        // 用 400ms 的延迟对比 100ms 的预算，留足 4 倍余量，避免机器繁忙时抖动。
        const string body =
            """{"AS":{"Results":[{"Suggests":[{"Txt":"微软件"},{"Txt":"微软"}]}]}}""";
        var slowEndpoint = new BingEndpoint(
            "slow-test",
            "example.invalid",
            "/suggest",
            BingResponseFormat.SearchBoxJson,
            HasContentFilter: false,
            "测试用慢端点",
            TimeoutMilliseconds: 100);

        using var slowHttp = new HttpClient(new StubHandler(async (_, token) =>
        {
            await Task.Delay(400, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }));

        var slowClient = new BingSuggestionClient(
            slowHttp,
            endpoints: new[] { slowEndpoint },
            timeoutMilliseconds: 1500);

        var watch = Stopwatch.StartNew();
        var slowAnswer = slowClient.QueryAsync("weiruan", CancellationToken.None).GetAwaiter().GetResult();
        watch.Stop();

        Check.Equal(0, slowAnswer.Items.Count, "端点级超时到点应返空");
        Check.True(
            watch.ElapsedMilliseconds < 1000,
            $"应按端点级 100ms 截断，实际 {watch.ElapsedMilliseconds}ms");
    }

    /// <summary>完整性级别映射。方向错一次，提示语就会出现在完全正常的机器上。</summary>
    private static void Integrity()
    {
        Check.Equal(IntegrityLevel.Low, ProcessIntegrity.FromRid(0x1000), "0x1000 应为低");
        Check.Equal(IntegrityLevel.Medium, ProcessIntegrity.FromRid(0x2000), "0x2000 应为普通");
        Check.Equal(IntegrityLevel.High, ProcessIntegrity.FromRid(0x3000), "0x3000 应为管理员");
        Check.Equal(IntegrityLevel.System, ProcessIntegrity.FromRid(0x4000), "0x4000 应为系统");
        Check.Equal(IntegrityLevel.Unknown, ProcessIntegrity.FromRid(0), "0 应为未知");
        Check.Equal(IntegrityLevel.Unknown, ProcessIntegrity.FromRid(-1), "非法值应为未知");

        // 读不到级别时必须判成"不高于自己"，否则会在正常机器上骚扰用户。
        Check.False(ProcessIntegrity.IsAboveSelf(IntegrityLevel.Unknown), "未知不应判为更高");
        Check.False(ProcessIntegrity.IsAboveSelf(ProcessIntegrity.Current), "同级不算更高");

        var level = ProcessIntegrity.Current;
        Check.True(
            level is IntegrityLevel.Low or IntegrityLevel.Medium or IntegrityLevel.High or IntegrityLevel.System,
            $"本进程级别应可读，实际 {level}");
        Check.True(!string.IsNullOrEmpty(ProcessIntegrity.Describe(level)), "级别应有中文说明");
    }

    /// <summary>固定返回预设响应的 HTTP 桩，用来在不联网的情况下验证请求形状。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _responder(request, cancellationToken);
    }

    private static void Expect(
        PinyinMatcher matcher,
        string query,
        string candidate,
        PinyinMatchKind kind,
        int extra = 0)
    {
        var result = matcher.Match(query, candidate);
        Check.Equal(kind, result.Kind, $"{query} → {candidate}");
        if (kind == PinyinMatchKind.Extended)
        {
            Check.Equal(extra, result.ExtraSyllables, $"{query} → {candidate} 的多余音节数");
        }
    }
}

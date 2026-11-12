using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Cloud.Bing;
using BetterPinyin.Core.Lexicon;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Ranking;
using BetterPinyin.Core.Settings;
using BetterPinyin.Core.Wordbook;

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
        Run("[1/9] Bing 响应解析", Parser);
        Run("[2/9] 候选清洗", Sanitizer);
        Run("[3/9] 拼音匹配", Matcher);
        Run("[4/9] 严格 / 扩展分区", Selector);
        Run("[5/9] 排序合并（旧链路）", Ranker);
        Run("[6/9] 设置持久化", Settings);
        Run("[7/9] 词库词表", Catalog);
        Run("[8/9] 用户词库", Wordbook);
        Run("[9/9] 自学习词库", Learning);
    }

    /// <summary>逐组执行，一组崩掉不影响其它组出结论。</summary>
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
            Check.True(defaults.ExtendedEnabled, "默认应启用扩展云联想");
            Check.Equal("Oem3", defaults.StrictAcceptKey, "默认采纳键应为 ·");
            Check.Equal(3, defaults.ExtendedMaxCount, "扩展默认 3 条");
            Check.True(!defaults.EnglishModeAssistEnabled, "英文模式默认关闭");
            Check.True(store.LastError is null, "文件不存在时不应报错");

            defaults.StrictAcceptKey = "Semicolon";
            defaults.ExtendedMaxCount = 2;
            defaults.OverlayPlacement = "Left";
            defaults.BlockedProcesses.Add("mstsc");
            Check.True(store.Save(defaults), "保存应成功");

            var reloaded = new SettingsStore(path).Load();
            Check.Equal("Semicolon", reloaded.StrictAcceptKey, "采纳键应持久化");
            Check.Equal(2, reloaded.ExtendedMaxCount, "扩展条数应持久化");
            Check.Equal("Left", reloaded.OverlayPlacement, "浮窗位置应持久化");
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

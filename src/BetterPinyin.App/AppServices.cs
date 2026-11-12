using BetterPinyin.App.Features.CloudAssist;
using BetterPinyin.Core.Lexicon;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Settings;
using BetterPinyin.Core.Wordbook;
using BetterPinyin.Interop.Uia;

namespace BetterPinyin.App;

/// <summary>
/// 进程内的共享服务定位器。
/// </summary>
/// <remarks>
/// 这个程序规模不大，用一个显式的静态容器比引入完整的依赖注入框架更直接，
/// 也避免为了 DI 而增加第三方依赖。所有字段在 <see cref="Initialize"/> 中一次性装配。
/// </remarks>
internal static class AppServices
{
    private static SettingsStore? _store;

    /// <summary>当前设置。修改后调用 <see cref="SaveSettings"/> 落盘。</summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>设置读写器。</summary>
    public static SettingsStore Store => _store ??= new SettingsStore();

    /// <summary>UIA 专用 STA 线程。</summary>
    public static UiaWorker Uia { get; private set; } = null!;

    /// <summary>候选窗读取器。</summary>
    public static UiaCandidateWindowReader Reader { get; private set; } = null!;

    /// <summary>云联想控制器（功能启动后可用）。</summary>
    public static CloudAssistController? Controller { get; set; }

    /// <summary>UIA 是否可用，供诊断页展示。</summary>
    public static bool UiaAvailable { get; set; }

    /// <summary>内置读音词库 + 用户词库的组合；加载失败时为空词库，匹配器自动降级为放行。</summary>
    public static IPinyinLexicon Lexicon { get; private set; } = NullPinyinLexicon.Instance;

    /// <summary>词库加载失败原因，供诊断页展示。</summary>
    public static string? LexiconError { get; private set; }

    /// <summary>用户词库：采纳云词时自动记忆，也可在词库页增删改。</summary>
    public static UserWordbook Wordbook { get; private set; } = new();

    /// <summary>用户词库的磁盘存储。</summary>
    public static WordbookStore WordbookStorage { get; private set; } = new();

    /// <summary>微软拼音自定义短语的读写器。</summary>
    public static MspyPhraseStore Phrases { get; } = new();

    /// <summary>微软拼音自学习词库的只读读取器。</summary>
    public static MspyLearningStore Learning { get; } = new();

    /// <summary>串行化短语文件的写入，避免多次采纳并发改同一个文件。</summary>
    private static readonly object PhraseGate = new();

    /// <summary>装配共享服务。</summary>
    public static void Initialize()
    {
        Settings = Store.Load();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var builtin = BinaryLexicon.LoadDefault(SyllableTable.Default, out var lexiconError);
        LexiconError = lexiconError;

        Wordbook = WordbookStorage.Load();
        if (WordbookStorage.LastError is not null)
        {
            Log.Write($"用户词库读取失败，已按空词库启动：{WordbookStorage.LastError}");
        }

        Wordbook.Changed += (_, _) => WordbookStorage.ScheduleSave(Wordbook);
        Lexicon = new CompositePinyinLexicon(builtin, Wordbook);
        watch.Stop();

        Log.Write(Lexicon.IsLoaded
            ? $"词库已加载：内置 {builtin.WordCount} 词 / 用户 {Wordbook.Count} 词，{watch.ElapsedMilliseconds} ms"
            : $"词库未加载（{lexiconError}），读音校验降级为放行");

        Uia = new UiaWorker();
        Reader = new UiaCandidateWindowReader(Uia);
        UiaAvailable = Reader.Initialize();
    }

    /// <summary>按当前设置创建读音匹配器。</summary>
    /// <returns>匹配器实例。</returns>
    public static PinyinMatcher CreateMatcher() => new(Lexicon, Settings.FuzzyPinyin);

    /// <summary>保存设置到磁盘。</summary>
    /// <returns>保存成功返回 true。</returns>
    public static bool SaveSettings() => Store.Save(Settings);

    /// <summary>
    /// 把一条已采纳的词写进微软拼音的自定义短语。
    /// </summary>
    /// <param name="pinyin">无调全拼。</param>
    /// <param name="text">要输出的词。</param>
    /// <param name="position">写入的位置值（当前输入法不按它排序，仅记录）。</param>
    /// <returns>写入成功返回 true；同拼音已有不同短语时返回 false。</returns>
    /// <remarks>
    /// 写文件是「临时文件 + 原子替换」，几百条也就几毫秒，可以在采纳的后台线程里直接做。
    /// 失败只记日志，不影响上屏——词已经进用户词库，下次仍会重试。
    /// </remarks>
    public static bool TryWritePhrase(string pinyin, string text, int position)
    {
        if (string.IsNullOrWhiteSpace(pinyin) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        lock (PhraseGate)
        {
            var ok = Phrases.Merge(
                new[] { new MspyPhrase(pinyin, position, text) },
                overwriteOthers: false,
                out var conflicts);

            if (!ok)
            {
                Log.Write($"写入自定义短语失败：{text}（{pinyin}）— {Phrases.LastError}");
                return false;
            }

            if (conflicts.Count > 0)
            {
                Log.Write($"自定义短语已有同拼音条目，跳过：{pinyin} → {text}");
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 把用户词库里所有「待同步」的词一次性写进自定义短语。
    /// </summary>
    /// <returns>实际写入的条数。</returns>
    public static int SyncPendingPhrases()
    {
        var pending = Wordbook.Words
            .Where(w => w.UdpState == UdpSyncState.Pending && !w.IsHidden)
            .ToList();

        if (pending.Count == 0)
        {
            return 0;
        }

        int written;
        lock (PhraseGate)
        {
            var batch = pending
                .Select(w => new MspyPhrase(w.Pinyin, ResolvePosition(w), w.Text))
                .ToList();

            if (!Phrases.Merge(batch, overwriteOthers: false, out var conflicts))
            {
                Log.Write($"批量写入自定义短语失败：{Phrases.LastError}");
                return 0;
            }

            var blocked = conflicts
                .Select(c => c.Pinyin)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            written = 0;
            foreach (var word in pending)
            {
                if (blocked.Contains(word.Pinyin))
                {
                    Wordbook.SetUdpState(word.Text, UdpSyncState.Conflict);
                    continue;
                }

                Wordbook.SetUdpState(word.Text, UdpSyncState.Synced);
                written++;
            }
        }

        Log.Write($"已把 {written} 条用户词同步到微软拼音自定义短语");
        return written;
    }

    /// <summary>取出某个词写入时应该用的位置值。</summary>
    private static int ResolvePosition(UserWord word)
    {
        var global = Math.Clamp(Settings.WordbookSyncPosition, 1, 9);
        return word.UdpPosition is >= 1 and <= 9 ? word.UdpPosition : global;
    }

    /// <summary>释放 UIA 线程，并把用户词库的挂起改动落盘。</summary>
    public static void Shutdown()
    {
        Controller = null;
        WordbookStorage.Flush(Wordbook);
        WordbookStorage.Dispose();
        Uia?.Dispose();
    }
}

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

    /// <summary>释放 UIA 线程，并把用户词库的挂起改动落盘。</summary>
    public static void Shutdown()
    {
        Controller = null;
        WordbookStorage.Flush(Wordbook);
        WordbookStorage.Dispose();
        Uia?.Dispose();
    }
}

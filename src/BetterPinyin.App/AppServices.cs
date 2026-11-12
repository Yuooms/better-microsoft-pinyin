using System.IO;
using System.Reflection;
using BetterPinyin.App.Features.CloudAssist;
using BetterPinyin.Core.Lexicon;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Settings;
using BetterPinyin.Core.Wordbook;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Startup;
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

    /// <summary>微软拼音自学习词库的读写器（只支持删除，不支持新增）。</summary>
    public static MspyLearningStore Learning { get; } = new();

    /// <summary>当前用户的开机启动登记。</summary>
    public static StartupRegistration Startup { get; } = CreateStartupRegistration();

    /// <summary>串行化短语文件的写入，避免多次采纳并发改同一个文件。</summary>
    private static readonly object PhraseGate = new();

    /// <summary>装配共享服务。</summary>
    public static void Initialize()
    {
        Settings = Store.Load();
        EnsureStartupRegistration();

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
    /// 启动时校正开机启动登记：配置开着、但命令还指向旧路径（程序被移动或重新发布过）时，
    /// 按当前进程路径重写一遍。
    /// </summary>
    /// <remarks>
    /// 只在配置为「已开启」时动注册表，绝不擅自把用户没开的功能打开。
    /// 校验/写入失败只记日志：注册表被组策略锁住时，程序本身仍要能正常启动。
    /// </remarks>
    public static void EnsureStartupRegistration()
    {
        if (!Settings.StartWithWindows)
        {
            return;
        }

        if (!Startup.TryProbe(out var state, out var probeError))
        {
            Log.Write($"读取开机启动项失败：{probeError}");
            return;
        }

        if (!state.Registered)
        {
            // 启动项被外部工具删掉了：尊重这个结果，不要把用户没开的功能又写回去。
            Settings.StartWithWindows = false;
            SaveSettings();
            Log.Write("开机启动项已不存在，配置同步为关闭");
            return;
        }

        if (state.MatchesCurrent)
        {
            return;
        }

        if (Startup.TryEnable(out var enableError))
        {
            Log.Write($"开机启动已按当前路径更新：{Startup.Command}");
        }
        else
        {
            Log.Write($"开机启动登记更新失败：{enableError}");
        }
    }

    /// <summary>
    /// 拼出当前进程的开机启动登记器。
    /// </summary>
    /// <remarks>
    /// 正常双击的是 apphost（<c>BetterMicrosoftPinyin.exe</c>）；用 <c>dotnet BetterPinyin.App.dll</c>
    /// 调试时 <see cref="Environment.ProcessPath"/> 会变成 <c>dotnet.exe</c>，
    /// 直接拿它登记会变成「开机启动一个空的 dotnet」，所以优先换回同目录的 apphost。
    /// </remarks>
    private static StartupRegistration CreateStartupRegistration()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path) ||
            string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var appHost = Path.Combine(AppContext.BaseDirectory, "BetterMicrosoftPinyin.exe");
            if (File.Exists(appHost))
            {
                path = appHost;
            }
            else if (Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
            {
                // 没有 apphost 时退回 dotnet + 入口 dll，保证开发环境下也能验证。
                var dotnet = string.IsNullOrEmpty(path)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dotnet.exe")
                    : path;
                return new StartupRegistration(dotnet, $"\"{entry}\" --autostart");
            }
        }

        // 走到这里仍为空只可能是宿主异常；给一个同目录下的默认名，至少不会让属性初始化抛异常。
        path ??= Path.Combine(AppContext.BaseDirectory, "BetterMicrosoftPinyin.exe");
        return new StartupRegistration(path, "--autostart");
    }

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
            // 同时补一条简拼记录，否则用户还得打全拼才出得来。
            var batch = PhraseVariants.Expand(
                new MspyPhrase(pinyin, position, text),
                Settings.PhraseWriteInitials);

            var ok = Phrases.Merge(batch, overwriteOthers: false, out var conflicts);
            if (!ok)
            {
                Log.Write($"写入自定义短语失败：{text}（{pinyin}）— {Phrases.LastError}");
                return false;
            }

            foreach (var conflict in conflicts)
            {
                Log.Write($"自定义短语已有同拼音条目，跳过：{conflict.Pinyin} → {text}");
            }

            // 简拼那条冲突不影响全拼已经写进去这个事实。
            return !conflicts.Any(c => string.Equals(c.Pinyin, pinyin, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 给已有的短语补写简拼记录（老数据、或刚打开这个开关时用）。
    /// </summary>
    /// <returns>新增的条数。</returns>
    public static int BackfillInitialPhrases()
    {
        if (!Settings.PhraseWriteInitials)
        {
            return 0;
        }

        lock (PhraseGate)
        {
            var phrases = Phrases.Read(out var error);
            if (error is not null)
            {
                Log.Write($"补写简拼失败：{error}");
                return 0;
            }

            var existing = phrases
                .Select(p => (p.Text, p.Pinyin))
                .ToHashSet();

            var additions = new List<MspyPhrase>();
            foreach (var phrase in phrases)
            {
                var initials = PhraseVariants.InitialsOf(phrase.Pinyin);
                if (initials is null || existing.Contains((phrase.Text, initials)))
                {
                    continue;
                }

                additions.Add(phrase with { Pinyin = initials });
                existing.Add((phrase.Text, initials));
            }

            if (additions.Count == 0)
            {
                return 0;
            }

            if (!Phrases.Merge(additions, overwriteOthers: false, out var conflicts))
            {
                Log.Write($"补写简拼失败：{Phrases.LastError}");
                return 0;
            }

            var written = additions.Count - conflicts.Count;
            Log.Write($"已为 {written} 条短语补写简拼记录");
            return written;
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

        // 同步进去的还是全拼，补上简拼才能按首字母打出来。
        if (written > 0)
        {
            BackfillInitialPhrases();
        }

        return written;
    }

    /// <summary>取出某个词写入时应该用的位置值。</summary>
    private static int ResolvePosition(UserWord word)
    {
        var global = Math.Clamp(Settings.WordbookSyncPosition, 1, 9);
        return word.UdpPosition is >= 1 and <= 9 ? word.UdpPosition : global;
    }

    /// <summary>从自学习词库里删掉若干词的结果。</summary>
    /// <param name="Removed">实际删掉的条数。</param>
    /// <param name="Reloaded">输入法是否已重新加载词库。</param>
    /// <param name="ReloadMessage">重新加载的说明或失败原因。</param>
    /// <param name="Error">写入失败的原因；成功为 null。</param>
    public sealed record LearnedRemovalResult(int Removed, bool Reloaded, string ReloadMessage, string? Error);

    /// <summary>
    /// 从自学习词库里删除若干词，并让输入法立刻重新加载。
    /// </summary>
    /// <param name="texts">要删除的词面。</param>
    /// <param name="window">
    /// 借用的输入法上下文窗口：必须是设置窗口自己的句柄，而且它得处在前台。
    /// 输入法只在键盘布局切走再切回时才重读词库，见 <c>ImeReloader</c>。
    /// </param>
    /// <returns>删除结果。</returns>
    /// <remarks>
    /// 输入法随时可能用内存里的副本覆盖文件，所以「写文件」和「让它重新加载」必须成对做；
    /// 万一刚写完就被覆盖，这里会自动再试一次。
    /// </remarks>
    public static LearnedRemovalResult RemoveLearnedWords(IReadOnlyCollection<string> texts, IntPtr window)
    {
        var removed = 0;
        var reloaded = false;
        var reloadMessage = string.Empty;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!Learning.TryRemove(texts, out removed, out var error))
            {
                return new LearnedRemovalResult(0, false, string.Empty, error);
            }

            if (removed == 0)
            {
                break; // 这些词已经不在文件里了
            }

            reloaded = ImeReloader.TryReload(window, out reloadMessage);
            Thread.Sleep(220);

            if (!Learning.TryRead(out var words, out _))
            {
                break;
            }

            if (!words.Any(w => texts.Contains(w.Text)))
            {
                break; // 删除已经落到文件里
            }
        }

        if (removed > 0)
        {
            Log.Write($"自学习词库删除 {removed} 条，重新加载：{(reloaded ? "成功" : reloadMessage)}");
        }

        return new LearnedRemovalResult(removed, reloaded, reloadMessage, null);
    }

    /// <summary>从自学习词库备份恢复的结果。</summary>
    /// <param name="Restored">恢复后的词条数。</param>
    /// <param name="Reloaded">输入法是否已重新加载词库。</param>
    /// <param name="ReloadMessage">重新加载的说明或失败原因。</param>
    /// <param name="Error">写入失败的原因；成功为 null。</param>
    public sealed record LearnedRestoreResult(int Restored, bool Reloaded, string ReloadMessage, string? Error);

    /// <summary>
    /// 把自学习词库恢复到某个备份，并让输入法立刻重新加载。
    /// </summary>
    /// <param name="backupPath">备份文件路径。</param>
    /// <param name="window">借用的输入法上下文窗口，要求在前台（备份窗口自己）。</param>
    /// <returns>恢复结果。</returns>
    /// <remarks>
    /// 和删除一样，写文件与让输入法重读必须成对做；恢复前 <see cref="MspyLearningStore.TryRestore"/>
    /// 会自动把当前状态也备份一份，所以恢复本身可撤。
    /// </remarks>
    public static LearnedRestoreResult RestoreLearnedBackup(string backupPath, IntPtr window)
    {
        if (!MspyLearningStore.TryReadFile(backupPath, out var expected, out var readError))
        {
            return new LearnedRestoreResult(0, false, string.Empty, readError);
        }

        var restored = 0;
        var reloaded = false;
        var reloadMessage = string.Empty;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!Learning.TryRestore(backupPath, out restored, out var error))
            {
                return new LearnedRestoreResult(0, false, string.Empty, error);
            }

            reloaded = ImeReloader.TryReload(window, out reloadMessage);
            Thread.Sleep(220);

            if (!Learning.TryRead(out var current, out _) ||
                MspyLearningStore.Diff(expected, current).IsEmpty)
            {
                break; // 文件内容已经与备份一致
            }
        }

        Log.Write($"自学习词库从备份恢复 {restored} 条，重新加载：{(reloaded ? "成功" : reloadMessage)}");
        return new LearnedRestoreResult(restored, reloaded, reloadMessage, null);
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

using BetterPinyin.Contracts;
using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Ranking;
using BetterPinyin.Core.Settings;
using BetterPinyin.Core.Wordbook;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Input;
using BetterPinyin.Interop.Uia;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>浮窗要展示的内容。</summary>
internal sealed class CloudSuggestionsShownEventArgs : EventArgs
{
    /// <summary>按「严格 → 扩展」排列的渲染行。</summary>
    public required IReadOnlyList<SlotDescriptor> Slots { get; init; }

    /// <summary>候选窗快照，用于定位。</summary>
    public required CandidateWindowSnapshot Anchor { get; init; }

    /// <summary>是否来自英文模式（英文模式的浮窗文案不同）。</summary>
    public bool EnglishMode { get; init; }
}

/// <summary>云候选真的上屏之后的事件参数。</summary>
internal sealed class CloudSuggestionAcceptedEventArgs : EventArgs
{
    /// <summary>上屏的文本。</summary>
    public required string Text { get; init; }

    /// <summary>本次实际请求的拼音串（简拼已展开）；缺失时等于原始输入。</summary>
    public required string Reading { get; init; }

    /// <summary>用户原始输入串。</summary>
    public required string Query { get; init; }
}

/// <summary>
/// 云联想主控。
/// </summary>
/// <remarks>
/// <para>
/// 数据流：键盘钩子 → 重建拼音串 → 防抖 → 读候选窗判断输入状态 → 查云端 → 清洗 →
/// 按读音分区（严格 / 扩展）→ 分配采纳键 → 浮窗 → 按键上屏。
/// </para>
/// <para>
/// 钩子回调运行在输入线程上，里面只做位运算与数组查找：不读 UIA、不发网络、不加锁。
/// 需要跨线程共享的状态全部用 <see cref="Volatile"/> 读写；引用赋值本身是原子的。
/// </para>
/// </remarks>
internal sealed class CloudAssistController : IDisposable
{
    private static readonly string[] OwnProcessNames = { "BetterMicrosoftPinyin" };

    /// <summary>设置环境变量 <c>BETTERPINYIN_TRACE_KEYS=1</c> 时把每个按键写进日志。</summary>
    /// <remarks>
    /// 用来排查「钩子装了但收不到键」这类问题：从日志能直接看出有没有回调、
    /// 是不是被当成自身注入、前台窗口是谁。默认关闭，零开销。
    /// </remarks>
    private static readonly bool s_traceKeys =
        Environment.GetEnvironmentVariable("BETTERPINYIN_TRACE_KEYS") == "1";

    private readonly AppSettings _settings;
    private readonly BingSuggestionClient _client;
    private readonly KeyboardHub _hub;
    private readonly CompositionTracker _tracker;

    /// <summary>本次按键序列里已被吞掉的键，抬起时也要吞掉，避免目标窗口收到孤立 keyup。</summary>
    private readonly HashSet<int> _swallowUntilKeyUp = new();

    private PinyinMatcher _matcher;
    private CancellationTokenSource? _debounceSource;

    private ActivationMap _map = ActivationMap.Empty;
    private SuggestionSet? _current;
    private IntPtr _candidateHandle;
    private IntPtr _currentForeground;
    private AnchorPoint _currentCaret;
    private string _currentQuery = string.Empty;
    private long _currentVersion;
    private InputMode _currentMode;
    private int _acceptInProgress;
    private long _acceptGeneration;
    private bool _disposed;

    /// <summary>创建控制器。</summary>
    /// <param name="settings">设置。</param>
    /// <param name="hub">键盘中枢：钩子与组字状态机由它持有，本类只是订阅者。</param>
    public CloudAssistController(AppSettings settings, KeyboardHub hub)
    {
        _settings = settings;
        _hub = hub;
        _tracker = hub.Tracker;
        _client = new BingSuggestionClient(
            Http.Client,
            settings.Market,
            timeoutMilliseconds: settings.RequestTimeoutMs);
        _matcher = AppServices.CreateMatcher();
    }

    /// <summary>有新的候选可展示。</summary>
    public event EventHandler<CloudSuggestionsShownEventArgs>? SuggestionsReady;

    /// <summary>候选应当隐藏。</summary>
    public event EventHandler? SuggestionsCleared;

    /// <summary>诊断信息。</summary>
    public event EventHandler<string>? Diagnostics;

    /// <summary>
    /// 云候选已经真的上屏。
    /// </summary>
    /// <remarks>
    /// 与「记入词库」分开：词库可以关，统计需要的是「这个词确实进了正文」这个事实。
    /// 因此本事件由 <see cref="CommitSucceeded"/> 的调用方触发，不受词库开关影响。
    /// </remarks>
    public event EventHandler<CloudSuggestionAcceptedEventArgs>? SuggestionAccepted;

    /// <summary>钩子是否已安装。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>当前展示的分区结果。</summary>
    public SuggestionSet? Current => _current;

    /// <summary>最近一次输入串，供诊断展示。</summary>
    public string LastQuery { get; private set; } = string.Empty;

    /// <summary>启动：申请钩子、订阅状态机。</summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _tracker.CompositionChanged += OnCompositionChanged;
        _tracker.CompositionEnded += OnCompositionEnded;
        _hub.Filter += OnKeyEvent;

        if (!_hub.Acquire("cloud-assist", out var error))
        {
            _hub.Filter -= OnKeyEvent;
            _tracker.CompositionChanged -= OnCompositionChanged;
            _tracker.CompositionEnded -= OnCompositionEnded;
            Report($"键盘钩子安装失败：{error}");
            return;
        }

        IsRunning = true;
        Report("已启动，等待输入");
    }

    /// <summary>停止。</summary>
    public void Stop()
    {
        _hub.Filter -= OnKeyEvent;
        _tracker.CompositionChanged -= OnCompositionChanged;
        _tracker.CompositionEnded -= OnCompositionEnded;
        _hub.Release("cloud-assist");

        CancelPendingQuery();
        Clear();
        IsRunning = false;
    }

    /// <summary>设置变更后重新装配匹配器并清空当前结果。</summary>
    public void ApplySettings()
    {
        _matcher = AppServices.CreateMatcher();
        CancelPendingQuery();
        Clear();
    }

    /// <summary>
    /// 丢弃当前输入与浮窗状态。
    /// </summary>
    /// <remarks>
    /// 设置窗口获得或失去焦点时调用。键盘钩子是全局的：在设置页搜索框里打的字母
    /// 会照常进入组字缓冲，切走之后第一个按键就会带着这串残留去查询一次。
    /// 这里把缓冲、防抖任务与浮窗一起清掉，窗口边界就闭合了。
    /// </remarks>
    public void DismissInput()
    {
        CancelPendingQuery();
        _tracker.Reset();
        Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _debounceSource?.Dispose();
    }

    private void OnKeyEvent(object? sender, KeyboardHookEventArgs e)
    {
        if (s_traceKeys && e.IsKeyDown)
        {
            Report($"trace 键 vk=0x{e.VirtualKey:X2} own={e.IsOwnInjection} mod={e.Modifiers} " +
                   $"fg=0x{NativeMethods.GetForegroundWindow().ToInt64():X} name={WindowProcess.GetProcessName(NativeMethods.GetForegroundWindow())}");
        }

        // 本程序自己注入的按键（Esc、上屏文本、回填的数字）不参与任何判断。
        if (e.IsOwnInjection)
        {
            return;
        }

        if (!e.IsKeyDown)
        {
            if (_swallowUntilKeyUp.Remove(e.VirtualKey))
            {
                e.Handled = true;
            }

            return;
        }

        // 已经吞下、但还没抬起的键：连同自动重复一起继续吞，否则正文会冒出 666。
        if (_swallowUntilKeyUp.Contains(e.VirtualKey))
        {
            e.Handled = true;
            return;
        }

        // 提交窗口（Esc → 候选窗消失 → 上屏，约 100~150ms）内不接受新输入。
        // 采纳流程已移出钩子线程，钩子会立刻返回；此时用户敲的键会插到 Esc 与上屏之间，
        // 正文里就会混进半截字母。修饰键放行，避免 Shift/Ctrl 状态错乱。
        if (Volatile.Read(ref _acceptInProgress) != 0)
        {
            if (IsModifierKey(e.VirtualKey))
            {
                return;
            }

            _swallowUntilKeyUp.Add(e.VirtualKey);
            e.Handled = true;
            return;
        }

        var modifiers = e.Modifiers;
        var map = Volatile.Read(ref _map);

        if (modifiers == ModifierKeys.None && map.Count > 0 && map.TryGet(e.VirtualKey, out var target))
        {
            // 映射是在某个窗口里发布的。前台已经换了就说明用户切走了窗口，
            // 这时再吞键会把词打进别的窗口（典型场景：浮窗弹着，Alt+Tab 到设置页按采纳键）。
            if (NativeMethods.GetForegroundWindow() != _currentForeground)
            {
                DismissInput();
                return;
            }

            e.Handled = true;
            _swallowUntilKeyUp.Add(e.VirtualKey);

            var generation = Interlocked.Increment(ref _acceptGeneration);
            Volatile.Write(ref _map, ActivationMap.Empty);
            Volatile.Write(ref _acceptInProgress, 1);

            var query = _currentQuery;
            var version = _currentVersion;
            var handle = _candidateHandle;
            var foreground = _currentForeground;
            var caret = _currentCaret;
            var mode = _currentMode;

            // 先关浮窗再上屏；句柄等字段都在上面取过副本，不会因为 Clear 而丢失。
            // 注意不要在这里 Reset：Reset 会递增代次，上屏前的“组字是否变化”校验就永远失败。
            Clear();

            // 采纳流程里有同步的 UIA 读取（复核数字键、等候选窗消失），单次上限 400ms。
            // 留在钩子线程上会阻塞整条键盘输入链，所以整体挪到线程池。
            // 注意不能用 Task.Yield()：它会把续体投回 Dispatcher，而 Dispatcher 就是钩子线程。
            _ = Task.Run(async () =>
            {
                try
                {
                    if (mode == InputMode.English)
                    {
                        await CommitEnglishAsync(target, query, version, foreground, caret, generation)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await CommitAsync(target, query, version, handle, generation)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // 两条 Commit 自己都有 try/catch，这里兜住参数求值等同步异常，
                    // 否则 _acceptInProgress 会永远停在 1，之后所有按键都会被吞掉。
                    Report($"采纳流程异常：{ex.GetType().Name} {ex.Message}");
                    Volatile.Write(ref _acceptInProgress, 0);
                }
            });

            return;
        }

        // 组字状态机由 KeyboardHub 统一喂：它在本方法返回、且 Handled 仍为 false 时才喂。
        // 这里再喂一次会让同一个字母被追加两遍。
    }

    /// <summary>修饰键本身不参与「提交窗口内吞键」，否则 Shift/Ctrl 状态会错乱。</summary>
    private static bool IsModifierKey(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C;

    private void OnCompositionChanged(object? sender, CompositionChangedEventArgs e)
    {
        if (s_traceKeys)
        {
            Report($"trace 组字 \"{e.RawInput}\" v{e.Version}");
        }

        LastQuery = e.RawInput;

        // 输入变了，旧映射立刻作废：浮窗还留在屏幕上，但那一刻按采纳键不应再生效。
        Volatile.Write(ref _map, ActivationMap.Empty);

        if (!_settings.CloudAssistEnabled ||
            _settings.DoublePinyinMode ||
            (!_settings.StrictEnabled && !_settings.ExtendedEnabled))
        {
            Clear();
            return;
        }

        if (e.RawInput.Length < Math.Max(1, _settings.MinQueryLength))
        {
            Clear();
            return;
        }

        CancelPendingQuery();
        var source = new CancellationTokenSource();
        _debounceSource = source;
        _ = DebouncedQueryAsync(e.RawInput, e.Version, source.Token);
    }

    private void OnCompositionEnded(object? sender, CompositionEndedEventArgs e) => Clear();

    private async Task DebouncedQueryAsync(string raw, long version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Math.Clamp(_settings.DebounceMs, 40, 500), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await QueryAsync(raw, version, cancellationToken).ConfigureAwait(false);
    }

    private async Task QueryAsync(string raw, long version, CancellationToken cancellationToken)
    {
        if (IsOwnProcessForeground() || IsBlockedProcess())
        {
            // 在自己的窗口里打字不该触发联想；顺手把组字缓冲清掉，
            // 否则切走之后第一个按键会带着这串残留去查询。
            _tracker.Reset();
            Clear();
            return;
        }

        try
        {
            // 先读候选窗：它既决定浮窗锚点，也是「当前是否真在组字」的唯一可靠信号。
            // 英文模式下不发请求，省一次往返。
            var anchorPoint = CaretLocator.GetAnchor();
            var snapshot = AppServices.Reader.ReadBest(anchorPoint, 120);

            if (cancellationToken.IsCancellationRequested || version != _tracker.Version)
            {
                return;
            }

            var mode = InputModeResolver.Resolve(snapshot, raw, _settings);
            if (mode == InputMode.None)
            {
                Report($"{raw}：当前不是可联想的输入状态");
                Clear();
                return;
            }

            var requests = BuildQueries(raw);
            var local = snapshot.Items.Select(i => i.Text).ToList();
            var options = new SuggestionSelectionOptions(
                StrictEnabled: _settings.StrictEnabled,
                StrictDedupeLocal: _settings.StrictDedupeLocal,
                ExtendedEnabled: _settings.ExtendedEnabled,
                ExtendedMaxCount: Math.Clamp(_settings.ExtendedMaxCount, 1, 4));

            var wordbook = BuildWordbookCandidates(raw);

            // 本地优先：词库能整词命中就直接出浮窗，不再等网络。
            // 只认严格槽——否则一个前缀也会被当成「已经记住了」，把云端结果全挡掉。
            if (_settings.WordbookLocalFirst && wordbook.Count > 0)
            {
                var localSet = SuggestionSelector.Select(
                    raw,
                    Array.Empty<string>(),
                    local,
                    _matcher,
                    options,
                    snapshot.Items.Count,
                    snapshot.Quality == CandidateReadQuality.Exact,
                    SuggestionSelector.WordbookProviderId,
                    wordbook);

                if (localSet.Strict is not null)
                {
                    Publish(localSet, raw, version, mode, anchorPoint, snapshot, "词库");
                    return;
                }
            }

            SuggestionSet? lastSet = null;
            var rawCount = 0;

            foreach (var request in requests)
            {
                var items = await _client.GetSuggestionsAsync(request, cancellationToken).ConfigureAwait(false);
                rawCount += items.Count;

                if (cancellationToken.IsCancellationRequested || version != _tracker.Version)
                {
                    return;
                }

                // 回显比对要用本次实际请求的串：简拼展开后请求的是全拼（rineiwa），不是 raw（rnw）。
                // 被遗忘的词在这里就剔掉，不让它进入后续分区。
                var cleaned = ResultSanitizer.Sanitize(
                    items,
                    request,
                    SanitizeOptions.Extended,
                    AppServices.Wordbook.HiddenTexts);

                // 分区同样必须用实际请求串。拿 raw 校验时，rnw（3 个字母）永远匹配不上
                // 「日内瓦公约」（5 个音节），整个批次会被判成 None 丢掉。
                var attempt = SuggestionSelector.Select(
                    request,
                    cleaned,
                    local,
                    _matcher,
                    options,
                    snapshot.Items.Count,
                    snapshot.Quality == CandidateReadQuality.Exact,
                    _client.LastEndpointId ?? "bing",
                    wordbook);

                lastSet = attempt;

                // 这条展开串分不出槽位就继续试下一条，别让词库里的劣质展开挡住正确结果。
                if (attempt.IsEmpty)
                {
                    continue;
                }

                Publish(attempt, raw, version, mode, anchorPoint, snapshot, _client.LastEndpointId ?? "-");
                return;
            }

            Report($"{raw}：{lastSet?.Diagnostic ?? $"云端无可用候选（原始 {rawCount} 条均被过滤）"}");
            Clear();
        }
        catch (OperationCanceledException)
        {
            // 用户继续打字触发的取消，属正常路径。
        }
        catch (Exception ex)
        {
            Report($"查询异常：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 决定这次要查什么。
    /// </summary>
    /// <remarks>
    /// 全拼直接用原串；简拼（无法完整切分且词库能展开）先展开成全拼再查——
    /// 直接把 <c>ystl</c> 丢给 Bing 只会拿到拉丁垃圾。
    /// </remarks>
    private IReadOnlyList<string> BuildQueries(string raw)
    {
        if (!AppServices.Lexicon.IsLoaded)
        {
            return new[] { raw };
        }

        if (SyllableTable.Default.TrySegment(raw, _settings.FuzzyPinyin, out var syllables) && syllables >= 2)
        {
            return new[] { raw };
        }

        var expansions = AppServices.Lexicon.ExpandAbbreviation(raw, 2);
        return expansions.Count > 0 ? expansions : new[] { raw };
    }

    /// <summary>
    /// 把一次分区结果发布成浮窗与按键映射。
    /// </summary>
    /// <param name="set">分区结果。</param>
    /// <param name="raw">用户原始输入串。</param>
    /// <param name="version">组字版本。</param>
    /// <param name="mode">输入模式。</param>
    /// <param name="anchorPoint">锚点。</param>
    /// <param name="snapshot">候选窗快照。</param>
    /// <param name="source">来源标识，只用于诊断文案。</param>
    private void Publish(
        SuggestionSet set,
        string raw,
        long version,
        InputMode mode,
        AnchorPoint anchorPoint,
        CandidateWindowSnapshot snapshot,
        string source)
    {
        var extendedKeys = ResolveExtendedKeys(set, snapshot);
        var strictKey = KeyNames.Resolve(_settings.StrictAcceptKey);
        var (map, slots) = ActivationMap.Build(set, strictKey, extendedKeys);

        if (slots.Count == 0)
        {
            Report($"{raw}：{set.Diagnostic}");
            Clear();
            return;
        }

        _current = set;
        _currentQuery = raw;
        _currentVersion = version;
        _currentMode = mode;
        _currentForeground = NativeMethods.GetForegroundWindow();
        _currentCaret = anchorPoint;
        _candidateHandle = snapshot.IsVisible ? AppServices.Reader.LocateNear(anchorPoint) : IntPtr.Zero;

        Volatile.Write(ref _map, map);

        Report($"{raw} → {set.Diagnostic}（来源 {source}，" +
               $"锚点 {(snapshot.IsVisible ? "候选窗" : "插入符")}）");

        SuggestionsReady?.Invoke(
            this,
            new CloudSuggestionsShownEventArgs
            {
                Slots = slots,
                Anchor = snapshot,
                EnglishMode = mode == InputMode.English,
            });
    }

    /// <summary>
    /// 从用户词库里找与本次输入相关的词。
    /// </summary>
    /// <remarks>
    /// 先按完整拼音找，再按声母找。用户词库最多 5000 条，线性扫一遍是微秒级；
    /// 结果按使用次数排序，最多取 3 条，避免一次弹出七八行。
    /// </remarks>
    private IReadOnlyList<string> BuildWordbookCandidates(string raw)
    {
        if (!_settings.WordbookEnabled)
        {
            return Array.Empty<string>();
        }

        var book = AppServices.Wordbook;
        if (book.Count == 0)
        {
            return Array.Empty<string>();
        }

        var matched = new List<UserWord>(4);
        matched.AddRange(book.MatchPinyin(raw, 3));

        if (matched.Count < 3)
        {
            foreach (var word in book.MatchInitials(raw, 3))
            {
                if (!matched.Exists(w => string.Equals(w.Text, word.Text, StringComparison.Ordinal)))
                {
                    matched.Add(word);
                }
            }
        }

        if (matched.Count == 0)
        {
            return Array.Empty<string>();
        }

        matched.Sort((a, b) => b.Hits.CompareTo(a.Hits));
        return matched.Take(3).Select(w => w.Text).ToList();
    }

    /// <summary>
    /// 分配扩展槽的采纳键。
    /// </summary>
    /// <remarks>
    /// 自动模式接在本地候选之后：本地显示 5 个就用 6/7/8。读不到候选数时退回 6/7/8，
    /// 并在提交前复核——万一那个编号其实是本地候选，就把按键还回去。
    /// </remarks>
    private IReadOnlyList<int> ResolveExtendedKeys(in SuggestionSet set, in CandidateWindowSnapshot snapshot)
    {
        if (set.Extended.Count == 0)
        {
            return Array.Empty<int>();
        }

        var max = Math.Clamp(_settings.ExtendedMaxCount, 1, 4);
        var strictKey = KeyNames.Resolve(_settings.StrictAcceptKey);

        if (string.Equals(_settings.ExtendedKeyMode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            var manual = KeyNames.ParseExtendedKeys(_settings.ExtendedKeys);
            return manual.Where(key => key != strictKey).Take(max).ToArray();
        }

        var localCount = snapshot.Quality == CandidateReadQuality.Exact && snapshot.Items.Count > 0
            ? snapshot.Items.Count
            : 5;

        // 采纳键若被设成数字，扩展槽要跳过它，否则两个槽会抢同一个键。
        var keys = new List<int>(max);
        var digit = localCount;
        while (keys.Count < max && digit < 9)
        {
            digit++;
            var key = '0' + digit;
            if (key == strictKey)
            {
                continue;
            }

            keys.Add(key);
        }

        return keys;
    }

    /// <summary>
    /// 词真的上屏之后的收口动作：记入用户词库。
    /// </summary>
    /// <param name="target">被采纳的目标。</param>
    /// <param name="query">用户原始输入串。</param>
    /// <remarks>
    /// 只在这一个地方学，且只在 <c>SendUnicodeText</c> 返回 true 之后调用。
    /// 不要放进 <c>finally</c>——那里同时覆盖数字键回放、代次变化、候选窗未消失等失败分支，
    /// 会把没上屏的词也记进去。
    /// </remarks>
    /// <summary>通知订阅方「这条云候选真的上屏了」。</summary>
    /// <param name="target">采纳目标。</param>
    /// <param name="query">用户原始输入串。</param>
    private void ReportAccepted(in ActivationTarget target, string query)
    {
        var reading = target.Suggestion.Reading;
        if (string.IsNullOrEmpty(reading))
        {
            reading = query;
        }

        SuggestionAccepted?.Invoke(
            this,
            new CloudSuggestionAcceptedEventArgs
            {
                Text = target.Suggestion.Text,
                Reading = reading,
                Query = query,
            });
    }

    private void CommitSucceeded(in ActivationTarget target, string query)
    {
        if (!_settings.WordbookLearnOnAccept)
        {
            return;
        }

        var text = target.Suggestion.Text;
        if (!ResultSanitizer.IsAllChinese(text))
        {
            return;
        }

        // Reading 是本次实际请求的拼音串（简拼已被展开成全拼）；缺失时退回原始输入。
        var pinyin = target.Suggestion.Reading;
        if (string.IsNullOrEmpty(pinyin))
        {
            pinyin = query;
        }

        if (AppServices.Wordbook.Learn(text, pinyin, query, out var word))
        {
            Report($"已记入词库：{word.Text}（{word.Pinyin}，第 {word.Hits} 次）");

            // 立即写进微软拼音的自定义短语：这是用户唯一能「看见」效果的地方
            // （几秒后原生候选里就有这个词）。写失败只记日志，不影响已上屏的内容。
            if (_settings.PhraseAutoWrite)
            {
                var position = Math.Clamp(_settings.WordbookSyncPosition, 1, 9);
                if (AppServices.TryWritePhrase(word.Pinyin, word.Text, position))
                {
                    AppServices.Wordbook.SetUdpState(word.Text, UdpSyncState.Synced);
                    Report($"已写入微软拼音自定义短语：{word.Text}");
                }
            }
        }
    }

    private async Task CommitAsync(
        ActivationTarget target,
        string query,
        long version,
        IntPtr handle,
        long generation)
    {
        try
        {
            // 数字键复核：吞键到提交之间可能有几百毫秒，期间输入法可能补上了新候选。
            if (target.DigitThreshold > 0)
            {
                var fresh = AppServices.Reader.ReadBest(CaretLocator.GetAnchor(), 80);
                if (fresh.Quality != CandidateReadQuality.Exact ||
                    fresh.Items.Count >= target.DigitThreshold)
                {
                    ReplayKey(target.VirtualKey);
                    Report($"回填：本地候选数变为 {fresh.Items.Count}，" +
                           $"{KeyNames.Describe(target.VirtualKey)} 已还给输入法");
                    return;
                }
            }

            if (generation != Volatile.Read(ref _acceptGeneration) || _tracker.Version != version)
            {
                Report("放弃：组字已变化");
                return;
            }

            // 先取消组字，否则拼音会和中文一起上屏。
            KeySender.SendVirtualKey(0x1B);

            if (!await WaitCandidateGoneAsync(handle, 140).ConfigureAwait(false))
            {
                KeySender.SendVirtualKey(0x1B);
                if (!await WaitCandidateGoneAsync(handle, 100).ConfigureAwait(false))
                {
                    Report($"上屏中止：组字未被取消（{target.Suggestion.Text}）");
                    return;
                }
            }

            await Task.Delay(20).ConfigureAwait(false);

            if (KeySender.SendUnicodeText(target.Suggestion.Text))
            {
                Report($"已上屏：{target.Suggestion.Text}");
                CommitSucceeded(target, query);
                ReportAccepted(target, query);
            }
            else
            {
                Report($"上屏失败：{target.Suggestion.Text}");
            }
        }
        catch (Exception ex)
        {
            Report($"上屏异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _tracker.Reset();
            Volatile.Write(ref _acceptInProgress, 0);
        }
    }

    /// <summary>
    /// 英文模式上屏：字母已经进了文档，必须先删再写。
    /// </summary>
    /// <remarks>
    /// 这是全项目最危险的一段代码，所有护栏缺一不可：组字代次未变、前台窗口未变、
    /// 插入符没移动、并且能用 UIA 读到「最后 N 个字符就是刚才输入的串」。
    /// 任何一项不满足都放弃，宁可不上屏也不删正文。
    /// </remarks>
    private async Task CommitEnglishAsync(
        ActivationTarget target,
        string query,
        long version,
        IntPtr foreground,
        AnchorPoint caret,
        long generation)
    {
        try
        {
            if (!string.Equals(_settings.EnglishModeCommit, "Replace", StringComparison.OrdinalIgnoreCase))
            {
                Report($"英文模式仅提示，未上屏：{target.Suggestion.Text}");
                return;
            }

            if (generation != Volatile.Read(ref _acceptGeneration) || _tracker.Version != version)
            {
                Report("放弃：输入已变化");
                return;
            }

            if (NativeMethods.GetForegroundWindow() != foreground)
            {
                Report("放弃：前台窗口已切换");
                return;
            }

            var current = CaretLocator.GetAnchor();
            if (Math.Abs(current.X - caret.X) > 2 || Math.Abs(current.Y - caret.Y) > 2)
            {
                Report("放弃：插入符位置已变化");
                return;
            }

            var tail = AppServices.Reader.ReadFocusedValueTail(query.Length);
            if (tail is null || !tail.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                Report($"放弃：无法确认已输入内容（读到 {tail ?? "空"}）");
                return;
            }

            for (var i = 0; i < query.Length; i++)
            {
                KeySender.SendVirtualKey(0x08); // Backspace
            }

            await Task.Delay(20).ConfigureAwait(false);

            if (KeySender.SendUnicodeText(target.Suggestion.Text))
            {
                Report($"已替换上屏：{target.Suggestion.Text}");
                CommitSucceeded(target, query);
                ReportAccepted(target, query);
            }
            else
            {
                Report($"上屏失败：{target.Suggestion.Text}");
            }
        }
        catch (Exception ex)
        {
            Report($"英文模式上屏异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _tracker.Reset();
            Volatile.Write(ref _acceptInProgress, 0);
        }
    }

    private void ReplayKey(int virtualKey)
    {
        KeySender.SendVirtualKey((ushort)virtualKey);
        _tracker.Reset();
        Volatile.Write(ref _acceptInProgress, 0);
    }

    private async Task<bool> WaitCandidateGoneAsync(IntPtr handle, int timeoutMilliseconds)
    {
        if (handle == IntPtr.Zero)
        {
            // 没有候选窗可观察，给输入法一点时间处理 Esc 即可。
            await Task.Delay(60).ConfigureAwait(false);
            return true;
        }

        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!AppServices.Reader.Read(handle).IsVisible)
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return false;
    }

    private static bool IsOwnProcessForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var processName = WindowProcess.GetProcessName(foreground);
        foreach (var own in OwnProcessNames)
        {
            if (string.Equals(processName, own, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsBlockedProcess()
    {
        if (_settings.BlockedProcesses.Count == 0)
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var processName = WindowProcess.GetProcessName(foreground);
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (var blocked in _settings.BlockedProcesses)
        {
            if (string.Equals(processName, blocked, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void CancelPendingQuery()
    {
        var source = _debounceSource;
        _debounceSource = null;
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
            source.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略。
        }
    }

    private void Clear()
    {
        Volatile.Write(ref _map, ActivationMap.Empty);
        _candidateHandle = IntPtr.Zero;

        if (_current is null)
        {
            return;
        }

        _current = null;
        SuggestionsCleared?.Invoke(this, EventArgs.Empty);
    }

    private void Report(string message)
    {
        Log.Write("[云联想] " + message);
        Diagnostics?.Invoke(this, message);
    }
}

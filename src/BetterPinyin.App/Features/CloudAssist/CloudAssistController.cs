using BetterPinyin.Contracts;
using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Cloud.Sogou;
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
    private readonly BingSuggestionClient _bing;
    private readonly SogouSuggestionClient _sogou;
    private readonly KeyboardHub _hub;
    private readonly CompositionTracker _tracker;

    /// <summary>本次按键序列里已被吞掉的键，抬起时也要吞掉，避免目标窗口收到孤立 keyup。</summary>
    private readonly HashSet<int> _swallowUntilKeyUp = new();

    private PinyinMatcher _matcher;
    private CancellationTokenSource? _debounceSource;

    /// <summary>上一次按键时的前台窗口，用来发现「用户换了个窗口」。</summary>
    private IntPtr _lastForeground;

    /// <summary>最近一次按键的时间戳（<see cref="Environment.TickCount64"/>）。</summary>
    private long _lastKeystrokeTick;

    /// <summary>最近一次看到候选窗时的窗口句柄，用来判断组字是不是已经结束。</summary>
    private IntPtr _lastCandidateHandle;

    /// <summary>组字看门狗：按键收不到结束通知时，靠它发现组字已经结束了。</summary>
    private Timer? _compositionWatch;

    /// <summary>看门狗重入保护：上一个 tick 还没跑完就不再进来。</summary>
    private int _watchdogBusy;

    /// <summary>候选窗已消失时，用户停手多久就认定这次组字结束。</summary>
    /// <remarks>留出一点余量：连续打字时候选窗会有正常的重绘间隙，不能一看到不可见就收摊。</remarks>
    private const int CandidateGoneIdleMs = 700;

    /// <summary>下一个按键进来时，停手多久才值得验一次「上一段组字是不是已经没了」。</summary>
    /// <remarks>
    /// 鼠标点候选通常发生在停手之后，所以这个值要明显小于人类的选词耗时；
    /// 又要大于连续打字的键间隔，否则每打一个字都会白验一次。
    /// </remarks>
    private const int StaleCheckIdleMs = 300;

    /// <summary>完全看不到候选窗时的兜底时限。</summary>
    /// <remarks>
    /// 有些界面（任务栏搜索框之类的自绘控件）我们读不到候选窗，句柄一直是 0，
    /// 那就没有任何判据能证明组字还活着。此时只靠时间兜底：宁可丢掉一段「用户其实
    /// 还在打、只是停下来想了很久」的组字，也不能让残留拼音一直拼到后面的输入上。
    /// </remarks>
    private const int UnobservableIdleMs = 5000;

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
        _bing = new BingSuggestionClient(
            Http.Client,
            settings.Market,
            timeoutMilliseconds: settings.RequestTimeoutMs);
        _sogou = new SogouSuggestionClient(
            Http.Client,
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
        _compositionWatch = new Timer(OnCompositionWatch, null, 250, 250);
        Report("已启动，等待输入");
    }

    /// <summary>停止。</summary>
    public void Stop()
    {
        _hub.Filter -= OnKeyEvent;
        _tracker.CompositionChanged -= OnCompositionChanged;
        _tracker.CompositionEnded -= OnCompositionEnded;
        _hub.Release("cloud-assist");

        _compositionWatch?.Dispose();
        _compositionWatch = null;

        CancelPendingQuery();
        Clear();
        ForgetCandidateWindow();
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
        ForgetCandidateWindow();
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

        // 先验一眼「上一段组字是不是已经结束了」。键盘钩子看不到鼠标选词、输入法自动上屏、
        // 点别的控件这些结束方式，残留的拼音就会被拼到这一次输入上，查出「上一次 + 这一次」。
        // 两种判据：换了前台窗口（输入法一定在焦点变化时结束组字）；或者停手一小会儿之后
        // 候选窗已经不可见。放在最前面：被吞掉的采纳键、提交窗口内的键也要先过这一关。
        var activeWindow = NativeMethods.GetForegroundWindow();

        if (activeWindow != _lastForeground)
        {
            _lastForeground = activeWindow;
            DropStaleComposition("换了前台窗口", CompositionEndReason.FocusLost);
        }
        else if (Environment.TickCount64 - Volatile.Read(ref _lastKeystrokeTick) >= StaleCheckIdleMs &&
                 IsCompositionGone())
        {
            // 停手超过 StaleCheckIdleMs 才验：连续打字时候选窗会有正常的重绘间隙，
            // 每按一个键都去验一次既不必要，也可能把正在进行的组字切断。
            DropStaleComposition("候选窗已消失", CompositionEndReason.Unknown);
        }

        Volatile.Write(ref _lastKeystrokeTick, Environment.TickCount64);

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

    private void OnCompositionEnded(object? sender, CompositionEndedEventArgs e)
    {
        Clear();
        ForgetCandidateWindow();
    }

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

            // 记下这次看到的候选窗：看门狗要靠它判断组字有没有结束。
            var previousHandle = Volatile.Read(ref _lastCandidateHandle);
            Volatile.Write(
                ref _lastCandidateHandle,
                snapshot.IsVisible ? AppServices.Reader.LastHandle : IntPtr.Zero);

            var mode = InputModeResolver.Resolve(snapshot, raw, _settings);
            if (mode == InputMode.None)
            {
                Report(
                    $"{raw}：当前不是可联想的输入状态" +
                    $"（锚点={anchorPoint.Source}，候选窗={(snapshot.IsVisible ? "可见" : "未找到")}，" +
                    $"{UiaCandidateWindowReader.LastDiagnostic}）");

                // 上一次查询还看得见候选窗、这次看不见了：那一段组字已经结束，
                // 只是没有任何按键通知我们。留着缓冲只会把拼音拼到下一次输入上。
                if (previousHandle != IntPtr.Zero)
                {
                    _tracker.NotifyCompositionLost(CompositionEndReason.Unknown);
                }

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

            var sources = CloudSourceCatalog.ResolveEnabled(_settings.CloudSources);
            if (sources.Count == 0)
            {
                // 用户把所有来源都关了：不请求任何接口，也不留上一轮的浮窗。
                Report($"{raw}：没有启用的云联想来源");
                Clear();
                return;
            }

            SuggestionSet? lastSet = null;
            var rawCount = 0;

            foreach (var request in requests)
            {
                var answer = await QuerySourcesAsync(
                        request, local, options, snapshot, wordbook, sources, cancellationToken)
                    .ConfigureAwait(false);
                rawCount += answer.RawCount;

                if (cancellationToken.IsCancellationRequested || version != _tracker.Version)
                {
                    return;
                }

                lastSet = answer.Set;

                // 这条展开串分不出槽位就继续试下一条，别让词库里的劣质展开挡住正确结果。
                if (answer.Set.IsEmpty)
                {
                    continue;
                }

                Publish(answer.Set, raw, version, mode, anchorPoint, snapshot, DescribeSources(answer.Set));
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

    /// <summary>一个来源本次查询的结果。</summary>
    /// <param name="SupportsExtended">这个来源是否参与扩展槽。</param>
    /// <param name="Set">按读音分区后的候选。</param>
    /// <param name="RawCount">云端返回的原始条数，仅用于诊断。</param>
    private readonly record struct SourceAnswer(
        bool SupportsExtended,
        SuggestionSet Set,
        int RawCount);

    /// <summary>
    /// 按用户设定的顺序，从启用的来源里取词。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 严格槽取「第一个给出整词」的来源，扩展槽取「第一个支持扩展且给出更长词」的来源；
    /// 两者可以是不同来源，最后合并。顺序就是设置页里列表的顺序，拖动即可调整。
    /// </para>
    /// <para>
    /// 并行模式同时请求所有启用来源，延迟取较慢的那条；串行模式逐个请求，
    /// 两个槽都有着落就不再问后面的来源。禁用来源在任何模式下都不会收到请求。
    /// </para>
    /// </remarks>
    private async Task<(SuggestionSet Set, int RawCount)> QuerySourcesAsync(
        string request,
        IReadOnlyList<string> local,
        SuggestionSelectionOptions options,
        CandidateWindowSnapshot snapshot,
        IReadOnlyList<string> wordbook,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken)
    {
        var answers = new List<SourceAnswer>(sources.Count);

        if (_settings.ParallelCloudQuery)
        {
            var tasks = new Task<SourceAnswer>[sources.Count];
            for (var i = 0; i < sources.Count; i++)
            {
                tasks[i] = QueryOneAsync(
                    sources[i], request, local, options, snapshot, wordbook, cancellationToken);
            }

            answers.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        }
        else
        {
            foreach (var source in sources)
            {
                var needStrict = options.StrictEnabled && !answers.Exists(a => a.Set.Strict is not null);
                var needExtended = options.ExtendedEnabled &&
                                   !answers.Exists(a => a.SupportsExtended && a.Set.Extended.Count > 0);

                // 两个槽都有结果了，后面的来源不必再问——这正是串行模式省流量的地方。
                if (!needStrict && !needExtended)
                {
                    break;
                }

                answers.Add(await QueryOneAsync(
                        source, request, local, options, snapshot, wordbook, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        SuggestionSet? strictSet = null;
        SuggestionSet? extendedSet = null;
        var rawCount = 0;

        // answers 与 sources 同序（WhenAll 保序，串行是追加），这里就是用户的优先级。
        foreach (var answer in answers)
        {
            rawCount += answer.RawCount;

            if (strictSet is null && answer.Set.Strict is not null)
            {
                strictSet = answer.Set;
            }

            if (extendedSet is null && answer.SupportsExtended && answer.Set.Extended.Count > 0)
            {
                extendedSet = answer.Set;
            }
        }

        if (!options.ExtendedEnabled || extendedSet is null)
        {
            return (strictSet ?? SuggestionSet.Empty, rawCount);
        }

        if (strictSet is null)
        {
            return (extendedSet, rawCount);
        }

        return (SuggestionSelector.Combine(strictSet, extendedSet), rawCount);
    }

    /// <summary>查一个来源并分区。</summary>
    /// <param name="sourceId">来源标识，调用方保证已归一化。</param>
    /// <remarks>
    /// 不支持扩展的来源强制关掉扩展分区：它返回的长词不是「以输入为前缀的联想」，
    /// 混进扩展槽会让用户看到来历不明的候选。
    /// </remarks>
    private async Task<SourceAnswer> QueryOneAsync(
        string sourceId,
        string request,
        IReadOnlyList<string> local,
        SuggestionSelectionOptions options,
        CandidateWindowSnapshot snapshot,
        IReadOnlyList<string> wordbook,
        CancellationToken cancellationToken)
    {
        ICloudSuggestionProvider provider =
            sourceId == CloudSourceCatalog.SogouId ? _sogou : _bing;
        var answer = await provider.QueryAsync(request, cancellationToken).ConfigureAwait(false);
        var supportsExtended = CloudSourceCatalog.SupportsExtended(sourceId);
        var selectionOptions = supportsExtended
            ? options
            : options with { ExtendedEnabled = false };

        var set = SuggestionSelector.Select(
            request,
            ResultSanitizer.Sanitize(
                answer.Items,
                request,
                SanitizeOptions.Extended,
                AppServices.Wordbook.HiddenTexts),
            local,
            _matcher,
            selectionOptions,
            snapshot.Items.Count,
            snapshot.Quality == CandidateReadQuality.Exact,
            answer.SourceId,
            wordbook);

        return new SourceAnswer(supportsExtended, set, answer.Items.Count);
    }

    /// <summary>把两个槽各自的来源拼成一行人话，用于诊断。</summary>
    /// <param name="set">分区结果。</param>
    /// <returns>形如 <c>严格=sogou，扩展=bing-qsonhs</c> 的说明。</returns>
    private static string DescribeSources(SuggestionSet set)
    {
        var strict = set.Strict?.ProviderId ?? "-";
        var extended = set.Extended.Count > 0 ? set.Extended[0].ProviderId : "-";
        return $"严格={strict}，扩展={extended}";
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
        _candidateHandle = snapshot.IsVisible ? AppServices.Reader.LastHandle : IntPtr.Zero;

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
        // 扩展候选默认只上屏、不记账：整句写进短语库会长期占住原生候选第一位。
        // 这里连词库一起跳过，否则词条会停在「待同步」，下次启动被补写，开关就失效了。
        if (!WordbookCommitPolicy.ShouldRecord(
                isExtended: target.Slot == SuggestionSlot.Extended,
                _settings))
        {
            return;
        }

        var text = target.Suggestion.Text;
        if (!ResultSanitizer.IsAllChinese(text))
        {
            return;
        }

        // 读音要从「候选词 + 用户真实输入」还原：Reading 是请求串，简拼取不到展开时
        // 它就是 chtqt 这种半截串，Learn 会在切分那一步拒绝，词静默地学不进去。
        if (!_matcher.TryResolveReading(query, text, out var pinyin))
        {
            // 还原不出来时退回请求串：词库未加载等降级场景下它仍是有效的全拼。
            pinyin = target.Suggestion.Reading ?? query;
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
        else
        {
            // 不记也要留一行：否则「已上屏」后面什么都没有，用户只能看到词没进库。
            Report($"未记入词库：{text}（读音「{pinyin}」切不出音节）");
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

    /// <summary>
    /// 忘掉上次读到的候选窗句柄。
    /// </summary>
    /// <remarks>
    /// 只能在确认组字已经结束时调用。浮窗消失（没有候选可展示）不代表组字结束，
    /// 那种情况下句柄要留着，否则鼠标选词那一下就没有判据了。
    /// </remarks>
    private void ForgetCandidateWindow() => Volatile.Write(ref _lastCandidateHandle, IntPtr.Zero);

    /// <summary>组字是否已经结束但没人告诉我们（候选窗看得见→现在看不见了）。</summary>
    /// <returns>已经结束返回 true。</returns>
    private bool IsCompositionGone()
    {
        if (!_tracker.IsComposing)
        {
            return false;
        }

        var handle = Volatile.Read(ref _lastCandidateHandle);

        // 从没读到过候选窗：没有判据，交给看门狗的时间兜底，不要在这里猜。
        return handle != IntPtr.Zero && !NativeMethods.IsWindowVisible(handle);
    }

    /// <summary>丢掉一段已经结束、但我们没收到结束通知的组字。</summary>
    /// <param name="reason">诊断用的原因。</param>
    /// <param name="endReason">记入统计的结束原因。</param>
    /// <remarks>日志里只写长度，不写拼音串——日志不记录用户输入内容。</remarks>
    private void DropStaleComposition(string reason, CompositionEndReason endReason)
    {
        if (_tracker.IsComposing)
        {
            Log.Write($"[云联想] 结束残留组字（{reason}，长度 {_tracker.PendingLength}）");
            _tracker.NotifyCompositionLost(endReason);
        }

        Clear();
    }

    /// <summary>
    /// 定时检查候选窗是否已经消失；消失了就结束这次组字。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 键盘钩子只能看到按键。用户用鼠标点候选、点别的窗口、或者输入法自己上屏时，
    /// 我们收不到任何通知，重建出来的拼音串就会一直挂着——下一次输入被拼成
    /// 「上一次 + 这一次」，查出来的词自然全是错的。
    /// </para>
    /// <para>
    /// 三个判据：组字窗口不再是前台（立刻收摊）；见过候选窗的看它还在不在
    /// （<see cref="CandidateGoneIdleMs"/>）；从没见过的（读不到候选窗的界面）只能等
    /// <see cref="UnobservableIdleMs"/> 兜底。后者保证「再读不到也最多残留 5 秒」，
    /// 不会一路合并下去。后两条都不在用户打字过程中触发：连续输入时不可能停手这么久。
    /// </para>
    /// </remarks>
    private void OnCompositionWatch(object? state)
    {
        if (Interlocked.Exchange(ref _watchdogBusy, 1) != 0)
        {
            return;
        }

        try
        {
            // 停用/释放之后定时器可能还有一次回调在跑：它不能再动事件订阅方。
            if (_disposed || !_tracker.IsComposing)
            {
                return;
            }

            var idle = Environment.TickCount64 - Volatile.Read(ref _lastKeystrokeTick);
            var handle = Volatile.Read(ref _lastCandidateHandle);

            // 组字所在窗口已经不是前台：输入法在焦点离开时就把组字结束了。
            // 这条不看停手时间——用户点了别的窗口，浮窗和拼音串都该立刻收摊。
            if (NativeMethods.GetForegroundWindow() != Volatile.Read(ref _lastForeground))
            {
                DropStaleComposition("组字窗口已不在前台", CompositionEndReason.FocusLost);
                return;
            }

            if (handle == IntPtr.Zero)
            {
                if (idle < UnobservableIdleMs)
                {
                    return;
                }
            }
            else
            {
                if (idle < CandidateGoneIdleMs)
                {
                    return;
                }

                // 候选窗还在就说明组字没结束（用户只是在思考），什么都不做。
                if (NativeMethods.IsWindowVisible(handle))
                {
                    return;
                }
            }

            Log.Write($"[云联想] 组字看门狗：停手 {idle}ms，" +
                      $"{(handle == IntPtr.Zero ? "始终没读到候选窗" : "候选窗已消失")}");
            DropStaleComposition(
                handle == IntPtr.Zero ? "组字长时间无法观察" : "候选窗已消失",
                CompositionEndReason.Unknown);
        }
        catch (Exception ex)
        {
            // 看门狗自己不能把程序带崩：它跑在线程池上，异常会直接结束进程。
            Log.Write($"[云联想] 组字看门狗异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _watchdogBusy, 0);
        }
    }

    private void Report(string message)
    {
        Log.Write("[云联想] " + message);
        Diagnostics?.Invoke(this, message);
    }
}

using BetterPinyin.Contracts;
using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Ranking;
using BetterPinyin.Core.Settings;
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

    private readonly AppSettings _settings;
    private readonly BingSuggestionClient _client;
    private readonly LowLevelKeyboardHook _hook = new();
    private readonly CompositionTracker _tracker = new();

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
    public CloudAssistController(AppSettings settings)
    {
        _settings = settings;
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

    /// <summary>钩子是否已安装。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>当前展示的分区结果。</summary>
    public SuggestionSet? Current => _current;

    /// <summary>最近一次输入串，供诊断展示。</summary>
    public string LastQuery { get; private set; } = string.Empty;

    /// <summary>启动：装钩子、订阅状态机。</summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _tracker.CompositionChanged += OnCompositionChanged;
        _tracker.CompositionEnded += OnCompositionEnded;
        _hook.KeyEvent += OnKeyEvent;

        try
        {
            _hook.Start();
            IsRunning = true;
            Report("已启动，等待输入");
        }
        catch (Exception ex)
        {
            _hook.KeyEvent -= OnKeyEvent;
            _tracker.CompositionChanged -= OnCompositionChanged;
            _tracker.CompositionEnded -= OnCompositionEnded;
            Report($"键盘钩子安装失败：{ex.Message}");
        }
    }

    /// <summary>停止。</summary>
    public void Stop()
    {
        _hook.Dispose();
        _hook.KeyEvent -= OnKeyEvent;
        _tracker.CompositionChanged -= OnCompositionChanged;
        _tracker.CompositionEnded -= OnCompositionEnded;

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

        var modifiers = ReadModifiers();
        var map = Volatile.Read(ref _map);

        if (modifiers == ModifierKeys.None && map.Count > 0 && map.TryGet(e.VirtualKey, out var target))
        {
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

        _tracker.Feed(e.VirtualKey, e.IsKeyDown, modifiers, e.IsOwnInjection);
    }

    /// <summary>修饰键本身不参与「提交窗口内吞键」，否则 Shift/Ctrl 状态会错乱。</summary>
    private static bool IsModifierKey(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C;

    private void OnCompositionChanged(object? sender, CompositionChangedEventArgs e)
    {
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
            var rawItems = new List<string>(16);

            foreach (var request in requests)
            {
                var items = await _client.GetSuggestionsAsync(request, cancellationToken).ConfigureAwait(false);
                rawItems.AddRange(items);

                if (cancellationToken.IsCancellationRequested || version != _tracker.Version)
                {
                    return;
                }

                // 简拼展开可能有多个全拼串，第一个有结果就不再往下查，控制延迟。
                if (items.Count > 0)
                {
                    break;
                }
            }

            var cleaned = ResultSanitizer.Sanitize(rawItems, raw, SanitizeOptions.Extended);
            if (cleaned.Count == 0)
            {
                Report($"{raw}：云端无可用候选（原始 {rawItems.Count} 条均被过滤）");
                Clear();
                return;
            }

            var local = snapshot.Items.Select(i => i.Text).ToList();
            var options = new SuggestionSelectionOptions(
                StrictEnabled: _settings.StrictEnabled,
                StrictDedupeLocal: _settings.StrictDedupeLocal,
                ExtendedEnabled: _settings.ExtendedEnabled,
                ExtendedMaxCount: Math.Clamp(_settings.ExtendedMaxCount, 1, 4));

            var set = SuggestionSelector.Select(
                raw,
                cleaned,
                local,
                _matcher,
                options,
                snapshot.Items.Count,
                snapshot.Quality == CandidateReadQuality.Exact,
                _client.LastEndpointId ?? "bing");

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

            Report($"{raw} → {set.Diagnostic}（来源 {_client.LastEndpointId ?? "-"}，" +
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

        if (string.Equals(_settings.ExtendedKeyMode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            var manual = KeyNames.ParseDigits(_settings.ExtendedKeys);
            return manual.Take(max).ToArray();
        }

        var localCount = snapshot.Quality == CandidateReadQuality.Exact && snapshot.Items.Count > 0
            ? snapshot.Items.Count
            : 5;

        var keys = new List<int>(max);
        for (var i = 0; i < max; i++)
        {
            var digit = localCount + 1 + i;
            if (digit > 9)
            {
                break;
            }

            keys.Add('0' + digit);
        }

        return keys;
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

    private static ModifierKeys ReadModifiers()
    {
        var modifiers = ModifierKeys.None;
        if ((NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0)
        {
            modifiers |= ModifierKeys.Shift;
        }

        if (NativeMethods.IsCtrlDown)
        {
            modifiers |= ModifierKeys.Control;
        }

        if (NativeMethods.IsAltDown)
        {
            modifiers |= ModifierKeys.Alt;
        }

        if (NativeMethods.IsWinDown)
        {
            modifiers |= ModifierKeys.Windows;
        }

        return modifiers;
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

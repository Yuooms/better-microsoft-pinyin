using BetterPinyin.Contracts;
using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Ranking;
using BetterPinyin.Core.Settings;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Input;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>云候选已就绪，需要浮窗展示。</summary>
internal sealed class CloudSuggestionShownEventArgs : EventArgs
{
    public CloudSuggestionShownEventArgs(
        CloudSuggestion suggestion,
        CandidateWindowSnapshot anchor,
        bool anchorFromCandidateWindow)
    {
        Suggestion = suggestion;
        Anchor = anchor;
        AnchorFromCandidateWindow = anchorFromCandidateWindow;
    }

    /// <summary>要展示的候选。</summary>
    public CloudSuggestion Suggestion { get; }

    /// <summary>候选窗快照（贴在它下方）。</summary>
    public CandidateWindowSnapshot Anchor { get; }

    /// <summary>锚点是否来自候选窗（否则需要退回插入符定位）。</summary>
    public bool AnchorFromCandidateWindow { get; }
}

/// <summary>
/// 云联想的编排器：把「按键 → 查云端 → 排序 → 展示 → 采纳上屏」串起来。
/// </summary>
/// <remarks>
/// 线程模型：
/// <list type="bullet">
/// <item>键盘钩子回调在安装了钩子的线程（UI 线程）上执行，只做过滤与转交，绝不做 I/O。</item>
/// <item>云端查询在后台线程，结果回投时需要校验组字代次，代次不符直接丢弃。</item>
/// <item>UIA 读取通过 <see cref="Interop.Uia.UiaWorker"/> 串行化到专用 STA 线程并带超时。</item>
/// </list>
/// </remarks>
internal sealed class CloudAssistController : IDisposable
{
    private static readonly string[] OwnProcessNames = { "BetterMicrosoftPinyin" };

    private readonly AppSettings _settings;
    private readonly BingSuggestionClient _client;
    private CandidateRanker _ranker;
    private readonly LowLevelKeyboardHook _hook = new();
    private readonly CompositionTracker _tracker = new();

    private CancellationTokenSource? _debounceSource;
    private IntPtr _candidateHandle;
    private bool _disposed;

    public CloudAssistController(AppSettings settings)
    {
        _settings = settings;
        _client = new BingSuggestionClient(
            Http.Client,
            settings.Market,
            timeoutMilliseconds: settings.RequestTimeoutMs);
        _ranker = new CandidateRanker(new PinyinMatcher(fuzzyEnabled: settings.FuzzyPinyin), settings.MaxSuggestions);
    }

    /// <summary>云候选就绪。</summary>
    public event EventHandler<CloudSuggestionShownEventArgs>? SuggestionReady;

    /// <summary>云候选应被清除。</summary>
    public event EventHandler? SuggestionCleared;

    /// <summary>诊断消息，供设置页实时展示。</summary>
    public event EventHandler<string>? Diagnostics;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>当前展示中的候选。</summary>
    public CloudSuggestion? Current { get; private set; }

    /// <summary>最近一次处理的输入串。</summary>
    public string LastQuery { get; private set; } = string.Empty;

    /// <summary>启动：安装键盘钩子并接管组字变化。</summary>
    /// <returns>成功返回 true。</returns>
    public bool Start()
    {
        if (IsRunning)
        {
            return true;
        }

        _tracker.CompositionChanged += OnCompositionChanged;
        _tracker.CompositionEnded += OnCompositionEnded;
        _hook.KeyEvent += OnKeyEvent;

        try
        {
            _hook.Start();
        }
        catch (Exception ex)
        {
            Report($"键盘钩子安装失败：{ex.Message}");
            _hook.KeyEvent -= OnKeyEvent;
            return false;
        }

        IsRunning = true;
        Report("云联想已启动");
        return true;
    }

    /// <summary>停止并卸载钩子。</summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _hook.Dispose();
        _hook.KeyEvent -= OnKeyEvent;
        _tracker.CompositionChanged -= OnCompositionChanged;
        _tracker.CompositionEnded -= OnCompositionEnded;
        CancelPendingQuery();
        Clear();
        IsRunning = false;
        Report("云联想已停止");
    }

    /// <summary>设置变更后调用，使排序参数与词源配置即时生效。</summary>
    public void ApplySettings()
    {
        _ranker = new CandidateRanker(
            new PinyinMatcher(fuzzyEnabled: _settings.FuzzyPinyin),
            _settings.MaxSuggestions);
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
        if (!e.IsKeyDown)
        {
            return;
        }

        // 采纳键只在我们确实有候选可采纳时才吞掉，其余情况一律放行。
        if (Current is not null && !e.IsInjected && IsAcceptKey(e.VirtualKey))
        {
            e.Handled = true;
            var suggestion = Current;
            Clear();
            _ = CommitAsync(suggestion);
            return;
        }

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

        _tracker.Feed(e.VirtualKey, e.IsKeyDown, modifiers, e.IsOwnInjection);
    }

    private void OnCompositionChanged(object? sender, CompositionChangedEventArgs e)
    {
        LastQuery = e.RawInput;

        if (!_settings.CloudAssistEnabled || _settings.DoublePinyinMode)
        {
            Clear();
            return;
        }

        if (e.RawInput.Length < Math.Max(1, _settings.MinQueryLength))
        {
            Clear();
            return;
        }

        // 输入还在继续，先把上一次未返回的查询取消掉，避免旧结果覆盖新输入。
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
            var rawItems = await _client.GetSuggestionsAsync(raw, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || version != _tracker.Version)
            {
                return;
            }

            var cleaned = ResultSanitizer.Sanitize(rawItems, raw);
            if (cleaned.Count == 0)
            {
                Report($"{raw}：云端无可用候选（原始 {rawItems.Count} 条均被过滤）");
                Clear();
                return;
            }

            // 读本地候选用于去重；同时拿到候选窗矩形作为浮窗锚点。
            var anchor = CaretLocator.GetAnchor();
            var snapshot = AppServices.Reader.ReadBest(anchor, 220);

            if (cancellationToken.IsCancellationRequested || version != _tracker.Version)
            {
                return;
            }

            var local = snapshot.Items.Select(i => i.Text).ToList();
            var ranked = _ranker.Rank(raw, cleaned, local, _client.LastEndpointId ?? "bing");

            if (ranked.Count == 0)
            {
                Report($"{raw}：{cleaned.Count} 条候选均已在本地区命中或读音不符");
                Clear();
                return;
            }

            Current = ranked[0];
            _candidateHandle = snapshot.IsVisible
                ? AppServices.Reader.LocateNear(anchor)
                : IntPtr.Zero;

            Report($"{raw} → {Current.Text}（来源 {_client.LastEndpointId ?? "-"}，" +
                   $"锚点 {(snapshot.IsVisible ? "候选窗" : "插入符")}）");

            SuggestionReady?.Invoke(
                this,
                new CloudSuggestionShownEventArgs(Current, snapshot, snapshot.IsVisible));
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

    private async Task CommitAsync(CloudSuggestion suggestion)
    {
        try
        {
            var handle = _candidateHandle;

            // 先取消组字，否则拼音会和中文一起上屏。
            KeySender.SendVirtualKey(0x1B);

            if (!await WaitCandidateGoneAsync(handle, 140).ConfigureAwait(false))
            {
                KeySender.SendVirtualKey(0x1B);
                if (!await WaitCandidateGoneAsync(handle, 100).ConfigureAwait(false))
                {
                    Report($"上屏中止：组字未被取消（{suggestion.Text}）");
                    return;
                }
            }

            await Task.Delay(20).ConfigureAwait(false);

            if (KeySender.SendUnicodeText(suggestion.Text))
            {
                Report($"已上屏：{suggestion.Text}");
            }
            else
            {
                Report($"上屏失败：{suggestion.Text}");
            }
        }
        catch (Exception ex)
        {
            Report($"上屏异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _tracker.Reset();
            _candidateHandle = IntPtr.Zero;
        }
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

    private bool IsAcceptKey(int virtualKey) => _settings.AcceptKey switch
    {
        "Semicolon" => virtualKey == 0xBA,
        "Backquote" => virtualKey == 0xC0,
        "Comma" => virtualKey == 0xBC,
        _ => virtualKey == 0x09,
    };

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
        if (Current is null)
        {
            return;
        }

        Current = null;
        _candidateHandle = IntPtr.Zero;
        SuggestionCleared?.Invoke(this, EventArgs.Empty);
    }

    private void Report(string message)
    {
        Log.Write("[云联想] " + message);
        Diagnostics?.Invoke(this, message);
    }
}

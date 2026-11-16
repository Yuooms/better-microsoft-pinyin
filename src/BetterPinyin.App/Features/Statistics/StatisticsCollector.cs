using BetterPinyin.App.Features.CloudAssist;
using BetterPinyin.Contracts;
using BetterPinyin.Core.Settings;
using BetterPinyin.Core.Statistics;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Input;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Features.Statistics;

/// <summary>
/// 把键盘与云联想事件翻译成统计累加。
/// </summary>
/// <remarks>
/// <para>
/// 这一层只做「翻译 + 过滤」，所有计数逻辑都在 <see cref="StatisticsAggregator"/> 里，
/// 因此跨天、归并这些容易算错的部分可以脱离钩子单独测试。
/// </para>
/// <para>
/// 两个过滤在组字开始时做一次，之后整段输入沿用同一个判断：
/// 前台是本程序自己的窗口 → 不记；前台不是中文键盘布局 → 不记（否则英文打字也会被当成拼音）。
/// 程序名同理，只在组字开始时取一次，不是每按一个键都去查进程。
/// </para>
/// </remarks>
internal sealed class StatisticsCollector : IDisposable
{
    /// <summary>在键盘中枢里的持有者标识。</summary>
    private const string HubOwner = "input-stats";

    private const string OwnProcessName = "BetterMicrosoftPinyin";

    /// <summary>UWP 应用的宿主进程名；这些窗口的真实应用无法从进程名还原。</summary>
    private const string UwpHostProcessName = "ApplicationFrameHost";

    private readonly StatisticsAggregator _aggregator;
    private readonly AppSettings _settings;
    private readonly KeyboardHub _hub;
    private readonly CloudAssistController? _controller;
    private readonly Action? _onChanged;

    private int _lastLength;
    private bool _started;

    /// <summary>最近一次启动失败的原因。</summary>
    public string? LastError { get; private set; }

    /// <summary>创建收集器。</summary>
    /// <param name="aggregator">累加器。</param>
    /// <param name="settings">设置，用来判断是否按程序统计。</param>
    /// <param name="hub">键盘中枢。</param>
    /// <param name="controller">云联想控制器，用于捕获采纳事件；可为空。</param>
    /// <param name="onChanged">一次输入告一段落后的回调，用来触发落盘。</param>
    public StatisticsCollector(
        StatisticsAggregator aggregator,
        AppSettings settings,
        KeyboardHub hub,
        CloudAssistController? controller,
        Action? onChanged = null)
    {
        _aggregator = aggregator;
        _settings = settings;
        _hub = hub;
        _controller = controller;
        _onChanged = onChanged;
    }

    /// <summary>开始收集。</summary>
    /// <returns>钩子可用返回 true；失败时不订阅任何事件。</returns>
    /// <remarks>
    /// 统计必须自己持有钩子，不能搭云联想的便车：用户完全可能关掉云联想、
    /// 只留统计，那时候没有人 <c>Acquire</c>，钩子不会装，数字会永远是 0。
    /// 中枢按引用计数，两个模块都在时也只会装一次。
    /// </remarks>
    public bool Start()
    {
        if (_started)
        {
            return true;
        }

        if (!_hub.Acquire(HubOwner, out var error))
        {
            LastError = error ?? "键盘钩子安装失败";
            return false;
        }

        _started = true;
        LastError = null;
        _hub.Observed += OnObserved;
        _hub.Tracker.CompositionChanged += OnCompositionChanged;
        _hub.Tracker.CompositionEnded += OnCompositionEnded;

        if (_controller is not null)
        {
            _controller.SuggestionAccepted += OnSuggestionAccepted;
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _hub.Observed -= OnObserved;
        _hub.Tracker.CompositionChanged -= OnCompositionChanged;
        _hub.Tracker.CompositionEnded -= OnCompositionEnded;

        if (_controller is not null)
        {
            _controller.SuggestionAccepted -= OnSuggestionAccepted;
        }

        _hub.Release(HubOwner);
    }

    private void OnObserved(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsKeyDown && !e.Handled)
        {
            _aggregator.AddKeystroke();
        }
    }

    private void OnCompositionChanged(object? sender, CompositionChangedEventArgs e)
    {
        var length = e.RawInput.Length;

        if (length == 1 && _lastLength != 1)
        {
            // 一段新的组字。这里决定这一段要不要记，之后整段沿用。
            if (ShouldIgnore())
            {
                _lastLength = length;
                return;
            }

            _aggregator.BeginComposition(ResolveProcess());

            // 触发「组字开始」的那个字母本身也要算进字母数，
            // 只把长度记成 1 会让每一段输入都少算一个字母。
            _aggregator.AddLetters(length);
            _lastLength = length;
            return;
        }

        if (length > _lastLength)
        {
            _aggregator.AddLetters(length - _lastLength);
        }

        _lastLength = length;
    }

    private void OnCompositionEnded(object? sender, CompositionEndedEventArgs e)
    {
        switch (e.Reason)
        {
            case CompositionEndReason.Committed:
                _aggregator.EndComposition(committed: true);
                break;

            case CompositionEndReason.Cancelled:
            case CompositionEndReason.FocusLost:
                _aggregator.EndComposition(committed: false);
                break;

            default:
                // 候选窗自己消失，判断不了是上屏还是放弃，两个计数都不动。
                _aggregator.DiscardComposition();
                break;
        }

        _lastLength = 0;
        _onChanged?.Invoke();
    }

    private void OnSuggestionAccepted(object? sender, CloudSuggestionAcceptedEventArgs e)
    {
        _aggregator.AddCloudAccept(e.Text);
        _lastLength = 0;
        _onChanged?.Invoke();
    }

    /// <summary>这一段输入是否应当完全忽略。</summary>
    /// <returns>忽略返回 true。</returns>
    /// <remarks>
    /// 忽略时干脆不调用 <c>BeginComposition</c>，累加器就不会进入组字态，
    /// 后续的按键与字母会被它的内部判断自然丢掉，这里不用再维护一份状态。
    /// </remarks>
    private static bool ShouldIgnore()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var processName = WindowProcess.GetProcessName(foreground);
        if (string.Equals(processName, OwnProcessName, StringComparison.OrdinalIgnoreCase))
        {
            // 在设置页里打字不该计入，否则用户一打开统计页数字就会跳。
            return true;
        }

        // 英文键盘布局下的字母不是拼音，记进去会让「字母数」虚高。
        return !MsPinyinDetector.IsChineseLayout();
    }

    /// <summary>确认这一段输入归属哪个程序。</summary>
    /// <returns>程序名；用户关闭按程序统计时返回 null。</returns>
    private string? ResolveProcess()
    {
        if (!_settings.StatisticsPerAppEnabled)
        {
            return null;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return StatisticsAggregator.UnknownProcess;
        }

        var processName = WindowProcess.GetProcessName(foreground);
        if (string.IsNullOrEmpty(processName))
        {
            return StatisticsAggregator.UnknownProcess;
        }

        if (string.Equals(processName, UwpHostProcessName, StringComparison.OrdinalIgnoreCase))
        {
            // 记事本、计算器这类 UWP 应用的窗口都挂在同一个宿主进程上，
            // 真实应用名只能通过 AppUserModelId 拿，代价与失败率都不划算，如实标注即可。
            return "UWP 应用";
        }

        return processName;
    }
}

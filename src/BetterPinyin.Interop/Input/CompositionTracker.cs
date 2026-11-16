using BetterPinyin.Contracts;

namespace BetterPinyin.Interop.Input;

/// <summary>
/// 当前按下的修饰键。
/// </summary>
[Flags]
public enum ModifierKeys
{
    /// <summary>无。</summary>
    None = 0,

    /// <summary>Shift。</summary>
    Shift = 1,

    /// <summary>Ctrl。</summary>
    Control = 2,

    /// <summary>Alt。</summary>
    Alt = 4,

    /// <summary>Win。</summary>
    Windows = 8,
}

/// <summary>
/// 通过按键流重建「用户正在输入的拼音串」。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接问输入法：微软拼音是 TSF 输入法，组字串保存在目标应用进程的 TSF 上下文里，
/// 跨进程没有任何受支持的读取方式。而用户在中文模式下敲的字母是可观测的，
/// 因此用按键重建 + 候选窗存在性作为「真的在组字」的判据，是当前最稳的组合。
/// </para>
/// <para>
/// 本类不调用任何 Win32 API，纯状态机，可直接单元测试。
/// </para>
/// <para>
/// 线程模型：<see cref="Feed"/> 来自键盘钩子线程，<see cref="Reset"/> 来自上屏流程所在的
/// 线程池线程。两者会并发访问同一份缓冲区，因此所有入口都走同一把锁。
/// 锁可重入，事件回调里再读 <see cref="Version"/> 不会自锁。
/// </para>
/// </remarks>
public sealed class CompositionTracker
{
    /// <summary>拼音串长度上限，防止异常输入导致无界增长。</summary>
    public const int MaxLength = 32;

    private readonly object _gate = new();
    private readonly char[] _buffer = new char[MaxLength];
    private int _length;
    private long _version;
    private bool _composing;

    /// <summary>当前是否处于组字状态。</summary>
    public bool IsComposing
    {
        get
        {
            lock (_gate)
            {
                return _composing;
            }
        }
    }

    /// <summary>当前重建出的拼音串（小写字母）。</summary>
    public string RawInput
    {
        get
        {
            lock (_gate)
            {
                return _length == 0 ? string.Empty : new string(_buffer, 0, _length);
            }
        }
    }

    /// <summary>当前已累积的字母数（英文模式下用来决定退格次数）。</summary>
    public int PendingLength
    {
        get
        {
            lock (_gate)
            {
                return _length;
            }
        }
    }

    /// <summary>当前代次号，每次变化递增。</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>组字串发生变化。</summary>
    public event EventHandler<CompositionChangedEventArgs>? CompositionChanged;

    /// <summary>组字结束。</summary>
    public event EventHandler<CompositionEndedEventArgs>? CompositionEnded;

    /// <summary>
    /// 输入一次按键。
    /// </summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <param name="isKeyDown">是否为按下；抬起事件会被忽略。</param>
    /// <param name="modifiers">当时按下的修饰键。</param>
    /// <param name="isInjected">是否为本程序注入的按键（注入的按键不参与重建）。</param>
    public void Feed(int virtualKey, bool isKeyDown, ModifierKeys modifiers, bool isInjected = false)
    {
        if (!isKeyDown || isInjected)
        {
            return;
        }

        lock (_gate)
        {
            FeedCore(virtualKey, modifiers);
        }
    }

    /// <summary>外部观察到候选窗已消失时调用，用于兜底结束组字。</summary>
    /// <param name="reason">结束原因。</param>
    public void NotifyCompositionLost(CompositionEndReason reason = CompositionEndReason.Unknown)
    {
        lock (_gate)
        {
            if (_composing)
            {
                End(reason);
            }
        }
    }

    /// <summary>强制复位，不触发事件。</summary>
    /// <remarks>
    /// 必须递增代次：否则上屏之后，之前发出的查询仍会通过 <see cref="Version"/> 校验，
    /// 把已经过期的浮窗重新弹出来。
    /// </remarks>
    public void Reset()
    {
        lock (_gate)
        {
            _length = 0;
            _composing = false;
            _version++;
        }
    }

    private void FeedCore(int virtualKey, ModifierKeys modifiers)
    {
        // 带 Ctrl / Alt / Win 的组合键是快捷键，不属于组字。
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            if (_composing)
            {
                End(CompositionEndReason.Cancelled);
            }

            return;
        }

        if (virtualKey is >= 'A' and <= 'Z')
        {
            // Shift + 字母 表示用户想直接上屏英文，不进入组字。
            if ((modifiers & ModifierKeys.Shift) != 0)
            {
                if (_composing)
                {
                    End(CompositionEndReason.Committed);
                }

                return;
            }

            Append((char)('a' + (virtualKey - 'A')));
            return;
        }

        if (!_composing)
        {
            return;
        }

        switch (virtualKey)
        {
            case 0x08: // Backspace
                Backspace();
                break;

            case 0x20: // Space
            case 0x0D: // Enter
            case 0x09: // Tab
                End(CompositionEndReason.Committed);
                break;

            case 0x1B: // Esc
                End(CompositionEndReason.Cancelled);
                break;

            case >= 0x31 and <= 0x39: // 1..9 选词
                End(CompositionEndReason.Committed);
                break;

            case 0x25: // Left
            case 0x26: // Up
            case 0x27: // Right
            case 0x28: // Down
                // 方向键是在候选列表里挪选中项，组字并没有结束。
                // 早先把它当成「可打印键」结束组字，翻候选时浮窗就会莫名消失。
                break;

            default:
                // 其余可打印键（标点、符号）都会让输入法定稿当前组字。
                if (IsCommitPunctuation(virtualKey))
                {
                    End(CompositionEndReason.Committed);
                }

                break;
        }
    }

    /// <summary>这个键是不是会让输入法定稿的标点 / 符号键。</summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <returns>是返回 true。</returns>
    /// <remarks>
    /// 必须用虚拟键码而不是 ASCII：美式键盘上 <c>; = , - . / `</c> 是 0xBA~0xC0，
    /// <c>[ \ ] '</c> 是 0xDB~0xDE，数字小键盘是 0x60~0x6F。
    /// 以前拿 ASCII 的 0x2C / 0x2E 去比，中文逗号、句号上屏后组字串根本没结束，
    /// 下一次输入就被拼成「上一次 + 这一次」——这正是「拼音残留并合并」的主因。
    /// </remarks>
    private static bool IsCommitPunctuation(int virtualKey) =>
        virtualKey is (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDE);

    private void Append(char c)
    {
        if (_length >= MaxLength)
        {
            return;
        }

        _buffer[_length++] = c;
        _composing = true;
        _version++;
        CompositionChanged?.Invoke(this, new CompositionChangedEventArgs(Snapshot(), _version));
    }

    private void Backspace()
    {
        if (_length == 0)
        {
            End(CompositionEndReason.Cancelled);
            return;
        }

        _length--;
        _version++;
        CompositionChanged?.Invoke(this, new CompositionChangedEventArgs(Snapshot(), _version));

        if (_length == 0)
        {
            End(CompositionEndReason.Cancelled);
        }
    }

    private void End(CompositionEndReason reason)
    {
        // 先快照再清零：事件订阅方（统计、诊断）需要知道这一次输入到底打了多长，
        // 清零之后再读 RawInput 只会拿到空串。
        var rawInput = Snapshot();
        var length = _length;

        _length = 0;
        _composing = false;
        _version++;
        CompositionEnded?.Invoke(this, new CompositionEndedEventArgs(_version, reason, rawInput, length));
    }

    /// <summary>在已持锁的前提下取当前串，避免事件参数里再次加锁。</summary>
    private string Snapshot() => _length == 0 ? string.Empty : new string(_buffer, 0, _length);
}

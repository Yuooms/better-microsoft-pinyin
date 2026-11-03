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
/// 为什么不直接问输入法：微软拼音是 TSF 输入法，组字串保存在目标应用进程的 TSF 上下文里，
/// 跨进程没有任何受支持的读取方式。而用户在中文模式下敲的字母是可观测的，
/// 因此用按键重建 + 候选窗存在性作为「真的在组字」的判据，是当前最稳的组合。
/// <para>
/// 本类不调用任何 Win32 API，纯状态机，可直接单元测试。
/// </para>
/// </remarks>
public sealed class CompositionTracker
{
    /// <summary>拼音串长度上限，防止异常输入导致无界增长。</summary>
    public const int MaxLength = 32;

    private readonly char[] _buffer = new char[MaxLength];
    private int _length;
    private long _version;

    /// <summary>当前是否处于组字状态。</summary>
    public bool IsComposing { get; private set; }

    /// <summary>当前重建出的拼音串（小写字母）。</summary>
    public string RawInput => _length == 0 ? string.Empty : new string(_buffer, 0, _length);

    /// <summary>当前已累积的字母数（英文模式下用来决定退格次数）。</summary>
    public int PendingLength => _length;

    /// <summary>当前代次号，每次变化递增。</summary>
    public long Version => _version;

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

        // 带 Ctrl / Alt / Win 的组合键是快捷键，不属于组字。
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            if (IsComposing)
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
                if (IsComposing)
                {
                    End(CompositionEndReason.Committed);
                }

                return;
            }

            Append((char)('a' + (virtualKey - 'A')));
            return;
        }

        if (!IsComposing)
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
                End(CompositionEndReason.Committed);
                break;

            case 0x1B: // Esc
                End(CompositionEndReason.Cancelled);
                break;

            case >= 0x31 and <= 0x39: // 1..9 选词
                End(CompositionEndReason.Committed);
                break;

            default:
                // 其余可打印键（标点、符号）都会让输入法定稿当前组字。
                if (virtualKey is >= 0x21 and <= 0x2F or >= 0x3A and <= 0x40 or >= 0x5B and <= 0x60 or >= 0x6B and <= 0x6F)
                {
                    End(CompositionEndReason.Committed);
                }

                break;
        }
    }

    /// <summary>外部观察到候选窗已消失时调用，用于兜底结束组字。</summary>
    /// <param name="reason">结束原因。</param>
    public void NotifyCompositionLost(CompositionEndReason reason = CompositionEndReason.Unknown)
    {
        if (IsComposing)
        {
            End(reason);
        }
    }

    /// <summary>强制复位，不触发事件。</summary>
    /// <remarks>
    /// 必须递增代次：否则上屏之后，之前发出的查询仍会通过 <see cref="Version"/> 校验，
    /// 把已经过期的浮窗重新弹出来。
    /// </remarks>
    public void Reset()
    {
        _length = 0;
        IsComposing = false;
        _version++;
    }

    private void Append(char c)
    {
        if (_length >= MaxLength)
        {
            return;
        }

        _buffer[_length++] = c;
        IsComposing = true;
        _version++;
        CompositionChanged?.Invoke(this, new CompositionChangedEventArgs(RawInput, _version));
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
        CompositionChanged?.Invoke(this, new CompositionChangedEventArgs(RawInput, _version));

        if (_length == 0)
        {
            End(CompositionEndReason.Cancelled);
        }
    }

    private void End(CompositionEndReason reason)
    {
        _length = 0;
        IsComposing = false;
        _version++;
        CompositionEnded?.Invoke(this, new CompositionEndedEventArgs(_version, reason));
    }
}

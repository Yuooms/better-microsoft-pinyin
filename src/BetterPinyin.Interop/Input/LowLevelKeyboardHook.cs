using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Interop.Input;

/// <summary>
/// 低级键盘钩子事件参数。
/// </summary>
public sealed class KeyboardHookEventArgs : EventArgs
{
    /// <summary>虚拟键码。</summary>
    public required int VirtualKey { get; init; }

    /// <summary>是否为按下（false 表示抬起）。</summary>
    public required bool IsKeyDown { get; init; }

    /// <summary>是否由 SendInput 之类的工具注入（含本程序）。</summary>
    public required bool IsInjected { get; init; }

    /// <summary>
    /// 是否是本程序自己注入的按键（靠 <c>dwExtraInfo</c> 上的标记识别）。
    /// 只有这一类必须被忽略，否则取消组字与上屏的按键会被自己重新计入拼音串。
    /// </summary>
    public required bool IsOwnInjection { get; init; }

    /// <summary>
    /// 设为 true 表示吞掉这次按键，不再传给目标窗口。
    /// 只在云候选浮窗可见且按下的正是采纳键时使用。
    /// </summary>
    public bool Handled { get; set; }
}

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）。
/// </summary>
/// <remarks>
/// 硬性约束：钩子回调运行在安装线程上，且系统会对整个输入链路上的所有按键等待它返回。
/// 因此回调里只做「判空 + 转交事件」，任何 I/O、UIA 调用、锁等待都会造成整机输入卡顿。
/// </remarks>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private IntPtr _hookHandle;
    private bool _disposed;

    /// <summary>创建钩子实例（尚未安装）。</summary>
    public LowLevelKeyboardHook()
    {
        // 必须保存委托实例，否则会被 GC 回收导致进程崩溃。
        _callback = HookCallback;
    }

    /// <summary>收到按键事件。回调线程即安装线程。</summary>
    public event EventHandler<KeyboardHookEventArgs>? KeyEvent;

    /// <summary>钩子是否已安装。</summary>
    public bool IsRunning => _hookHandle != IntPtr.Zero;

    /// <summary>安装钩子。</summary>
    /// <returns>是否成功。</returns>
    /// <exception cref="Win32Exception">安装失败时抛出，包含系统错误码。</exception>
    public bool Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return true;
        }

        var module = NativeMethods.GetModuleHandle(null);
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, module, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "安装低级键盘钩子失败");
        }

        return true;
    }

    /// <summary>卸载钩子。</summary>
    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
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
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || KeyEvent is null)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        if (!isDown && !isUp)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        var args = new KeyboardHookEventArgs
        {
            VirtualKey = (int)info.VirtualKeyCode,
            IsKeyDown = isDown,
            IsInjected = (info.Flags & NativeMethods.LLKHF_INJECTED) != 0,
            IsOwnInjection = info.ExtraInfo == NativeMethods.InjectionMarker,
        };

        try
        {
            KeyEvent.Invoke(this, args);
        }
        catch (Exception)
        {
            // 订阅方异常绝不能冒泡回系统钩子链，否则整个输入会失效。
        }

        return args.Handled
            ? 1
            : NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }
}

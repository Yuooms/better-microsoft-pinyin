using System.Runtime.InteropServices;

namespace BetterPinyin.Interop.Windowing;

/// <summary>锚点的来源，用于诊断与降级判断。</summary>
public enum AnchorSource
{
    /// <summary>系统插入符（原生编辑控件）。</summary>
    SystemCaret = 0,

    /// <summary>鼠标位置（拿不到插入符时的兜底）。</summary>
    Mouse = 1,

    /// <summary>屏幕固定位置（最后兜底）。</summary>
    ScreenFallback = 2,
}

/// <summary>
/// 浮窗锚点：一个屏幕坐标点（物理像素）及其来源。
/// </summary>
/// <param name="X">屏幕 X 坐标。</param>
/// <param name="Y">屏幕 Y 坐标。</param>
/// <param name="Source">来源。</param>
public readonly record struct AnchorPoint(int X, int Y, AnchorSource Source);

/// <summary>
/// 定位输入光标位置。
/// </summary>
/// <remarks>
/// 候选窗通常出现在插入符正下方，因此插入符是浮窗最自然的贴靠基准。
/// 但并非所有应用都提供系统插入符：Chromium、Electron、WPF、UWP 这类自绘文本控件
/// 会走 UIA 的 TextPattern 才能拿到光标矩形，而 <see cref="CaretLocator"/> 目前只覆盖
/// 有系统插入符的原生控件，其余情况由调用方降级到鼠标位置。
/// </remarks>
public static class CaretLocator
{
    /// <summary>
    /// 尝试获取前台窗口的插入符位置（屏幕坐标）。
    /// </summary>
    /// <returns>成功返回锚点；前台窗口没有系统插入符时返回 null。</returns>
    public static AnchorPoint? TryGetForegroundCaret()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return null;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        if (threadId == 0)
        {
            return null;
        }

        var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        if (!NativeMethods.GetGUIThreadInfo(threadId, ref info) || info.hwndCaret == IntPtr.Zero)
        {
            return null;
        }

        // rcCaret 是插入符所属窗口的客户区坐标，必须换算到屏幕坐标。
        var point = new NativePoint { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
        if (!NativeMethods.ClientToScreen(info.hwndCaret, ref point))
        {
            return null;
        }

        return new AnchorPoint(point.X, point.Y, AnchorSource.SystemCaret);
    }

    /// <summary>
    /// 获取一个可用的锚点：优先插入符，退而求其次用鼠标位置。
    /// </summary>
    /// <returns>锚点，始终有值。</returns>
    public static AnchorPoint GetAnchor()
    {
        var caret = TryGetForegroundCaret();
        if (caret is not null)
        {
            return caret.Value;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            return new AnchorPoint(cursor.X, cursor.Y, AnchorSource.Mouse);
        }

        return new AnchorPoint(
            NativeMethods.ScreenWidth / 3,
            NativeMethods.ScreenHeight / 2,
            AnchorSource.ScreenFallback);
    }
}

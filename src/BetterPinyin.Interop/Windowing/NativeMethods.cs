using System.Runtime.InteropServices;

namespace BetterPinyin.Interop.Windowing;

/// <summary>
/// 屏幕矩形（物理像素）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    /// <summary>左边界。</summary>
    public int Left;

    /// <summary>上边界。</summary>
    public int Top;

    /// <summary>右边界。</summary>
    public int Right;

    /// <summary>下边界。</summary>
    public int Bottom;

    /// <summary>宽度。</summary>
    public readonly int Width => Right - Left;

    /// <summary>高度。</summary>
    public readonly int Height => Bottom - Top;
}

/// <summary>
/// 光标信息（GetGUIThreadInfo 的 GUI_THREAD_INFO）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GuiThreadInfo
{
    /// <summary>结构大小，调用前必须赋值。</summary>
    public int cbSize;

    /// <summary>线程状态标志。</summary>
    public uint flags;

    /// <summary>拥有键盘焦点的窗口。</summary>
    public IntPtr hwndActive;

    /// <summary>拥有焦点的控件。</summary>
    public IntPtr hwndFocus;

    /// <summary>拥有键盘捕获的窗口。</summary>
    public IntPtr hwndCapture;

    /// <summary>拥有菜单的窗口。</summary>
    public IntPtr hwndMenuOwner;

    /// <summary>拥有移动尺寸边框的窗口。</summary>
    public IntPtr hwndMoveSize;

    /// <summary>拥有系统光标（插入符）的窗口。</summary>
    public IntPtr hwndCaret;

    /// <summary>插入符矩形（客户区坐标）。</summary>
    public NativeRect rcCaret;
}

/// <summary>
/// Win32 原生方法声明。只做声明，不含任何逻辑。
/// </summary>
public static class NativeMethods
{
    /// <summary>低级键盘钩子类型。</summary>
    public const int WH_KEYBOARD_LL = 13;

    /// <summary>键盘按下。</summary>
    public const int WM_KEYDOWN = 0x0100;

    /// <summary>键盘抬起。</summary>
    public const int WM_KEYUP = 0x0101;

    /// <summary>系统键按下。</summary>
    public const int WM_SYSKEYDOWN = 0x0104;

    /// <summary>系统键抬起。</summary>
    public const int WM_SYSKEYUP = 0x0105;

    /// <summary>获取前台窗口。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>获取窗口所属线程。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>获取指定线程的输入状态（含插入符位置）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo pgui);

    /// <summary>获取窗口类名。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    /// <summary>获取窗口屏幕矩形。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    /// <summary>获取指定点所在的窗口（用于命中沉浸式 IME 候选窗）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(NativePoint point);

    /// <summary>获取窗口的扩展样式。</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    /// <summary>枚举顶层窗口。返回 false 表示枚举结束。</summary>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>枚举所有顶层窗口。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>判断窗口是否可见。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>获取前台窗口所属线程的键盘布局。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    /// <summary>获取异步键状态（用于读取 Shift/Ctrl/Alt）。</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>判断 Ctrl 是否按下。</summary>
    public static bool IsCtrlDown => (GetAsyncKeyState(0x11) & 0x8000) != 0;

    /// <summary>判断 Alt 是否按下。</summary>
    public static bool IsAltDown => (GetAsyncKeyState(0x12) & 0x8000) != 0;

    /// <summary>判断 Win 键是否按下。</summary>
    public static bool IsWinDown => (GetAsyncKeyState(0x5B) & 0x8000) != 0;

    /// <summary>读取窗口类名，失败返回空串。</summary>
    public static string GetWindowClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new char[256];
        var length = GetClassName(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    // ── 键盘钩子 ────────────────────────────────────────────────────────────

    /// <summary>低级键盘钩子回调。</summary>
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>钩子结构中的「注入」标志位。</summary>
    public const uint LLKHF_INJECTED = 0x10;

    /// <summary>安装钩子。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    /// <summary>卸载钩子。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>把事件传递给下一个钩子。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>获取模块句柄。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>低级键盘钩子结构体（KBDLLHOOKSTRUCT）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        /// <summary>虚拟键码。</summary>
        public uint VirtualKeyCode;

        /// <summary>硬件扫描码。</summary>
        public uint ScanCode;

        /// <summary>标志位（含 LLKHF_INJECTED）。</summary>
        public uint Flags;

        /// <summary>时间戳。</summary>
        public uint Time;

        /// <summary>附加信息，本程序注入的按键会带上自定义标记。</summary>
        public IntPtr ExtraInfo;
    }

    // ── 按键注入 ────────────────────────────────────────────────────────────

    /// <summary>输入类型：键盘。</summary>
    public const uint INPUT_KEYBOARD = 1;

    /// <summary>按键抬起。</summary>
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>使用 Unicode 字符而非虚拟键码。</summary>
    public const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>本程序注入的按键统一携带这个标记，钩子据此放行自己的输入。</summary>
    public static readonly IntPtr InjectionMarker = new(0x42_50_59_4E);

    /// <summary>发送输入。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ── 消息循环 ────────────────────────────────────────────────────────────

    /// <summary>取一条消息。</summary>
    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>翻译消息。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    /// <summary>分发消息。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    /// <summary>投递退出消息。</summary>
    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    // ── 前台窗口控制（仅自测工具使用）──────────────────────────────────────

    /// <summary>把窗口设为前台。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>显示窗口。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>还原窗口（SW_RESTORE）。</summary>
    public const int SW_RESTORE = 9;

    // ── 控件文本读取（仅自测工具使用）──────────────────────────────────────

    /// <summary>读取控件文本。</summary>
    public const uint WM_GETTEXT = 0x000D;

    /// <summary>查找子窗口。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    /// <summary>发送消息（字符串版本）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, System.Text.StringBuilder lParam);

    /// <summary>附加线程输入队列，用于绕过前台窗口锁定。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    /// <summary>取当前线程 Id。</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>设置焦点窗口。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    /// <summary>把窗口提到 Z 序顶部。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// 强制把目标窗口置为前台。
    /// </summary>
    /// <param name="hWnd">目标窗口。</param>
    /// <returns>是否成功。</returns>
    /// <remarks>
    /// Windows 会阻止后台进程抢前台，标准做法是临时附加到当前前台线程的输入队列，
    /// 借用它的前台权限完成切换，再立刻解除附加，否则两个线程的输入状态会互相污染。
    /// </remarks>
    public static bool ForceForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        var foreground = GetForegroundWindow();
        if (foreground == hWnd)
        {
            return true;
        }

        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = AttachThreadInput(currentThread, foregroundThread, true);
            }

            BringWindowToTop(hWnd);
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
            SetFocus(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        Thread.Sleep(120);
        return GetForegroundWindow() == hWnd;
    }
}

/// <summary>窗口消息结构（MSG）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    /// <summary>目标窗口。</summary>
    public IntPtr hwnd;

    /// <summary>消息号。</summary>
    public uint message;

    /// <summary>参数一。</summary>
    public IntPtr wParam;

    /// <summary>参数二。</summary>
    public IntPtr lParam;

    /// <summary>时间戳。</summary>
    public uint time;

    /// <summary>屏幕坐标。</summary>
    public NativePoint pt;
}

/// <summary>键盘输入结构（联合体的键盘分支）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    /// <summary>虚拟键码；使用 Unicode 模式时忽略。</summary>
    public ushort VirtualKey;

    /// <summary>硬件扫描码。</summary>
    public ushort ScanCode;

    /// <summary>标志位。</summary>
    public uint Flags;

    /// <summary>时间戳，0 表示由系统填充。</summary>
    public uint Time;

    /// <summary>附加信息。</summary>
    public IntPtr ExtraInfo;
}

/// <summary>
/// INPUT 结构。
/// </summary>
/// <remarks>
/// x64 下 INPUT 的真实大小是 40 字节（联合体中最大的是 MOUSEINPUT），
/// 这里用 <see cref="LayoutKind.Explicit"/> 显式补齐，否则 SendInput 会以参数非法失败。
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 40)]
public struct INPUT
{
    /// <summary>输入类型，见 <see cref="NativeMethods.INPUT_KEYBOARD"/>。</summary>
    [FieldOffset(0)]
    public uint Type;

    /// <summary>键盘输入，联合体内部偏移为 8。</summary>
    [FieldOffset(8)]
    public KEYBDINPUT Keyboard;
}

/// <summary>屏幕坐标点（物理像素）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativePoint
{
    /// <summary>X 坐标。</summary>
    public int X;

    /// <summary>Y 坐标。</summary>
    public int Y;
}

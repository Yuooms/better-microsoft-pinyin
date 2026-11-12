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
/// 显示器信息（<c>MONITORINFO</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MonitorInfo
{
    /// <summary>结构大小，调用前必须赋值。</summary>
    public int Size;

    /// <summary>显示器矩形（物理像素）。</summary>
    public NativeRect Monitor;

    /// <summary>工作区矩形（已排除任务栏）。</summary>
    public NativeRect Work;

    /// <summary>显示器标志。</summary>
    public uint Flags;
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

    /// <summary>读取窗口标题。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="text">接收缓冲。</param>
    /// <param name="maxCount">缓冲长度。</param>
    /// <returns>实际字符数。</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

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

    /// <summary>设置窗口的扩展样式。</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>扩展样式索引。</summary>
    public const int GWL_EXSTYLE = -20;

    /// <summary>窗口不激活（点击不抢焦点）。</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>工具窗口（不出现在任务栏与 Alt+Tab）。</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>鼠标点击穿透到下层窗口。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>置顶窗口。</summary>
    public const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>置顶窗口的 Z 序插值。</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>保持尺寸不变。</summary>
    public const uint SWP_NOSIZE = 0x0001;

    /// <summary>保持位置不变。</summary>
    public const uint SWP_NOMOVE = 0x0002;

    /// <summary>保持 Z 序不变。</summary>
    public const uint SWP_NOZORDER = 0x0004;

    /// <summary>不激活窗口。</summary>
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>强制重新计算非客户区（改标题栏颜色后需要一次）。</summary>
    public const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>显示窗口。</summary>
    public const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>设置窗口位置与尺寸（物理像素）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    /// <summary>强制重绘窗口的指定区域。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    /// <summary>重绘非客户区（标题栏）。</summary>
    public const uint RDW_INVALIDATE = 0x0001;

    /// <summary>立即重绘，不等下一次消息循环。</summary>
    public const uint RDW_UPDATENOW = 0x0100;

    /// <summary>包含非客户区。</summary>
    public const uint RDW_FRAME = 0x0400;

    /// <summary>获取窗口所在显示器的 DPI。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>
    /// 给窗口加上「不抢焦点、点击穿透」的扩展样式。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <remarks>
    /// 云候选浮窗绝不能激活：一旦抢走焦点，输入法会立刻提交组字，
    /// 用户还没按采纳键，拼音就已经上屏了。点击穿透则保证浮窗不会挡住用户操作。
    /// </remarks>
    public static void ApplyNoActivateStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

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

    /// <summary>列出本机已加载的键盘布局，返回实际写入的数量。</summary>
    [DllImport("user32.dll")]
    public static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    /// <summary>加载键盘布局，<paramref name="flags"/> 传 0 表示只加载不激活。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    /// <summary>为调用线程激活已加载的键盘布局。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

    /// <summary>向窗口投递消息（不等待处理）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>请求窗口所在线程切换输入法（DefWindowProc 会转成 ActivateKeyboardLayout）。</summary>
    public const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

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

    /// <summary>把客户区坐标转换为屏幕坐标。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    /// <summary>获取鼠标位置。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out NativePoint lpPoint);

    /// <summary>获取系统度量值。</summary>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>主显示器宽度（物理像素）。</summary>
    public static int ScreenWidth => GetSystemMetrics(0);

    /// <summary>主显示器高度（物理像素）。</summary>
    public static int ScreenHeight => GetSystemMetrics(1);


    /// <summary><c>WCA_ACCENT_POLICY</c>：窗口合成属性里的强调色策略。</summary>
    public const int WCA_ACCENT_POLICY = 19;

    /// <summary><c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c>：亚克力模糊。</summary>
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    /// <summary>
    /// 设置窗口合成属性（未公开 API，Win10 1803+ 可用）。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>设置 DWM 窗口属性。</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>把 DWM 边框扩展到整个客户区，配合透明背景显示模糊。</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>创建圆角矩形区域。</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    /// <summary>设置窗口区域（系统接管区域句柄）。</summary>
    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    /// <summary>DWM 边框边距。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        /// <summary>左边距。</summary>
        public int Left;

        /// <summary>右边距。</summary>
        public int Right;

        /// <summary>上边距。</summary>
        public int Top;

        /// <summary>下边距。</summary>
        public int Bottom;
    }

    /// <summary>窗口合成属性数据。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttributeData
    {
        /// <summary>属性编号。</summary>
        public int Attribute;

        /// <summary>数据指针。</summary>
        public IntPtr Data;

        /// <summary>数据大小。</summary>
        public int SizeOfData;
    }

    /// <summary>强调色策略。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        /// <summary>状态，见 <see cref="ACCENT_ENABLE_ACRYLICBLURBEHIND"/>。</summary>
        public int AccentState;

        /// <summary>标志位。</summary>
        public int AccentFlags;

        /// <summary>着色，格式为 AABBGGRR。</summary>
        public int GradientColor;

        /// <summary>动画编号。</summary>
        public int AnimationId;
    }

    /// <summary>取某点所在显示器的工作区（物理像素）。</summary>
    /// <param name="point">屏幕坐标。</param>
    /// <returns>工作区矩形；查询失败时退回主屏。</returns>
    /// <remarks>
    /// 浮窗定位必须用工作区而不是主屏尺寸：多显示器下主屏尺寸会把位于副屏的浮窗拽回来，
    /// 而且任务栏占据的区域也应当排除。
    /// </remarks>
    public static NativeRect GetWorkArea(NativePoint point)
    {
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                return info.Work;
            }
        }

        return new NativeRect { Left = 0, Top = 0, Right = ScreenWidth, Bottom = ScreenHeight };
    }

    /// <summary>取最近显示器（<c>MONITOR_DEFAULTTONEAREST</c>）。</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>按屏幕坐标取最近的显示器。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    /// <summary>取显示器信息。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    /// <summary>DWM 属性：窗口是否被隐身（cloaked）。</summary>
    public const int DWMWA_CLOAKED = 14;

    /// <summary>查询窗口的 DWM 属性（整型）。</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>
    /// 判断窗口是否被 DWM 隐身。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>被隐身返回 true；查询失败时返回 false。</returns>
    /// <remarks>
    /// 沉浸式输入法的宿主窗口在非激活状态下常被 cloak，
    /// 对 cloak 窗口做 UIA 读取会得到空子树，因此发现候选窗时必须排除它们。
    /// </remarks>
    public static bool IsCloaked(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

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

        // 以前这里定长睡 120ms，而调用点在 UI 线程上，等于每次打开设置窗口先冻 120ms。
        // 实际绝大多数情况几毫秒就切换完成，改成短间隔轮询。
        var deadline = Environment.TickCount64 + 50;
        while (GetForegroundWindow() != hWnd && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }

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

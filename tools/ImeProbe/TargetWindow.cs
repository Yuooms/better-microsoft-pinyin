using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Tools.ImeProbe;

/// <summary>
/// 按进程名定位一个可输入的目标窗口，并把它切到前台。
/// </summary>
/// <remarks>
/// <para>存在的理由：Windows 11 的记事本是 UWP 应用，<c>Process.Start("notepad.exe")</c>
/// 返回的是一个立刻退出的转发进程，真实窗口挂在另一个 PID 上，
/// 因此「启动记事本再等它的主窗口」这条路在 Win11 上必然超时。</para>
/// <para>改为按进程名找**已经存在**的窗口，测试就不再依赖目标程序怎么启动，
/// 也不再需要杀掉用户可能正在编辑的文档。</para>
/// </remarks>
internal static class TargetWindow
{
    /// <summary>按进程名查找主窗口。</summary>
    /// <param name="processName">不含扩展名的进程名，例如 <c>Notepad</c>。</param>
    /// <returns>窗口句柄；找不到返回 <see cref="IntPtr.Zero"/>。</returns>
    public static IntPtr Find(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[目标] 枚举进程 {processName} 失败：{ex.Message}");
            return IntPtr.Zero;
        }

        foreach (var process in processes)
        {
            try
            {
                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已退出。
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 把指定窗口切到前台，并确认前台确实切过去了。
    /// </summary>
    /// <param name="handle">目标窗口句柄。</param>
    /// <returns>切到前台返回 true。</returns>
    /// <remarks>
    /// 后续要往这个窗口发按键，所以必须复核一次：切不过去就绝不能继续，
    /// 否则字母会打进用户正在用的程序里。
    /// </remarks>
    public static bool Focus(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
        NativeMethods.ForceForegroundWindow(handle);
        Thread.Sleep(350);

        var foreground = NativeMethods.GetForegroundWindow();
        return foreground == handle;
    }

    /// <summary>按进程名查找并切到前台，一步完成。</summary>
    /// <param name="processName">不含扩展名的进程名。</param>
    /// <returns>成功返回窗口句柄，失败返回 <see cref="IntPtr.Zero"/>。</returns>
    public static IntPtr Activate(string processName)
    {
        var handle = Find(processName);
        if (handle == IntPtr.Zero)
        {
            Console.WriteLine($"[目标] 没找到进程 {processName} 的窗口");
            return IntPtr.Zero;
        }

        NativeMethods.GetWindowRect(handle, out var rect);
        Console.WriteLine($"[目标] 0x{handle.ToInt64():X} \"{ReadTitle(handle)}\" " +
                          $"({rect.Left},{rect.Top}) {rect.Width}x{rect.Height}");

        // 布局是「每线程」的：控制台是中文布局不代表目标窗口也是。
        // 打字前必须先看清目标线程到底挂在哪个布局上，否则会得出"输入法没反应"的错误结论。
        var threadId = NativeMethods.GetWindowThreadProcessId(handle, out _);
        var layout = threadId == 0 ? IntPtr.Zero : NativeMethods.GetKeyboardLayout(threadId);
        var languageId = (ushort)(layout.ToInt64() & 0xFFFF);
        Console.WriteLine($"[目标] 线程布局 HKL=0x{layout.ToInt64():X8} 语言=0x{languageId:X4}");

        if (!Focus(handle))
        {
            Console.WriteLine("[目标] ✗ 未能切到前台");
            return IntPtr.Zero;
        }

        return handle;
    }

    /// <summary>按标题包含的关键字查找可见的顶层窗口。</summary>
    /// <param name="keyword">标题里要包含的文字，不区分大小写。</param>
    /// <returns>窗口句柄；找不到返回 <see cref="IntPtr.Zero"/>。</returns>
    /// <remarks>
    /// 进程名可能对不上（例如设置窗口的进程名是 <c>BetterMicrosoftPinyin</c> 而标题是「Better 微软拼音」），
    /// 所以截图这类只读操作额外给一条按标题找的路。
    /// </remarks>
    public static IntPtr FindByTitle(string keyword)
    {
        var found = IntPtr.Zero;

        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                var buffer = new System.Text.StringBuilder(512);
                NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
                if (buffer.Length == 0)
                {
                    return true;
                }

                if (buffer.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);

        return found;
    }

    /// <summary>列出某个进程当前所有的可见主窗口。</summary>
    /// <param name="processName">不含扩展名的进程名。</param>
    /// <returns>窗口句柄集合；没有时返回空集合。</returns>
    /// <remarks>
    /// 用途是"启动前后对比"：只有启动之后才出现的窗口才是本次测试自己的，
    /// 否则会把用户正在编辑的文档当成测试目标。
    /// </remarks>
    public static HashSet<IntPtr> ListByProcessName(string processName)
    {
        var result = new HashSet<IntPtr>();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var process in processes)
        {
            try
            {
                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    result.Add(handle);
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已退出。
            }
        }

        return result;
    }

    /// <summary>等待某个进程出现一个新的主窗口。</summary>
    /// <param name="processName">不含扩展名的进程名。</param>
    /// <param name="existing">调用方启动前已经存在的窗口，这些会被跳过。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <returns>新窗口句柄；超时返回 <see cref="IntPtr.Zero"/>。</returns>
    public static IntPtr WaitForNewWindow(string processName, HashSet<IntPtr> existing, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            foreach (var handle in ListByProcessName(processName))
            {
                if (!existing.Contains(handle))
                {
                    return handle;
                }
            }

            Thread.Sleep(150);
        }

        return IntPtr.Zero;
    }

    /// <summary>窗口是否已最小化。最小化时 PrintWindow 通常画不出内容。</summary>
    /// <param name="handle">窗口句柄。</param>
    /// <returns>已最小化返回 true。</returns>
    public static bool IsMinimized(IntPtr handle) => IsIconic(handle);

    private static string ReadTitle(IntPtr handle)
    {
        var buffer = new System.Text.StringBuilder(256);
        NativeMethods.GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}

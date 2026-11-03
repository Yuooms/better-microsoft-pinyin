using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Input;
using BetterPinyin.Interop.Uia;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Tools.ImeProbe;

/// <summary>
/// 几何命中测试：在候选条附近逐点调用 <c>WindowFromPoint</c>，找出真正的候选窗句柄。
/// </summary>
/// <remarks>
/// 存在的理由：沉浸式输入法的候选条位于独立 window band，
/// <c>EnumWindows</c> 等枚举类 API 拿不到它，只有按坐标命中才能找到。
/// 之前按进程名枚举到的是输入宿主的全屏站点窗口（1920x1080），它的 UIA 子树是空的，
/// 因此「读到 0 个元素」并不代表 UIA 不可用，而是句柄不对。
/// 本测试就是在验证这一点。
/// </remarks>
internal static class HitTest
{
    /// <summary>执行一次命中测试。</summary>
    /// <param name="reader">候选窗读取器。</param>
    /// <param name="sample">模拟输入的拼音串。</param>
    /// <param name="shotPath">可选截图路径。</param>
    /// <returns>退出码：0 表示命中了疑似候选窗。</returns>
    public static int Run(UiaCandidateWindowReader reader, string sample, string? shotPath)
    {
        Console.WriteLine($"[命中] 打开记事本并模拟输入 \"{sample}\"，随后在插入符附近逐点探测");

        using var notepad = SelfTest.StartNotepad();
        if (notepad is null)
        {
            Console.WriteLine("[命中] 无法启动记事本");
            return 2;
        }

        var editorHandle = SelfTest.WaitForMainWindow(notepad, TimeSpan.FromSeconds(6));
        if (editorHandle == IntPtr.Zero)
        {
            Console.WriteLine("[命中] 等待记事本窗口超时");
            SelfTest.SafeKill(notepad);
            return 3;
        }

        NativeMethods.ShowWindow(editorHandle, NativeMethods.SW_RESTORE);
        if (!NativeMethods.ForceForegroundWindow(editorHandle))
        {
            Console.WriteLine("[命中] ✗ 无法把记事本切到前台，已中止（不发送任何按键）");
            SelfTest.SafeKill(notepad);
            return 6;
        }

        var editHandle = NativeMethods.FindWindowEx(editorHandle, IntPtr.Zero, "Edit", null);
        if (editHandle != IntPtr.Zero)
        {
            NativeMethods.SetFocus(editHandle);
        }

        Thread.Sleep(200);
        if (NativeMethods.GetForegroundWindow() != editorHandle)
        {
            Console.WriteLine("[命中] ✗ 前台已变化，已中止");
            SelfTest.SafeKill(notepad);
            return 6;
        }

        SelfTest.TypePinyin(sample);
        Thread.Sleep(350);

        var origin = ResolveProbeOrigin();
        Console.WriteLine($"[命中] 探测原点 = ({origin.X},{origin.Y})，来源 = {DescribeOrigin()}");

        // 候选条通常出现在插入符下方，也可能被上方翻转，因此双向都探。
        int[] offsetsY = { 2, 8, 18, 28, 40, -22, -34 };
        int[] offsetsX = { 0, 24, 60, 120, -24 };

        var hits = new Dictionary<IntPtr, string>();
        foreach (var dy in offsetsY)
        {
            foreach (var dx in offsetsX)
            {
                var x = origin.X + dx;
                var y = origin.Y + dy;
                var hwnd = UiaCandidateWindowReader.WindowAt(x, y);
                if (hwnd == IntPtr.Zero)
                {
                    continue;
                }

                var description = DescribeWindow(hwnd);
                var key = $"{x,5},{y,5} → {description}";
                Console.WriteLine($"    {key}");

                if (!hits.ContainsKey(hwnd))
                {
                    hits[hwnd] = description;
                }
            }
        }

        // 把输入法宿主窗口也列出来对比，说明"枚举到的"和"命中的"不是同一个窗口。
        Console.WriteLine("[命中] 按进程名枚举到的输入法窗口（用于对比）：");
        foreach (var (handle, process, className) in reader.DiscoverImeWindows())
        {
            NativeMethods.GetWindowRect(handle, out var rect);
            Console.WriteLine($"    0x{handle.ToInt64():X}  {process,-16} {className,-30} " +
                              $"({rect.Left},{rect.Top}) {rect.Width}x{rect.Height} " +
                              $"cloaked={NativeMethods.IsCloaked(handle)}");
        }

        Console.WriteLine($"[命中] 不同窗口共 {hits.Count} 个，逐个尝试读取 UIA 子树：");
        var candidateFound = false;
        foreach (var (hwnd, description) in hits)
        {
            NativeMethods.GetWindowRect(hwnd, out var rect);
            var isSmallWindow = rect.Width > 0 && rect.Height > 0 &&
                                rect.Width < 1920 * 0.8 && rect.Height < 1080 * 0.5;

            var elements = reader.ReadAllElements(hwnd);
            var listItems = elements.Count(e => e.ControlType == UiaCandidateWindowReader.ListItemControlTypeId && !e.IsOffscreen);

            Console.WriteLine($"    ── 0x{hwnd.ToInt64():X} {description}");
            Console.WriteLine($"       元素总数={elements.Count}，可见 ListItem={listItems}，" +
                              $"疑似候选窗={(isSmallWindow ? "是" : "否")}");
            Console.WriteLine($"       诊断：{UiaCandidateWindowReader.LastDiagnostic}");

            foreach (var element in elements.Take(30))
            {
                Console.WriteLine($"       {element}");
            }

            if (listItems > 0)
            {
                candidateFound = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(shotPath))
        {
            NativeMethods.GetWindowRect(editorHandle, out var windowRect);
            const int margin = 160;
            if (ScreenGrabber.Capture(
                    shotPath,
                    Math.Max(0, windowRect.Left - margin),
                    Math.Max(0, windowRect.Top - margin),
                    windowRect.Width + (margin * 2),
                    windowRect.Height + (margin * 2),
                    2,
                    out var shotWidth,
                    out var shotHeight))
            {
                Console.WriteLine($"[命中] 截图已保存：{shotPath}（{shotWidth}x{shotHeight}）");
            }
        }

        KeySender.SendVirtualKey(0x1B, markAsOwn: false);
        Thread.Sleep(120);
        SelfTest.SafeKill(notepad);

        Console.WriteLine(candidateFound
            ? "[命中] ✓ 找到可读取的候选列表"
            : "[命中] ✗ 未找到可读取的候选列表");

        return candidateFound ? 0 : 4;
    }

    private static NativePoint ResolveProbeOrigin()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);

        if (threadId != 0)
        {
            var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
            if (NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.hwndCaret != IntPtr.Zero)
            {
                // rcCaret 是客户区坐标，必须换算到屏幕坐标才能做 WindowFromPoint。
                var point = new NativePoint { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
                if (NativeMethods.ClientToScreen(info.hwndCaret, ref point))
                {
                    return point;
                }
            }
        }

        // 拿不到插入符时退回鼠标位置，至少让流程能继续。
        return NativeMethods.GetCursorPos(out var cursor) ? cursor : new NativePoint { X = 200, Y = 200 };
    }

    private static string DescribeOrigin() => "插入符/鼠标兜底";

    private static string DescribeWindow(IntPtr hwnd)
    {
        var processId = WindowProcess.GetProcessId(hwnd);
        var processName = WindowProcess.GetProcessName(hwnd);
        var className = NativeMethods.GetWindowClassName(hwnd);
        NativeMethods.GetWindowRect(hwnd, out var rect);

        return $"0x{hwnd.ToInt64():X} pid={processId} {processName} class={className} " +
               $"rect=({rect.Left},{rect.Top}) {rect.Width}x{rect.Height} " +
               $"visible={NativeMethods.IsWindowVisible(hwnd)} cloaked={NativeMethods.IsCloaked(hwnd)}";
    }
}

using System.Diagnostics;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Input;
using BetterPinyin.Interop.Uia;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Tools.ImeProbe;

/// <summary>
/// 自测模式：自动打开记事本、唤醒微软拼音、模拟输入，然后把候选窗的读取结果打印出来。
/// </summary>
/// <remarks>
/// 存在的意义是把「候选窗能不能读」这个架构级问题变成一条可重复执行的命令，
/// 而不是每次都要人工敲键盘验证。它会短暂占用前台窗口，仅用于开发期诊断。
/// </remarks>
internal static class SelfTest
{
    /// <summary>执行一次完整的自测。</summary>
    /// <param name="worker">UIA 线程。</param>
    /// <param name="reader">候选窗读取器。</param>
    /// <param name="sample">要模拟输入的拼音串。</param>
    /// <returns>进程退出码：0 表示成功读到候选窗。</returns>
    public static int Run(
        UiaWorker worker,
        UiaCandidateWindowReader reader,
        string sample,
        int holdSeconds = 0,
        string? shotPath = null,
        int acceptKey = 0,
        bool englishMode = false)
    {
        Console.WriteLine($"[自测] 将打开记事本并模拟输入 \"{sample}\"（采纳键 0x{acceptKey:X2}）");
        Console.WriteLine("[自测] 注意：期间会抢占前台窗口，约 5 秒后自动恢复");

        using var notepad = StartNotepad();
        if (notepad is null)
        {
            Console.WriteLine("[自测] 无法启动记事本");
            return 2;
        }

        var editorHandle = WaitForMainWindow(notepad, TimeSpan.FromSeconds(6));
        if (editorHandle == IntPtr.Zero)
        {
            Console.WriteLine("[自测] 等待记事本窗口超时");
            SafeKill(notepad);
            return 3;
        }

        NativeMethods.ShowWindow(editorHandle, NativeMethods.SW_RESTORE);
        var focused = NativeMethods.ForceForegroundWindow(editorHandle);
        Console.WriteLine(focused
            ? "[自测] 已把记事本切到前台"
            : "[自测] 未能把记事本切到前台，输入可能落到了别的窗口");
        Thread.Sleep(300);

        // 记事本的编辑区是子控件，必须把焦点交给它，否则按键没有落点。
        var editHandle = NativeMethods.FindWindowEx(editorHandle, IntPtr.Zero, "Edit", null);
        Console.WriteLine(editHandle != IntPtr.Zero
            ? $"[自测] 找到编辑区 0x{editHandle.ToInt64():X}"
            : "[自测] 未找到 Edit 子控件（记事本可能是新版控件结构）");

        if (editHandle != IntPtr.Zero)
        {
            NativeMethods.SetFocus(editHandle);
            Thread.Sleep(150);
        }

        var foreground = NativeMethods.GetForegroundWindow();
        Console.WriteLine($"[自测] 前台窗口 = 0x{foreground.ToInt64():X}（目标 0x{editorHandle.ToInt64():X}），" +
                          $"进程 = {WindowProcess.GetProcessName(foreground)}");

        if (foreground != editorHandle)
        {
            // 抢焦失败时绝不能继续发送按键，否则会把字母打进用户正在使用的窗口。
            Console.WriteLine("[自测] ✗ 前台不是记事本，已中止，未发送任何按键");
            DumpImeWindowCandidates(reader);
            SafeKill(notepad);
            return 6;
        }

        if (englishMode)
        {
            // 按一下 Shift 把微软拼音切到英文状态，此时字母会直接进文档。
            KeySender.SendVirtualKey(0x10, markAsOwn: false);
            Thread.Sleep(300);
            Console.WriteLine("[自测] 已尝试切到英文状态");
        }

        TypePinyin(sample);
        Thread.Sleep(400);

        if (englishMode)
        {
            Thread.Sleep(900);
            var typed = editHandle != IntPtr.Zero ? ReadEditorText(editHandle) : string.Empty;
            Console.WriteLine($"[自测] 英文模式输入后内容 = \"{typed}\"");

            if (acceptKey != 0)
            {
                Console.WriteLine($"[自测] 发送采纳键 0x{acceptKey:X2}");
                KeySender.SendVirtualKey((ushort)acceptKey, markAsOwn: false);
                Thread.Sleep(900);
                var after = editHandle != IntPtr.Zero ? ReadEditorText(editHandle) : string.Empty;
                Console.WriteLine($"[自测] 采纳后内容 = \"{after}\"");
                Console.WriteLine(after.Length > 0 && after != typed
                    ? "[自测] ✓ 英文模式替换已生效"
                    : "[自测] ✗ 英文模式未生效（可能仍在中文状态，或校验未通过）");
            }

            CancelComposition();
            SafeKill(notepad);
            return 0;
        }

        var editedText = editHandle != IntPtr.Zero ? ReadEditorText(editHandle) : string.Empty;
        Console.WriteLine($"[自测] 记事本当前内容 = \"{editedText}\"" +
                          (string.IsNullOrEmpty(editedText)
                              ? "（为空说明按键没有进入输入法，可能前台或焦点被抢）"
                              : "（非空说明按键确实被输入法接收了）"));

        if (holdSeconds > 0)
        {
            Console.WriteLine($"[自测] 保持画面 {holdSeconds} 秒，便于外部截图取证");
            Thread.Sleep(holdSeconds * 1000);
        }

        if (!string.IsNullOrWhiteSpace(shotPath))
        {
            // 只截记事本窗口周围一块，全屏图太大不便查看。
            NativeMethods.GetWindowRect(editorHandle, out var windowRect);
            const int margin = 140;
            var regionX = Math.Max(0, windowRect.Left - margin);
            var regionY = Math.Max(0, windowRect.Top - margin);
            var regionWidth = windowRect.Width + (margin * 2);
            var regionHeight = windowRect.Height + (margin * 2);

            if (ScreenGrabber.Capture(shotPath, regionX, regionY, regionWidth, regionHeight, 2, out var shotWidth, out var shotHeight))
            {
                Console.WriteLine($"[自测] 截图已保存：{shotPath}（{shotWidth}x{shotHeight}，原区域 {regionWidth}x{regionHeight}）");
            }
            else
            {
                Console.WriteLine("[自测] 截图失败");
            }
        }

        var anchor = CaretLocator.GetAnchor();
        Console.WriteLine($"[自测] 锚点 = ({anchor.X},{anchor.Y})，来源 = {anchor.Source}");

        var handle = WaitForCandidateWindow(reader, anchor, TimeSpan.FromSeconds(2));
        if (handle == IntPtr.Zero)
        {
            Console.WriteLine("[自测] ✗ 未发现候选窗。可能原因：输入法处于英文模式、或锚点附近没有候选条。");
            DumpImeWindowCandidates(reader);
            DumpHostElementTree(reader);
            CancelComposition();
            SafeKill(notepad);
            return 4;
        }

        Console.WriteLine($"[自测] ✓ 发现候选窗 0x{handle.ToInt64():X}");
        var snapshot = reader.Read(handle);
        Console.WriteLine($"[自测] 读取质量 = {snapshot.Quality}");
        Console.WriteLine($"[自测] 候选窗矩形 = ({snapshot.Left},{snapshot.Top}) {snapshot.Width}x{snapshot.Height}");
        Console.WriteLine($"[自测] 候选词 {snapshot.Items.Count} 个：");
        foreach (var item in snapshot.Items)
        {
            Console.WriteLine($"    {item.Text}");
        }

        Console.WriteLine("[自测] 完整 UIA 元素树：");
        foreach (var element in reader.ReadAllElements(handle))
        {
            Console.WriteLine($"    {element}");
        }

        if (acceptKey != 0)
        {
            // 等云端查询完成、浮窗与按键映射就绪后再按，否则按键会被输入法当成选词。
            Thread.Sleep(600);
            Console.WriteLine($"[自测] 发送采纳键 0x{acceptKey:X2}");
            KeySender.SendVirtualKey((ushort)acceptKey, markAsOwn: false);
            Thread.Sleep(800);
            var committed = editHandle != IntPtr.Zero ? ReadEditorText(editHandle) : string.Empty;
            Console.WriteLine($"[自测] 采纳后内容 = \"{committed}\"");
            Console.WriteLine(committed.Length > 0
                ? "[自测] ✓ 云候选已上屏"
                : "[自测] ✗ 未检测到上屏内容");
        }

        CancelComposition();
        Thread.Sleep(150);
        SafeKill(notepad);

        return snapshot.Items.Count > 0 ? 0 : 5;
    }

    internal static void TypePinyin(string sample)
    {
        var index = 0;
        foreach (var ch in sample)
        {
            var virtualKey = char.ToUpperInvariant(ch);
            var ok = KeySender.SendVirtualKey(virtualKey, markAsOwn: false);
            if (!ok && index == 0)
            {
                Console.WriteLine($"[自测] ✗ SendInput 失败，Win32 错误码 = {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                return;
            }

            index++;
            Thread.Sleep(12);
        }

        Console.WriteLine($"[自测] 已发送 {index} 个字母");
    }

    private static IntPtr WaitForCandidateWindow(
        UiaCandidateWindowReader reader,
        AnchorPoint anchor,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var handle = reader.LocateNear(anchor);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }

            Thread.Sleep(80);
        }

        return IntPtr.Zero;
    }

    private static void DumpImeWindowCandidates(UiaCandidateWindowReader reader)
    {
        var windows = reader.DiscoverImeWindows();
        Console.WriteLine($"[自测] 输入法相关顶层窗口 {windows.Count} 个：");
        foreach (var (handle, process, className) in windows)
        {
            NativeMethods.GetWindowRect(handle, out var rect);
            Console.WriteLine($"    0x{handle.ToInt64():X}  {process,-16} {className,-28} " +
                              $"({rect.Left},{rect.Top}) {rect.Width}x{rect.Height}");
        }
    }

    /// <summary>把输入法宿主窗口的完整 UIA 元素树打印出来，用于判断 UIA 到底能不能看到候选面板。</summary>
    private static void DumpHostElementTree(UiaCandidateWindowReader reader)
    {
        foreach (var (handle, process, className) in reader.DiscoverImeWindows())
        {
            if (!process.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) &&
                !process.Equals("ChsIME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var elements = reader.ReadAllElements(handle);
            Console.WriteLine($"[自测] 0x{handle.ToInt64():X} ({process}/{className}) 的 UIA 元素数 = {elements.Count}");
            Console.WriteLine($"[自测] 诊断：{UiaCandidateWindowReader.LastDiagnostic}");
            foreach (var element in elements.Take(60))
            {
                Console.WriteLine($"    {element}");
            }
        }
    }

    private static string ReadEditorText(IntPtr editHandle)
    {
        if (editHandle == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(2048);
        NativeMethods.SendMessage(editHandle, NativeMethods.WM_GETTEXT, new IntPtr(buffer.Capacity), buffer);
        return buffer.ToString();
    }

    private static void CancelComposition()
    {
        KeySender.SendVirtualKey(0x1B, markAsOwn: false);
        Thread.Sleep(80);
    }

    internal static Process? StartNotepad()
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[自测] 启动记事本异常：{ex.Message}");
            return null;
        }
    }

    internal static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            Thread.Sleep(120);
        }

        return IntPtr.Zero;
    }

    internal static void SafeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
        }
        catch (Exception)
        {
            // 已退出或无权访问，忽略。
        }
    }
}

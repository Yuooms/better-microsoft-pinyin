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
    public static int Run(UiaWorker worker, UiaCandidateWindowReader reader, string sample)
    {
        Console.WriteLine($"[自测] 将打开记事本并模拟输入 \"{sample}\"");
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
        Thread.Sleep(400);

        var foreground = NativeMethods.GetForegroundWindow();
        Console.WriteLine($"[自测] 前台窗口 = 0x{foreground.ToInt64():X}（目标 0x{editorHandle.ToInt64():X}），" +
                          $"进程 = {WindowProcess.GetProcessName(foreground)}");

        TypePinyin(sample);
        Thread.Sleep(400);

        var editedText = ReadEditorText(editorHandle);
        Console.WriteLine($"[自测] 记事本当前内容 = \"{editedText}\"" +
                          (string.IsNullOrEmpty(editedText)
                              ? "（为空说明按键没有进入输入法，可能前台或焦点被抢）"
                              : "（非空说明按键确实被输入法接收了）"));

        var handle = WaitForCandidateWindow(reader, TimeSpan.FromSeconds(2));
        if (handle == IntPtr.Zero)
        {
            Console.WriteLine("[自测] ✗ 未发现候选窗。可能原因：输入法处于英文模式、或该窗口不受 UIA 暴露。");
            DumpImeWindowCandidates(reader);
            DumpHostElementTree(reader);
            CancelComposition();
            SafeKill(notepad);
            return 4;
        }

        Console.WriteLine($"[自测] ✓ 发现候选窗 0x{handle.ToInt64():X}");
        var snapshot = reader.Read(handle);
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

        CancelComposition();
        Thread.Sleep(150);
        SafeKill(notepad);

        return snapshot.Items.Count > 0 ? 0 : 5;
    }

    private static void TypePinyin(string sample)
    {
        foreach (var ch in sample)
        {
            var virtualKey = char.ToUpperInvariant(ch);
            KeySender.SendVirtualKey(virtualKey);
            Thread.Sleep(12);
        }
    }

    private static IntPtr WaitForCandidateWindow(UiaCandidateWindowReader reader, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var handle = reader.FindCandidateWindow();
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
            foreach (var element in elements.Take(60))
            {
                Console.WriteLine($"    {element}");
            }
        }
    }

    private static string ReadEditorText(IntPtr hostWindow)
    {
        var edit = NativeMethods.FindWindowEx(hostWindow, IntPtr.Zero, "Edit", null);
        if (edit == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(2048);
        NativeMethods.SendMessage(edit, NativeMethods.WM_GETTEXT, new IntPtr(buffer.Capacity), buffer);
        return buffer.ToString();
    }

    private static void CancelComposition()
    {
        KeySender.SendVirtualKey(0x1B);
        Thread.Sleep(80);
    }

    private static Process? StartNotepad()
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

    private static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
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

    private static void SafeKill(Process process)
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

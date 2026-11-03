using System.Collections.Concurrent;
using System.Text;
using BetterPinyin.Contracts;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Input;
using BetterPinyin.Interop.Uia;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Tools.ImeProbe;

/// <summary>
/// 微软拼音环境诊断探针。
/// </summary>
/// <remarks>
/// 这是 M0 阶段的验证工具，用来回答三个决定架构的问题：
/// <list type="number">
/// <item>本机能否通过 UIA 看到微软拼音的候选窗？</item>
/// <item>候选窗的矩形与候选词文本能否正确读出？</item>
/// <item>键盘钩子重建的拼音串是否与用户实际输入一致？</item>
/// </list>
/// 这三个问题任一为否，整个「浮窗 + 专属键」方案就要重新评估。
/// </remarks>
internal static class Program
{
    private static readonly ConcurrentQueue<string> LogQueue = new();
    private static LowLevelKeyboardHook? _hook;
    private static CompositionTracker? _tracker;
    private static UiaWorker? _uiaWorker;
    private static UiaCandidateWindowReader? _reader;

    private static IntPtr _lastCandidateHandle;
    private static string _lastSnapshotSignature = string.Empty;
    private static bool _dumpRequested;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Title = "Better Microsoft Pinyin · IME 诊断探针";

        PrintHeader();

        _uiaWorker = new UiaWorker();
        _reader = new UiaCandidateWindowReader(_uiaWorker);
        var uiaReady = _reader.Initialize();
        Console.WriteLine(uiaReady
            ? "[UIA] 初始化成功"
            : "[UIA] 初始化失败，后续将无法读取候选窗");

        if (args.Any(a => a.Equals("--activate-app", StringComparison.OrdinalIgnoreCase)))
        {
            var process = System.Diagnostics.Process
                .GetProcessesByName("BetterMicrosoftPinyin")
                .FirstOrDefault();
            if (process is null || process.MainWindowHandle == IntPtr.Zero)
            {
                Console.WriteLine("[置前] 未找到运行中的设置窗口");
                return 1;
            }

            var ok = NativeMethods.ForceForegroundWindow(process.MainWindowHandle);

            var moveArg = args.FirstOrDefault(a => a.StartsWith("--move-app=", StringComparison.OrdinalIgnoreCase));
            if (moveArg is not null && TryParseRect(moveArg["--move-app=".Length..], out var mx, out var my, out var mw, out var mh))
            {
                NativeMethods.SetWindowPos(
                    process.MainWindowHandle,
                    IntPtr.Zero,
                    mx,
                    my,
                    mw,
                    mh,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }

            Console.WriteLine(ok ? "[置前] 已切到前台" : "[置前] 系统拒绝置前");

            // 按键脚本：逗号分隔，12 表示按下并抬起，d12/u12 表示只按下/只抬起（十六进制）。
            // 用来验证下拉框、开关这类真实交互——模板写坏时界面看不出问题，键盘一按就露馅。
            var keysArg = args.FirstOrDefault(a => a.StartsWith("--send-keys=", StringComparison.OrdinalIgnoreCase));
            if (keysArg is not null)
            {
                Thread.Sleep(300);

                // 安全闸：前置窗口不是设置窗口就绝不发按键，否则会把 Tab/F4 打到用户正在用的程序里。
                var foreground = NativeMethods.GetForegroundWindow();
                if (foreground != process.MainWindowHandle)
                {
                    Console.WriteLine(
                        $"[按键] 前台是 0x{foreground.ToInt64():X}（{WindowProcess.GetProcessName(foreground)}），" +
                        "不是设置窗口，已取消发送");
                    return 4;
                }

                var sent = 0;
                foreach (var token in keysArg["--send-keys=".Length..]
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var mode = token[0];
                    var body = token;
                    var down = false;
                    var up = false;
                    if (mode is 'd' or 'D')
                    {
                        down = true;
                        body = token[1..];
                    }
                    else if (mode is 'u' or 'U')
                    {
                        up = true;
                        body = token[1..];
                    }

                    if (!ushort.TryParse(body, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var virtualKey))
                    {
                        continue;
                    }

                    if (down)
                    {
                        KeySender.SendKeyState(virtualKey, true, markAsOwn: false);
                    }
                    else if (up)
                    {
                        KeySender.SendKeyState(virtualKey, false, markAsOwn: false);
                    }
                    else
                    {
                        KeySender.SendVirtualKey(virtualKey, markAsOwn: false);
                    }

                    sent++;
                    Thread.Sleep(110);
                }

                Console.WriteLine($"[按键] 已发送 {sent} 个");
            }

            return ok ? 0 : 2;
        }

        if (args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("--hit-test", StringComparison.OrdinalIgnoreCase)))
        {
            var isHitTest = args.Any(a => a.Equals("--hit-test", StringComparison.OrdinalIgnoreCase));
            var sample = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "yisitalu";
            var hold = 0;
            var holdArg = args.FirstOrDefault(a => a.StartsWith("--hold=", StringComparison.OrdinalIgnoreCase));
            if (holdArg is not null && int.TryParse(holdArg["--hold=".Length..], out var parsed))
            {
                hold = parsed;
            }

            var shotArg = args.FirstOrDefault(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase));
            string? shotPath = shotArg is null ? null : shotArg["--shot=".Length..];

            var accept = 0;
            var acceptArg = args.FirstOrDefault(a => a.StartsWith("--accept=", StringComparison.OrdinalIgnoreCase));
            if (acceptArg is not null)
            {
                var text = acceptArg["--accept=".Length..];
                accept = text.Equals("oem3", StringComparison.OrdinalIgnoreCase)
                    ? 0xC0
                    : text.Length > 0 ? text[0] : 0;
            }

            var english = args.Any(a => a.Equals("--english", StringComparison.OrdinalIgnoreCase));

            var exitCode = isHitTest
                ? HitTest.Run(_reader, sample, shotPath)
                : SelfTest.Run(_uiaWorker, _reader, sample, hold, shotPath, accept, english);

            _uiaWorker.Dispose();
            return exitCode;
        }

        if (args.Any(a => a.Equals("--shot-only", StringComparison.OrdinalIgnoreCase)))
        {
            var target = args
                .FirstOrDefault(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase))
                ?["--shot=".Length..] ?? "shot.bmp";

            var scaleArg = args.FirstOrDefault(a => a.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase));
            var scale = scaleArg is not null && int.TryParse(scaleArg["--scale=".Length..], out var parsed) ? Math.Max(1, parsed) : 1;

            var rectArg = args.FirstOrDefault(a => a.StartsWith("--rect=", StringComparison.OrdinalIgnoreCase));
            bool ok;
            int w;
            int h;

            if (rectArg is not null &&
                TryParseRect(rectArg["--rect=".Length..], out var rx, out var ry, out var rw, out var rh))
            {
                ok = ScreenGrabber.Capture(target, rx, ry, rw, rh, scale, out w, out h);
            }
            else
            {
                ok = ScreenGrabber.CaptureVirtualScreen(target, out w, out h, scale);
            }

            if (ok)
            {
                Console.WriteLine($"[截图] {target}（{w}x{h}）");
                return 0;
            }

            Console.WriteLine("[截图] 失败");
            return 1;
        }

        _tracker = new CompositionTracker();
        _tracker.CompositionChanged += (_, e) => LogQueue.Enqueue($"[拼音] {e.RawInput}");
        _tracker.CompositionEnded += (_, e) => LogQueue.Enqueue($"[组字结束] 原因={e.Reason}");

        _hook = new LowLevelKeyboardHook();
        _hook.KeyEvent += OnKeyEvent;
        try
        {
            _hook.Start();
            Console.WriteLine("[钩子] 低级键盘钩子已安装");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[钩子] 安装失败：{ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("现在请切换到任意输入框，用微软拼音输入几个词（例如 yisitalu）。");
        Console.WriteLine("快捷键：D = 导出当前候选窗的完整 UIA 元素树，W = 重新扫描输入法窗口，Q = 退出");
        Console.WriteLine(new string('-', 78));

        using var pollTimer = new Timer(PollCallback, null, 300, 100);

        RunMessageLoop();

        _hook.Dispose();
        _uiaWorker.Dispose();
        return 0;
    }

    private static void OnKeyEvent(object? sender, KeyboardHookEventArgs e)
    {
        if (!e.IsKeyDown)
        {
            return;
        }

        // 探针自身的热键
        if (e.VirtualKey is 'D' or 'd' && NativeMethods.IsCtrlDown)
        {
            _dumpRequested = true;
            return;
        }

        if (e.VirtualKey is 'W' or 'w' && NativeMethods.IsCtrlDown)
        {
            DumpImeWindows();
            return;
        }

        if (e.VirtualKey is 'Q' or 'q' && NativeMethods.IsCtrlDown)
        {
            NativeMethods.PostQuitMessage(0);
            return;
        }

        var modifiers = ModifierKeys.None;
        if ((NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0)
        {
            modifiers |= ModifierKeys.Shift;
        }

        if (NativeMethods.IsCtrlDown)
        {
            modifiers |= ModifierKeys.Control;
        }

        if (NativeMethods.IsAltDown)
        {
            modifiers |= ModifierKeys.Alt;
        }

        if (NativeMethods.IsWinDown)
        {
            modifiers |= ModifierKeys.Windows;
        }

        _tracker?.Feed(e.VirtualKey, e.IsKeyDown, modifiers, e.IsOwnInjection);
    }

    private static void PollCallback(object? state)
    {
        while (LogQueue.TryDequeue(out var line))
        {
            Console.WriteLine(line);
        }

        if (_reader is null || !_reader.IsAvailable)
        {
            return;
        }

        try
        {
            var anchor = CaretLocator.GetAnchor();
            var handle = _reader.LocateNear(anchor);
            if (handle == IntPtr.Zero)
            {
                if (_lastCandidateHandle != IntPtr.Zero)
                {
                    Console.WriteLine("[候选窗] 已消失");
                    _lastCandidateHandle = IntPtr.Zero;
                    _lastSnapshotSignature = string.Empty;
                }

                return;
            }

            if (handle != _lastCandidateHandle)
            {
                _lastCandidateHandle = handle;
                Console.WriteLine($"[候选窗] 发现句柄 0x{handle.ToInt64():X} " +
                                  $"锚点=({anchor.X},{anchor.Y})/{anchor.Source} " +
                                  $"进程={WindowProcess.GetProcessName(handle)} " +
                                  $"类名={NativeMethods.GetWindowClassName(handle)}");
            }

            var snapshot = _reader.Read(handle);
            if (!snapshot.IsVisible)
            {
                return;
            }

            var signature = string.Join('|', snapshot.Items.Select(i => i.Text));
            if (signature == _lastSnapshotSignature && !_dumpRequested)
            {
                return;
            }

            _lastSnapshotSignature = signature;
            Console.WriteLine($"[候选窗] rect=({snapshot.Left},{snapshot.Top}) {snapshot.Width}x{snapshot.Height} " +
                              $"候选数={snapshot.Items.Count}");
            for (var i = 0; i < snapshot.Items.Count; i++)
            {
                var item = snapshot.Items[i];
                Console.WriteLine($"    {i + 1}. {item.Text}  @({item.Left},{item.Top}) {item.Width}x{item.Height}");
            }

            if (_dumpRequested)
            {
                _dumpRequested = false;
                var elements = _reader.ReadAllElements(handle);
                Console.WriteLine($"[UIA 树] 共 {elements.Count} 个元素：");
                foreach (var element in elements)
                {
                    Console.WriteLine($"    {element}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[候选窗] 读取异常：{ex.Message}");
        }
    }

    private static void DumpImeWindows()
    {
        if (_reader is null)
        {
            return;
        }

        var windows = _reader.DiscoverImeWindows();
        Console.WriteLine($"[窗口扫描] 找到 {windows.Count} 个输入法相关窗口：");
        foreach (var (handle, process, className) in windows)
        {
            NativeMethods.GetWindowRect(handle, out var rect);
            Console.WriteLine($"    0x{handle.ToInt64():X}  {process,-18} {className,-30} " +
                              $"({rect.Left},{rect.Top}) {rect.Width}x{rect.Height}");
        }
    }

    private static void PrintHeader()
    {
        Console.WriteLine("══════════════════════════════════════════════════════════════════");
        Console.WriteLine("  Better Microsoft Pinyin · IME 诊断探针 (M0)");
        Console.WriteLine("══════════════════════════════════════════════════════════════════");

        var language = MsPinyinDetector.GetForegroundLanguageId();
        Console.WriteLine($"[系统] 前台键盘布局语言 = {MsPinyinDetector.DescribeLanguage(language)}" +
                          $"（0x0804 表示简体中文）");
        Console.WriteLine($"[系统] 微软拼音已安装 = {MsPinyinDetector.IsMsPinyinInstalled()}");

        var settings = MsPinyinDetector.ReadSettingsSnapshot();
        if (settings.Count > 0)
        {
            Console.WriteLine($"[设置] 微软拼音设置项 {settings.Count} 条：");
            foreach (var (name, value) in settings.OrderBy(kv => kv.Key))
            {
                Console.WriteLine($"    {name} = {value}");
            }

            Console.WriteLine($"[设置] 模糊音 = {MsPinyinDetector.IsFuzzyInputEnabled(settings)}，" +
                              $"双拼 = {MsPinyinDetector.IsDoublePinyinEnabled(settings)}");
        }
        else
        {
            Console.WriteLine("[设置] 未读到微软拼音设置（可能未安装或注册表不可访问）");
        }
    }

    private static bool TryParseRect(string text, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        var parts = text.Split(',');
        return parts.Length == 4
               && int.TryParse(parts[0], out x)
               && int.TryParse(parts[1], out y)
               && int.TryParse(parts[2], out width)
               && int.TryParse(parts[3], out height)
               && width > 0
               && height > 0;
    }

    private static void RunMessageLoop()
    {
        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }
    }
}

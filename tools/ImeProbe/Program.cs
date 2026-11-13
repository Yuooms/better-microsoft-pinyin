using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using BetterPinyin.Contracts;
using BetterPinyin.Core.Wordbook;
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

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, MouseInputUnion[] pInputs, int cbSize);

    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct MouseInputUnion
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public MouseInput Mouse;
    }

    private static MouseInputUnion CreateMouseMove(int dx) => new()
    {
        Type = InputMouse,
        Mouse = new MouseInput { Dx = dx, Flags = MouseEventMove },
    };

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

        if (args.Any(a => a.Equals("--nudge-mouse", StringComparison.OrdinalIgnoreCase)))
        {
            // SetCursorPos 只改坐标，不算"用户输入"，唤不醒关掉的显示器。
            // 这里走 SendInput 的相对移动：位置回到原处，但系统会认为有人在动鼠标。
            var ok = SendInput(2, new[] { CreateMouseMove(1), CreateMouseMove(-1) }, Marshal.SizeOf<MouseInputUnion>());
            Console.WriteLine(ok == 2 ? "[鼠标] 已发送一次真实移动（用于唤醒显示）" : "[鼠标] ✗ 发送失败");
            return ok == 2 ? 0 : 1;
        }

        var mouseArg = args.FirstOrDefault(a =>
            a.StartsWith("--move-mouse=", StringComparison.OrdinalIgnoreCase));
        if (mouseArg is not null)
        {
            // 悬停状态没法靠截图命令造出来，只能真的把指针移过去。
            // 配合 --activate-app 与 --shot-window 就能验证 ToolTip 与 hover 动画。
            var parts = mouseArg["--move-mouse=".Length..].Split(',');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var mouseX) ||
                !int.TryParse(parts[1], out var mouseY))
            {
                Console.WriteLine("[鼠标] ✗ 坐标格式应为 --move-mouse=横,纵");
                return 1;
            }

            var moved = SetCursorPos(mouseX, mouseY);
            Console.WriteLine(moved
                ? $"[鼠标] 已移动到 ({mouseX},{mouseY})"
                : $"[鼠标] ✗ 移动失败，错误码 {Marshal.GetLastWin32Error()}");
            return moved ? 0 : 1;
        }

        var hotkeyArg = args.FirstOrDefault(a =>
            a.StartsWith("--hotkey=", StringComparison.OrdinalIgnoreCase));
        if (hotkeyArg is not null)
        {
            // 按键总是发给前台窗口，所以先把目标切上来；切不过去就一个键都不发。
            var focusArg = args.FirstOrDefault(a =>
                a.StartsWith("--focus=", StringComparison.OrdinalIgnoreCase));
            if (focusArg is not null && TargetWindow.Activate(focusArg["--focus=".Length..]) == IntPtr.Zero)
            {
                return 5;
            }

            Thread.Sleep(300);
            return SendHotkey(hotkeyArg["--hotkey=".Length..]);
        }

        if (args.Any(a => a.Equals("--activate-app", StringComparison.OrdinalIgnoreCase)))
        {            var process = System.Diagnostics.Process
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

        if (args.Any(a => a.Equals("--ime", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.StartsWith("--ime=", StringComparison.OrdinalIgnoreCase)))
        {
            return HandleImeCommands(args, _uiaWorker);
        }

        if (args.Any(a => a.Equals("--wordbook", StringComparison.OrdinalIgnoreCase)))
        {
            return DumpWordbooks(_uiaWorker);
        }

        if (args.Any(a => a.StartsWith("--layout", StringComparison.OrdinalIgnoreCase)))
        {
            return HandleLayoutCommands(args, _uiaWorker);
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

            // 用已存在的窗口做测试（Windows 11 的记事本是 UWP，启动进程拿不到窗口）。
            var targetArg = args.FirstOrDefault(a =>
                a.StartsWith("--target=", StringComparison.OrdinalIgnoreCase));
            var targetName = targetArg?["--target=".Length..];

            var exitCode = isHitTest
                ? HitTest.Run(_reader, sample, shotPath, targetName)
                : SelfTest.Run(_uiaWorker, _reader, sample, hold, shotPath, accept, english, targetName);

            _uiaWorker.Dispose();
            return exitCode;
        }

        if (args.Any(a => a.Equals("--reload-ime", StringComparison.OrdinalIgnoreCase)))
        {
            // 验证「切一次键盘布局就能让输入法重读自学习词库」这个结论，也方便改完文件手工复验。
            // 默认借用当前前台窗口（探针自己没有可用的输入法上下文），加 --self 则只切本线程。
            var target = args.Any(a => a.Equals("--self", StringComparison.OrdinalIgnoreCase))
                ? IntPtr.Zero
                : NativeMethods.GetForegroundWindow();
            var reloaded = ImeReloader.TryReload(target, out var reloadMessage);
            Console.WriteLine($"[重载] 目标窗口 = 0x{target.ToInt64():X}");
            Console.WriteLine($"[重载] {(reloaded ? "✓" : "✗")} {reloadMessage}");
            _uiaWorker.Dispose();
            return reloaded ? 0 : 1;
        }

        if (args.Any(a => a.StartsWith("--shot-window=", StringComparison.OrdinalIgnoreCase)))
        {
            return CaptureWindowShot(args, _uiaWorker);
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
        _tracker.CompositionChanged += (_, e) => LogQueue.Enqueue($"[拼音] {e.RawInput}");        _tracker.CompositionEnded += (_, e) => LogQueue.Enqueue($"[组字结束] 原因={e.Reason}");

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

        // 同一语言下可能装了多个输入法（例如搜狗 + 微软拼音），布局 Id 一模一样，
        // 只有 TSF 的生效配置能区分。测出来的结论属于谁，全看这一行。
        var activeIme = ImeProfile.GetActive();
        Console.WriteLine($"[系统] 当前生效输入法 = {activeIme?.Description ?? "(未知)"}");

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

    /// <summary>
    /// 只截某个窗口本身，不截屏幕。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="worker">UIA 线程，仅在退出时释放。</param>
    /// <returns>进程退出码。</returns>
    /// <remarks>
    /// 与 <c>--shot-only</c> 的区别：那个走屏幕 DC，窗口被遮挡或不在最前时抓到的是别人的内容；
    /// 这里让窗口自己渲染，前后台都不影响，也**不会把窗口切到前台**（截图不该有副作用）。
    /// </remarks>
    private static int CaptureWindowShot(string[] args, UiaWorker worker)
    {
        var name = args
            .First(a => a.StartsWith("--shot-window=", StringComparison.OrdinalIgnoreCase))
            ["--shot-window=".Length..];

        var shotArg = args.FirstOrDefault(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase));
        var path = shotArg is null ? "window.bmp" : shotArg["--shot=".Length..];

        var scaleArg = args.FirstOrDefault(a => a.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase));
        var scale = scaleArg is not null && int.TryParse(scaleArg["--scale=".Length..], out var parsed)
            ? Math.Max(1, parsed)
            : 1;

        var handle = TargetWindow.Find(name);
        if (handle == IntPtr.Zero)
        {
            handle = TargetWindow.FindByTitle(name);
        }

        if (handle == IntPtr.Zero)
        {
            Console.WriteLine($"[截图] ✗ 没找到匹配「{name}」的窗口");
            worker.Dispose();
            return 1;
        }

        NativeMethods.GetWindowRect(handle, out var rect);
        var state = TargetWindow.IsMinimized(handle) ? "已最小化" : "正常";
        Console.WriteLine($"[截图] 目标 0x{handle.ToInt64():X} " +
                          $"{WindowProcess.GetProcessName(handle)} " +
                          $"({rect.Left},{rect.Top}) {rect.Width}x{rect.Height} {state}");

        if (!ScreenGrabber.CaptureWindow(handle, path, scale, out var width, out var height))
        {
            Console.WriteLine("[截图] ✗ 窗口拒绝渲染（最小化、受保护内容或纯分层窗口），" +
                              "可改用 --shot-only 从屏幕抓");
            worker.Dispose();
            return 1;
        }

        Console.WriteLine($"[截图] {path}（{width}x{height}）");
        worker.Dispose();
        return 0;
    }

    /// <summary>
    /// 直接读本机微软拼音的自定义短语与自学习词库，确认文件路径与格式对得上。
    /// </summary>
    /// <param name="worker">UIA 线程，仅在退出时释放。</param>
    /// <returns>进程退出码：两个文件都读成功返回 0。</returns>
    /// <remarks>
    /// 这两份文件的路径与二进制格式都是微软的内部约定，跨 Windows 版本可能变化。
    /// 用程序自己的解析器去读真实文件，比在设置界面里数行数更可靠：
    /// 解析器坏掉时，界面只会显示空列表，看不出是「没数据」还是「读不懂」。
    /// </remarks>
    private static int DumpWordbooks(UiaWorker worker)
    {
        var allOk = true;

        var phrases = new MspyPhraseStore();
        Console.WriteLine($"[短语] {phrases.FilePath}");
        Console.WriteLine($"[短语] 文件存在 = {phrases.Exists}");
        var phraseList = phrases.Read(out var phraseError);
        Console.WriteLine(phraseError is null
            ? $"[短语] ✓ 读到 {phraseList.Count} 条"
            : $"[短语] ✗ {phraseError}");
        allOk &= phraseError is null;

        foreach (var phrase in phraseList.Take(10))
        {
            Console.WriteLine($"    {phrase.Pinyin} → {phrase.Text}（位置 {phrase.Position}）");
        }

        var learning = new MspyLearningStore();
        Console.WriteLine($"[自学习] {learning.FilePath}");
        Console.WriteLine($"[自学习] 文件存在 = {learning.Exists}");

        if (learning.Exists)
        {
            var read = learning.TryRead(out var words, out var learningError);
            Console.WriteLine(read
                ? $"[自学习] ✓ 读到 {words.Count} 条"
                : $"[自学习] ✗ {learningError}");
            allOk &= read;

            foreach (var word in words.Take(10))
            {
                Console.WriteLine($"    {word.Text}  简拼={word.Jianpin}  长度={word.Length}");
            }
        }
        else
        {
            Console.WriteLine("[自学习] 还没有自学习词库（输入法尚未学习过任何词）");
        }

        worker.Dispose();
        return allOk ? 0 : 1;
    }

    /// <summary>
    /// 列出本机键盘布局，或把目标线程切到指定布局。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="worker">UIA 线程，仅在退出时释放。</param>
    /// <returns>进程退出码。</returns>
    /// <remarks>
    /// 键盘布局是每线程的：控制台处于中文布局，不代表要测试的那个窗口也是。
    /// 打字前先把目标线程钉在简体中文上，才能排除「其实在英文键盘上打字」这类假阴性。
    /// </remarks>
    private static int HandleLayoutCommands(string[] args, UiaWorker worker)
    {
        var count = NativeMethods.GetKeyboardLayoutList(0, null);
        var buffer = new IntPtr[Math.Max(count, 0)];
        var written = count > 0 ? NativeMethods.GetKeyboardLayoutList(buffer.Length, buffer) : 0;

        Console.WriteLine($"[布局] 本机共 {written} 个键盘布局：");
        for (var i = 0; i < written; i++)
        {
            var hkl = buffer[i];
            var languageId = (ushort)(hkl.ToInt64() & 0xFFFF);
            Console.WriteLine($"    HKL=0x{hkl.ToInt64():X8}  KLID=0000{languageId:X4}  " +
                              $"语言=0x{languageId:X4}");
        }

        var applyArg = args.FirstOrDefault(a =>
            a.StartsWith("--layout=apply:", StringComparison.OrdinalIgnoreCase));
        if (applyArg is null)
        {
            worker.Dispose();
            return 0;
        }

        // 接受 "00000804" 这种标准 KLID，也接受 "08040804" 这种直接抄 HKL 的写法：
        // KLID 的后四位是语言 Id，前四位对常规布局恒为 0000。
        var raw = applyArg["--layout=apply:".Length..].Trim();
        if (raw.Length < 4)
        {
            Console.WriteLine($"[布局] ✗ KLID 至少要有 4 位十六进制，收到「{raw}」");
            worker.Dispose();
            return 1;
        }

        var klid = "0000" + raw[^4..];

        var targetArg = args.FirstOrDefault(a =>
            a.StartsWith("--target=", StringComparison.OrdinalIgnoreCase));
        var handle = targetArg is null
            ? NativeMethods.GetForegroundWindow()
            : TargetWindow.Find(targetArg["--target=".Length..]);

        if (handle == IntPtr.Zero)
        {
            Console.WriteLine("[布局] ✗ 没有可用目标窗口");
            worker.Dispose();
            return 1;
        }

        var loaded = NativeMethods.LoadKeyboardLayout(klid, 0x00000001);
        if (loaded == IntPtr.Zero)
        {
            Console.WriteLine($"[布局] ✗ LoadKeyboardLayout({klid}) 失败");
            worker.Dispose();
            return 1;
        }

        NativeMethods.PostMessage(handle, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, loaded);
        Thread.Sleep(400);

        var threadId = NativeMethods.GetWindowThreadProcessId(handle, out _);
        var current = threadId == 0 ? IntPtr.Zero : NativeMethods.GetKeyboardLayout(threadId);
        var ok = current == loaded;
        Console.WriteLine(ok
            ? $"[布局] ✓ 目标线程已切到 HKL=0x{current.ToInt64():X8}"
            : $"[布局] ✗ 切换未生效，目标线程仍是 HKL=0x{current.ToInt64():X8}");

        worker.Dispose();
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 列出简中下注册的输入法、标出当前生效的那个，并可切换到指定输入法。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="worker">UIA 线程，仅在退出时释放。</param>
    /// <returns>进程退出码。</returns>
    private static int HandleImeCommands(string[] args, UiaWorker worker)
    {
        var installed = ImeProfile.EnumerateInstalled();
        var active = ImeProfile.GetActive();

        Console.WriteLine($"[输入法] 简体中文下注册 {installed.Count} 个：");
        foreach (var entry in installed)
        {
            var mark = active is not null && active.Value.ProfileId == entry.ProfileId ? "  ← 当前生效" : string.Empty;
            Console.WriteLine($"    {entry.Description,-26} {entry.Clsid}{mark}");
        }

        var useArg = args.FirstOrDefault(a =>
            a.StartsWith("--ime=use:", StringComparison.OrdinalIgnoreCase));
        if (useArg is null)
        {
            worker.Dispose();
            return 0;
        }

        var keyword = useArg["--ime=use:".Length..];
        var target = ImeProfile.FindByDescription(keyword);
        if (target is null)
        {
            Console.WriteLine($"[输入法] ✗ 没有找到描述包含「{keyword}」的输入法");
            worker.Dispose();
            return 1;
        }

        Console.WriteLine($"[输入法] 切换到 {target.Value.Description} …");
        if (!ImeProfile.Activate(target.Value))
        {
            worker.Dispose();
            return 1;
        }

        Thread.Sleep(400);

        var after = ImeProfile.GetActive();
        if (after is null)
        {
            Console.WriteLine("[输入法] ✗ 切换后查不到生效的输入法");
            worker.Dispose();
            return 1;
        }

        var ok = after.Value.ProfileId == target.Value.ProfileId;
        Console.WriteLine(ok
            ? $"[输入法] ✓ 当前生效 = {after.Value.Description}"
            : $"[输入法] ✗ 切换未生效，当前仍是 {after.Value.Description}");

        worker.Dispose();
        return ok ? 0 : 1;
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

    /// <summary>
    /// 按住若干虚拟键再逆序释放，用于模拟 Win+Space 这类组合键。
    /// </summary>
    /// <param name="spec">逗号分隔的十六进制虚拟键码，例如 <c>5B,20</c>。</param>
    /// <returns>进程退出码。</returns>
    /// <remarks>
    /// 单独存在的原因是：需要「切换输入法 / 让输入法重新加载」时，
    /// 用 SendInput 发组合键比手工按键盘更适合放进可重复执行的脚本。
    /// </remarks>
    private static int SendHotkey(string spec)
    {
        var keys = new List<ushort>(4);
        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ushort.TryParse(token, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var virtualKey))
            {
                Console.WriteLine($"[组合键] 无法解析：{token}");
                return 2;
            }

            keys.Add(virtualKey);
        }

        if (keys.Count == 0)
        {
            Console.WriteLine("[组合键] 没有按键");
            return 2;
        }

        // 必须按「真实按键」注入（不带 InjectionMarker）。
        // 带标记的注入会被被测程序识别成自己人发的，直接忽略，测出来全是假阴性。
        foreach (var key in keys)
        {
            KeySender.SendKeyState(key, down: true, markAsOwn: false);
            Thread.Sleep(30);
        }

        for (var i = keys.Count - 1; i >= 0; i--)
        {
            KeySender.SendKeyState(keys[i], down: false, markAsOwn: false);
            Thread.Sleep(30);
        }

        Console.WriteLine($"[组合键] 已发送 {string.Join("+", keys.Select(k => k.ToString("X2")))}");
        return 0;
    }
}

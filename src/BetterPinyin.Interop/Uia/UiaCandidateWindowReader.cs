using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterPinyin.Contracts;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Interop.Uia;

/// <summary>
/// UIA 元素的原始信息，仅用于诊断探针输出。
/// </summary>
/// <param name="Name">元素名称。</param>
/// <param name="AutomationId">自动化 Id。</param>
/// <param name="ControlType">控件类型 Id（50007 为 ListItem）。</param>
/// <param name="Bounds">屏幕包围盒。</param>
/// <param name="IsOffscreen">是否在屏幕外。</param>
public readonly record struct UiaElementInfo(
    string Name,
    string AutomationId,
    int ControlType,
    NativeRect Bounds,
    bool IsOffscreen)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"type={ControlType} offscreen={(IsOffscreen ? 1 : 0)} " +
        $"rect=({Bounds.Left},{Bounds.Top},{Bounds.Width}x{Bounds.Height}) " +
        $"id=\"{AutomationId}\" name=\"{Name}\"";
}

/// <summary>
/// 读取微软拼音的候选窗内容与位置。
/// </summary>
/// <remarks>
/// <para>发现候选窗的正确姿势（实测结论）：</para>
/// <list type="number">
/// <item>候选条位于沉浸式输入法的独立 window band，<c>EnumWindows</c> 一类枚举 API <b>拿不到它</b>。</item>
/// <item>按进程名能枚举到的那个 <c>TextInputHost</c> 窗口是输入宿主的全屏站点窗口
/// （1920x1080 且被 DWM 隐身），它的 UIA 子树为空——误把它当候选窗，就会得出"UIA 读不到"的错误结论。</item>
/// <item>真正的候选窗只能按坐标命中：在插入符附近调用 <c>WindowFromPoint</c>，
/// 得到的是一个"小矩形、类名 CoreWindow、未被隐身"的窗口。</item>
/// </list>
/// <para>结构（实测）：容器是 <c>Pane</c>（type=50033），AutomationId 为
/// <c>IME_Prediction_Window</c> 或 <c>IME_Candidate_Window</c>；候选项是 <c>ListItem</c>（type=50007），
/// 其 <c>Name</c> 即候选词文本；每个候选项下还有
/// <c>TEMPLATE_PART_CandidateItemIndex</c>（序号）与 <c>TEMPLATE_PART_CandidateItemText</c>（文本）两个 Text 子元素。</para>
/// </remarks>
public sealed class UiaCandidateWindowReader
{
    /// <summary>UIA_ListItemControlTypeId。</summary>
    public const int ListItemControlTypeId = 50007;

    /// <summary>转换候选列表的约定 AutomationId。</summary>
    public const string CandidateWindowAutomationId = "IME_Candidate_Window";

    /// <summary>预测候选列表的约定 AutomationId。</summary>
    public const string PredictionWindowAutomationId = "IME_Prediction_Window";

    private const string CoreWindowClassName = "Windows.UI.Core.CoreWindow";

    private static readonly string[] ImeProcessNames =
    {
        "TextInputHost",
        "ChsIME",
        "InputPersonalization",
    };

    /// <summary>
    /// 插入符附近的探测偏移。候选条一般在光标下方，也可能因空间不足翻到上方，
    /// 因此纵向正负都探；横向向右展开以覆盖较长的候选条。
    /// </summary>
    private static readonly (int Dx, int Dy)[] ProbeOffsets =
    {
        (0, 2), (0, 8), (0, 18), (0, 28), (0, 40),
        (24, 8), (60, 18), (120, 18), (180, 18),
        (-24, 18),
        (0, -22), (0, -34),
    };

    private readonly UiaWorker _worker;
    private readonly Dictionary<uint, string> _processNameCache = new();
    private readonly object _cacheLock = new();

    private IUIAutomation? _automation;
    private IUIAutomationCondition? _trueCondition;

    /// <summary>创建读取器。</summary>
    /// <param name="worker">UIA 专用 STA 线程。</param>
    public UiaCandidateWindowReader(UiaWorker worker)
    {
        _worker = worker;
    }

    /// <summary>最近一次 UIA 读取的诊断信息。</summary>
    public static string LastDiagnostic { get; private set; } = "(尚未读取)";

    /// <summary>UIA 是否已成功初始化。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>初始化 UIA（必须在 STA 线程上执行）。</summary>
    /// <returns>是否成功。</returns>
    public bool Initialize()
    {
        return _worker.Invoke(
            () =>
            {
                try
                {
                    _automation = UiaFactory.Create();
                    var hr = _automation.CreateTrueCondition(out var condition);
                    if (hr != 0 || condition is null)
                    {
                        IsAvailable = false;
                        return false;
                    }

                    _trueCondition = condition;
                    IsAvailable = true;
                    return true;
                }
                catch (Exception ex)
                {
                    LastDiagnostic = $"初始化失败：{ex.GetType().Name} {ex.Message}";
                    IsAvailable = false;
                    return false;
                }
            },
            timeoutMilliseconds: 3000,
            fallback: false);
    }

    /// <summary>
    /// 读取当前焦点控件文本的最后若干个字符。
    /// </summary>
    /// <param name="count">要读取的字符数。</param>
    /// <returns>文本尾部；控件不支持 ValuePattern 或文本过短时返回 null。</returns>
    /// <remarks>
    /// 英文模式采纳时要先删掉已经上屏的字母，删之前必须确认这段文本确实是用户刚打的，
    /// 否则会删掉正文。ValuePattern 只读不写，比 TextPattern 的接口面小得多，
    /// 失败时一律返回 null，调用方据此放弃上屏。
    /// </remarks>
    public string? ReadFocusedValueTail(int count)
    {
        if (_automation is null || count <= 0)
        {
            return null;
        }

        var automation = _automation;
        return _worker.Invoke(
            () => ReadFocusedValueTailCore(automation, count),
            timeoutMilliseconds: 400,
            fallback: null);
    }

    private static string? ReadFocusedValueTailCore(IUIAutomation automation, int count)
    {
        IntPtr patternPointer = IntPtr.Zero;

        try
        {
            if (automation.GetFocusedElement(out var focused) != 0 || focused is null)
            {
                return null;
            }

            if (focused.GetCurrentPattern(UiaInterop.ValuePatternId, out patternPointer) != 0 ||
                patternPointer == IntPtr.Zero)
            {
                return null;
            }

            var pattern = (IUIAutomationValuePattern)Marshal.GetObjectForIUnknown(patternPointer);
            if (pattern.get_CurrentValue(out var value) != 0 || string.IsNullOrEmpty(value))
            {
                return null;
            }

            return value.Length >= count ? value[^count..] : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (patternPointer != IntPtr.Zero)
            {
                Marshal.Release(patternPointer);
            }
        }
    }

    /// <summary>
    /// 在锚点附近查找候选窗并读取内容，带重试（候选条可能比按键晚几十毫秒出现）。
    /// </summary>
    /// <param name="anchor">锚点（屏幕坐标）。</param>
    /// <param name="timeoutMilliseconds">总等待上限。</param>
    /// <returns>候选窗快照；找不到时返回 <see cref="CandidateWindowSnapshot.Empty"/>。</returns>
    public CandidateWindowSnapshot ReadBest(AnchorPoint anchor, int timeoutMilliseconds = 400)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        do
        {
            var handle = LocateNear(anchor);
            if (handle != IntPtr.Zero)
            {
                var snapshot = Read(handle);
                if (snapshot.IsVisible)
                {
                    return snapshot;
                }
            }

            Thread.Sleep(60);
        }
        while (Environment.TickCount64 < deadline);

        return CandidateWindowSnapshot.Empty;
    }

    /// <summary>在锚点附近按坐标命中候选窗。</summary>
    /// <param name="anchor">锚点（屏幕坐标）。</param>
    /// <returns>候选窗句柄；未命中返回 <see cref="IntPtr.Zero"/>。</returns>
    public IntPtr LocateNear(AnchorPoint anchor)
    {
        foreach (var (dx, dy) in ProbeOffsets)
        {
            var handle = WindowAt(anchor.X + dx, anchor.Y + dy);
            if (handle == IntPtr.Zero || !IsPlausibleCandidateWindow(handle))
            {
                continue;
            }

            // 类名与尺寸可伪造性低，但仍以"能读出候选"作为最终判据。
            var snapshot = Read(handle);
            if (snapshot.IsVisible)
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>读取指定窗口的候选窗快照。</summary>
    /// <param name="hwnd">候选窗句柄。</param>
    /// <returns>候选窗快照；读不到时返回 <see cref="CandidateWindowSnapshot.Empty"/>。</returns>
    public CandidateWindowSnapshot Read(IntPtr hwnd)
    {
        if (_automation is null || _trueCondition is null || hwnd == IntPtr.Zero)
        {
            return CandidateWindowSnapshot.Empty;
        }

        var automation = _automation;
        var condition = _trueCondition;

        return _worker.Invoke(
            () => ReadCore(automation, condition, hwnd),
            timeoutMilliseconds: 400,
            fallback: CandidateWindowSnapshot.Empty);
    }

    /// <summary>
    /// 读取指定窗口下的全部 UIA 元素（诊断用，数量可能较多）。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>元素信息列表，失败时返回空列表。</returns>
    public IReadOnlyList<UiaElementInfo> ReadAllElements(IntPtr hwnd)
    {
        if (_automation is null || _trueCondition is null || hwnd == IntPtr.Zero)
        {
            return Array.Empty<UiaElementInfo>();
        }

        var automation = _automation;
        var condition = _trueCondition;

        return _worker.Invoke(
            () => ReadAllElementsCore(automation, condition, hwnd),
            timeoutMilliseconds: 1500,
            fallback: (IReadOnlyList<UiaElementInfo>)Array.Empty<UiaElementInfo>());
    }

    /// <summary>
    /// 按进程名枚举输入法相关窗口。
    /// </summary>
    /// <remarks>
    /// 仅供诊断与对比使用：这里能枚举到的通常是输入宿主的全屏站点窗口，而不是候选条本身。
    /// 候选条必须用 <see cref="LocateNear"/> 按坐标命中。
    /// </remarks>
    /// <returns>窗口句柄、进程名与类名的列表。</returns>
    public IReadOnlyList<(IntPtr Handle, string ProcessName, string ClassName)> DiscoverImeWindows()
    {
        var result = new List<(IntPtr, string, string)>();

        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                var processName = GetProcessName(pid);
                if (processName is null || !IsImeProcess(processName))
                {
                    return true;
                }

                result.Add((hwnd, processName, NativeMethods.GetWindowClassName(hwnd)));
                return true;
            },
            IntPtr.Zero);

        return result;
    }

    /// <summary>在指定屏幕坐标处用 <c>WindowFromPoint</c> 探测窗口。</summary>
    /// <param name="x">屏幕 X 坐标。</param>
    /// <param name="y">屏幕 Y 坐标。</param>
    /// <returns>命中的窗口句柄，未命中返回 <see cref="IntPtr.Zero"/>。</returns>
    public static IntPtr WindowAt(int x, int y)
    {
        return NativeMethods.WindowFromPoint(new NativePoint { X = x, Y = y });
    }

    /// <summary>
    /// 判断一个窗口是否"可能是候选条"：类名是 CoreWindow、属于输入法进程、未被隐身、且不是全屏。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>可能是候选条返回 true。</returns>
    public static bool IsPlausibleCandidateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (NativeMethods.IsCloaked(hwnd))
        {
            return false;
        }

        if (!string.Equals(
                NativeMethods.GetWindowClassName(hwnd),
                CoreWindowClassName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsImeProcess(WindowProcess.GetProcessName(hwnd)))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        // 输入宿主的站点窗口是全屏的，必须排除，否则会读到空子树。
        return rect.Width < NativeMethods.ScreenWidth * 0.8
               && rect.Height < NativeMethods.ScreenHeight * 0.5;
    }

    private static bool IsImeProcess(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (var name in ImeProcessNames)
        {
            if (string.Equals(processName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCandidateContainer(UiaElementInfo element)
    {
        return string.Equals(element.AutomationId, CandidateWindowAutomationId, StringComparison.Ordinal)
               || string.Equals(element.AutomationId, PredictionWindowAutomationId, StringComparison.Ordinal);
    }

    private static bool Intersects(NativeRect a, NativeRect b)
    {
        return a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    }

    private static IReadOnlyList<UiaElementInfo> ReadAllElementsCore(
        IUIAutomation automation,
        IUIAutomationCondition condition,
        IntPtr hwnd)
    {
        var list = new List<UiaElementInfo>(48);

        try
        {
            var hr = automation.ElementFromHandle(hwnd, out var root);
            if (hr != 0 || root is null)
            {
                LastDiagnostic = "ElementFromHandle 失败 hr=0x" + hr.ToString("X8");
                return list;
            }

            hr = root.FindAll(TreeScope.Descendants, condition, out var found);
            if (hr != 0 || found is null)
            {
                LastDiagnostic = "FindAll 失败 hr=0x" + hr.ToString("X8");
                return list;
            }

            found.get_Length(out var length);
            LastDiagnostic = $"元素数={length}";

            for (var i = 0; i < length; i++)
            {
                if (found.GetElement(i, out var element) != 0 || element is null)
                {
                    continue;
                }

                element.get_CurrentAutomationId(out var automationId);
                element.get_CurrentName(out var name);
                element.get_CurrentControlType(out var controlType);
                element.get_CurrentIsOffscreen(out var offscreen);
                element.get_CurrentBoundingRectangle(out var bounds);

                list.Add(new UiaElementInfo(
                    name ?? string.Empty,
                    automationId ?? string.Empty,
                    controlType,
                    bounds,
                    offscreen != 0));
            }
        }
        catch (COMException ex)
        {
            LastDiagnostic = "COMException hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message;
        }
        catch (InvalidCastException ex)
        {
            LastDiagnostic = "InvalidCastException " + ex.Message;
        }

        return list;
    }

    private static CandidateWindowSnapshot ReadCore(
        IUIAutomation automation,
        IUIAutomationCondition condition,
        IntPtr hwnd)
    {
        var elements = ReadAllElementsCore(automation, condition, hwnd);
        if (elements.Count == 0)
        {
            return CandidateWindowSnapshot.Empty;
        }

        UiaElementInfo? container = null;
        foreach (var element in elements)
        {
            if (IsCandidateContainer(element) && element.Bounds.Width > 0 && element.Bounds.Height > 0)
            {
                container = element;
                break;
            }
        }

        var items = new List<CandidateItem>(16);
        foreach (var element in elements)
        {
            if (element.ControlType != ListItemControlTypeId || element.IsOffscreen)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(element.Name))
            {
                continue;
            }

            if (element.Bounds.Width <= 0 || element.Bounds.Height <= 0)
            {
                continue;
            }

            // 有容器时只收容器内的项，避免把同窗口里的其它列表算进来。
            if (container is not null && !Intersects(element.Bounds, container.Value.Bounds))
            {
                continue;
            }

            items.Add(new CandidateItem(
                element.Name,
                element.Bounds.Left,
                element.Bounds.Top,
                element.Bounds.Width,
                element.Bounds.Height));
        }

        if (items.Count == 0)
        {
            // 有容器但没读到候选项：仍然给出矩形供浮窗贴靠，但如实标注质量等级。
            return container is null
                ? CandidateWindowSnapshot.Empty
                : new CandidateWindowSnapshot(
                    true,
                    container.Value.Bounds.Left,
                    container.Value.Bounds.Top,
                    container.Value.Bounds.Width,
                    container.Value.Bounds.Height,
                    Array.Empty<CandidateItem>())
                {
                    Quality = CandidateReadQuality.GeometryOnly,
                };
        }

        items.Sort((a, b) =>
        {
            var byTop = a.Top.CompareTo(b.Top);
            return byTop != 0 ? byTop : a.Left.CompareTo(b.Left);
        });

        var left = container?.Bounds.Left ?? items.Min(i => i.Left);
        var top = container?.Bounds.Top ?? items.Min(i => i.Top);
        var right = container?.Bounds.Right ?? items.Max(i => i.Left + i.Width);
        var bottom = container?.Bounds.Bottom ?? items.Max(i => i.Top + i.Height);

        return new CandidateWindowSnapshot(
            IsVisible: true,
            Left: left,
            Top: top,
            Width: right - left,
            Height: bottom - top,
            Items: items)
        {
            Quality = CandidateReadQuality.Exact,
        };
    }

    private string? GetProcessName(uint pid)
    {
        lock (_cacheLock)
        {
            if (_processNameCache.TryGetValue(pid, out var cached))
            {
                return string.IsNullOrEmpty(cached) ? null : cached;
            }
        }

        string? name = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // 进程已退出。
        }
        catch (InvalidOperationException)
        {
            // 进程已退出。
        }

        lock (_cacheLock)
        {
            if (_processNameCache.Count > 512)
            {
                _processNameCache.Clear();
            }

            _processNameCache[pid] = name ?? string.Empty;
        }

        return name;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterPinyin.Contracts;
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
/// 从微软拼音的候选窗口中读取候选列表。
/// </summary>
/// <remarks>
/// 候选窗位于 Windows 的沉浸式 IME 图层（z-band），普通窗口枚举看不到，
/// 因此这里提供两条发现路径：
/// <list type="number">
/// <item>按进程名扫描顶层窗口（<c>TextInputHost</c> / <c>ChsIME</c>）。</item>
/// <item>在系统插入符附近用 <c>WindowFromPoint</c> 命中。</item>
/// </list>
/// 两条都失败时返回空快照，调用方降级到光标定位而不应报错。
/// </remarks>
public sealed class UiaCandidateWindowReader
{
    /// <summary>UIA_ListItemControlTypeId。</summary>
    public const int ListItemControlTypeId = 50007;

    private static readonly string[] ImeProcessNames =
    {
        "TextInputHost",
        "ChsIME",
        "InputPersonalization",
        "ctfmon",
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
                catch (Exception)
                {
                    IsAvailable = false;
                    return false;
                }
            },
            timeoutMilliseconds: 3000,
            fallback: false);
    }

    /// <summary>
    /// 扫描所有可能的输入法窗口。
    /// </summary>
    /// <returns>窗口句柄、进程名与类名的列表，用于诊断。</returns>
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
                if (processName is null)
                {
                    return true;
                }

                var matched = false;
                foreach (var candidate in ImeProcessNames)
                {
                    if (string.Equals(processName, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                }

                if (matched)
                {
                    result.Add((hwnd, processName, NativeMethods.GetWindowClassName(hwnd)));
                }

                return true;
            },
            IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// 在指定屏幕坐标处用 <c>WindowFromPoint</c> 探测窗口。
    /// </summary>
    /// <param name="x">屏幕 X 坐标。</param>
    /// <param name="y">屏幕 Y 坐标。</param>
    /// <returns>命中的窗口句柄，未命中返回 <see cref="IntPtr.Zero"/>。</returns>
    public static IntPtr WindowAt(int x, int y)
    {
        return NativeMethods.WindowFromPoint(new NativePoint { X = x, Y = y });
    }

    /// <summary>
    /// 读取指定窗口下的全部 UIA 元素（诊断用，数量可能较多）。
    /// </summary>
    /// <param name="hwnd">候选窗句柄。</param>
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
    /// 读取候选窗快照。
    /// </summary>
    /// <param name="hwnd">候选窗句柄；传 <see cref="IntPtr.Zero"/> 时自动发现。</param>
    /// <returns>候选窗快照，读不到时返回 <see cref="CandidateWindowSnapshot.Empty"/>。</returns>
    public CandidateWindowSnapshot Read(IntPtr hwnd = default)
    {
        if (_automation is null || _trueCondition is null)
        {
            return CandidateWindowSnapshot.Empty;
        }

        var target = hwnd == IntPtr.Zero ? FindCandidateWindow() : hwnd;
        if (target == IntPtr.Zero)
        {
            return CandidateWindowSnapshot.Empty;
        }

        var automation = _automation;
        var condition = _trueCondition;

        return _worker.Invoke(
            () => ReadCore(automation, condition, target),
            timeoutMilliseconds: 250,
            fallback: CandidateWindowSnapshot.Empty);
    }

    /// <summary>自动发现候选窗句柄。</summary>
    /// <returns>候选窗句柄，找不到返回 <see cref="IntPtr.Zero"/>。</returns>
    public IntPtr FindCandidateWindow()
    {
        foreach (var (handle, _, _) in DiscoverImeWindows())
        {
            var elements = ReadAllElements(handle);
            foreach (var element in elements)
            {
                if (string.Equals(element.AutomationId, "IME_Candidate_Window", StringComparison.Ordinal))
                {
                    return handle;
                }

                if (element.ControlType == ListItemControlTypeId && !element.IsOffscreen && !string.IsNullOrWhiteSpace(element.Name))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>把物理像素矩形换算成逻辑像素，便于 UI 层使用。</summary>
    /// <param name="rect">物理像素矩形。</param>
    /// <param name="dpi">该窗口所在显示器的 DPI。</param>
    /// <returns>逻辑像素矩形。</returns>
    public static NativeRect ToLogical(NativeRect rect, uint dpi)
    {
        if (dpi == 0 || dpi == 96)
        {
            return rect;
        }

        var scale = 96.0 / dpi;
        return new NativeRect
        {
            Left = (int)Math.Round(rect.Left * scale),
            Top = (int)Math.Round(rect.Top * scale),
            Right = (int)Math.Round(rect.Right * scale),
            Bottom = (int)Math.Round(rect.Bottom * scale),
        };
    }

    private static IReadOnlyList<UiaElementInfo> ReadAllElementsCore(
        IUIAutomation automation,
        IUIAutomationCondition condition,
        IntPtr hwnd)
    {
        var list = new List<UiaElementInfo>(64);

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
            LastDiagnostic = $"ElementFromHandle=ok FindAll=ok 元素数={length}";

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
            LastDiagnostic = $"InvalidCastException {ex.Message}";
        }

        return list;
    }

    /// <summary>最近一次 UIA 读取的诊断信息，用于探针输出。</summary>
    public static string LastDiagnostic { get; private set; } = "(尚未读取)";

    private static CandidateWindowSnapshot ReadCore(
        IUIAutomation automation,
        IUIAutomationCondition condition,
        IntPtr hwnd)
    {
        var elements = ReadAllElementsCore(automation, condition, hwnd);

        var items = new List<CandidateItem>(16);
        foreach (var element in elements)
        {
            if (element.IsOffscreen || element.ControlType != ListItemControlTypeId)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(element.Name) || element.Bounds.Width <= 0 || element.Bounds.Height <= 0)
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
            return CandidateWindowSnapshot.Empty;
        }

        // 按屏幕位置排序：先上后左，兼容横向与纵向两种候选窗布局。
        items.Sort((a, b) =>
        {
            var byTop = a.Top.CompareTo(b.Top);
            return byTop != 0 ? byTop : a.Left.CompareTo(b.Left);
        });

        var left = items.Min(i => i.Left);
        var top = items.Min(i => i.Top);
        var right = items.Max(i => i.Left + i.Width);
        var bottom = items.Max(i => i.Top + i.Height);

        return new CandidateWindowSnapshot(
            IsVisible: true,
            Left: left,
            Top: top,
            Width: right - left,
            Height: bottom - top,
            Items: items);
    }

    private string? GetProcessName(uint pid)
    {
        lock (_cacheLock)
        {
            if (_processNameCache.TryGetValue(pid, out var cached))
            {
                return cached;
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

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 把闲置进程的工作集交还系统。
/// </summary>
/// <remarks>
/// <para>
/// 设置窗口是 WPF 写的，第一次打开会拉起整套渲染设施：D3D 设备、窗口渲染目标、
/// 字形缓存、Fluent 控件模板。实测首次打开增加约 60 MB 私有内存，而且 WPF 在窗口
/// 关闭后不会拆除这套设施——它留在进程里等下一个窗口。连续打字时 UIA 那边也会
/// 留下一批 COM 对象的缓存。对一个只驻留托盘、设置窗口几分钟才看一次的程序，
/// 这些常驻页没有意义。
/// </para>
/// <para>
/// 两条触发路径，覆盖两种「用完了」的时机：
/// </para>
/// <list type="bullet">
/// <item>关窗后延迟 4 秒（避开「关了又开」）；</item>
/// <item>每 30 秒巡检一次，系统连续空闲 45 秒且本程序没有可见窗口时裁剪。</item>
/// </list>
/// <para>
/// 两条都要求「没有可见窗口」，因为裁剪后下一次访问要重新调页，
/// 落在用户正看着的界面上就是白等。页不会被丢弃，只是让出物理内存。
/// </para>
/// </remarks>
internal static class WorkingSetTrimmer
{
    /// <summary>关窗后等这么久再裁剪，避开「关了又开」。</summary>
    private const int DelayMilliseconds = 4000;

    /// <summary>启动后等这么久做第一次裁剪：留给开机自启动时的首轮初始化。</summary>
    public const int StartupDelayMilliseconds = 30_000;

    /// <summary>两次裁剪的最小间隔，防止高频开关窗口反复触发。</summary>
    private const int MinIntervalMilliseconds = 15_000;

    /// <summary>巡检周期。</summary>
    private const int WatchPeriodMilliseconds = 30_000;

    /// <summary>
    /// 巡检触发裁剪的最小间隔。
    /// </summary>
    /// <remarks>
    /// 一开始想用「系统连续空闲 N 秒」当判据，实测不可靠：只要用户还在别的程序里
    /// 动鼠标，全系统就算「忙」，可那些输入与本进程毫无关系，我们的页早就是冷的。
    /// 改成固定周期：只要自己没有可见窗口，就每几分钟把冷页让出去一次。
    /// 代价是下一次按键多几次缺页（毫秒级），换回几十兆物理内存。
    /// </remarks>
    private const int IdleMinIntervalMilliseconds = 3 * 60_000;

    private static readonly object Gate = new();

    private static Timer? _watch;
    private static long _lastTrimTick;
    private static long _lastIdleTrimTick;
    private static int _pending;

    /// <summary>启动空闲巡检。整个进程生命周期只需要调一次。</summary>
    public static void StartIdleWatch()
    {
        lock (Gate)
        {
            _watch ??= new Timer(_ => IdleTick(), null, WatchPeriodMilliseconds, WatchPeriodMilliseconds);
        }
    }

    /// <summary>请求一次延迟裁剪；已有请求排队时不重复排队。</summary>
    /// <param name="delayMilliseconds">延迟多久执行。</param>
    public static void Schedule(int delayMilliseconds = DelayMilliseconds)
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        _ = TrimLaterAsync(delayMilliseconds);
    }

    private static async Task TrimLaterAsync(int delayMilliseconds)
    {
        try
        {
            await Task.Delay(delayMilliseconds).ConfigureAwait(false);

            // 又有可见窗口了：这时候裁剪等于让用户下次点击去调页，不做。
            if (HasVisibleWindow())
            {
                return;
            }

            Trim(MinIntervalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Write($"[内存] 工作集裁剪失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _pending, 0);
        }
    }

    /// <summary>巡检一次：没有可见窗口、距上次裁剪足够久，就把冷页交还系统。</summary>
    private static void IdleTick()
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastIdleTrimTick) < IdleMinIntervalMilliseconds)
            {
                return;
            }

            if (HasVisibleWindow())
            {
                return;
            }

            if (!Trim(IdleMinIntervalMilliseconds))
            {
                return;
            }

            Interlocked.Exchange(ref _lastIdleTrimTick, now);
        }
        catch (Exception ex)
        {
            Log.Write($"[内存] 巡检失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>应用里是否还有可见窗口。</summary>
    /// <remarks>
    /// <see cref="Application.Windows"/> 属于 UI 线程的 <see cref="System.Windows.Threading.DispatcherObject"/>，
    /// 而这里跑在线程池上，直接读会抛「另一个线程拥有该对象」。所以要么切到 UI 线程读，
    /// 要么在 UI 线程已经退出时当没有窗口。
    /// </remarks>
    private static bool HasVisibleWindow()
    {
        var app = Application.Current;
        if (app is null)
        {
            return false;
        }

        var dispatcher = app.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return false;
            }

            return dispatcher.Invoke(HasVisibleWindow);
        }

        foreach (Window window in app.Windows)
        {
            if (window.IsVisible)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>执行裁剪，受最小间隔约束。</summary>
    /// <param name="minIntervalMilliseconds">距上次裁剪至少多久。</param>
    /// <returns>是否真的裁剪了。</returns>
    private static bool Trim(int minIntervalMilliseconds)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastTrimTick) < minIntervalMilliseconds)
        {
            return false;
        }

        Interlocked.Exchange(ref _lastTrimTick, now);

        using var process = Process.GetCurrentProcess();
        var before = process.WorkingSet64;

        // 两个 -1 是「尽量小」的文档化写法，比 EmptyWorkingSet 少一次解析依赖。
        if (!NativeMethods.SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1)))
        {
            Log.Write($"[内存] 工作集裁剪未生效（错误码 {Marshal.GetLastWin32Error()}）");
            return false;
        }

        process.Refresh();
        Log.Write(
            $"[内存] 工作集已交还系统：{before / 1024d / 1024d:0.0} MB → " +
            $"{process.WorkingSet64 / 1024d / 1024d:0.0} MB");
        return true;
    }
}

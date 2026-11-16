using System;
using System.Collections.Generic;
using System.Windows.Threading;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>检测到前台程序权限更高时的事件参数。</summary>
internal sealed class ElevatedAppEventArgs : EventArgs
{
    /// <summary>创建参数。</summary>
    /// <param name="processName">前台进程名。</param>
    /// <param name="level">该进程的完整性级别。</param>
    public ElevatedAppEventArgs(string processName, IntegrityLevel level)
    {
        ProcessName = processName;
        Level = level;
    }

    /// <summary>前台进程名，例如 <c>notepad</c>。</summary>
    public string ProcessName { get; }

    /// <summary>该进程的完整性级别。</summary>
    public IntegrityLevel Level { get; }
}

/// <summary>
/// 观察前台程序是否比本程序权限更高。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：Windows 的 UIPI 会让低级键盘钩子收不到高完整性窗口的按键，
/// 而且不报错、不回调。此时云联想"完全不工作"，日志里连一行都不会有——
/// 用户只能描述成"这个程序里不显示"。既然按键那条路上什么都拿不到，
/// 就只能另开一条与键盘无关的观察线：定时看前台窗口是谁、权限多少。
/// </para>
/// <para>
/// 用轮询而不是 <c>SetWinEventHook</c>：后者需要额外线程与消息泵，
/// 而这里只关心"有没有切到一个更高权限的程序"，2 秒粒度完全够用，
/// 单次开销只有一次 <c>GetForegroundWindow</c> 加一次字典查找。
/// </para>
/// <para>
/// 同一个程序名只报一次：用户切来切去不该反复弹提示。
/// </para>
/// </remarks>
internal sealed class ElevatedForegroundWatch : IDisposable
{
    private const int PollMilliseconds = 2000;

    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>创建观察器（尚未启动）。</summary>
    public ElevatedForegroundWatch()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollMilliseconds),
        };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>发现一个权限更高的前台程序（同一程序只触发一次）。</summary>
    public event EventHandler<ElevatedAppEventArgs>? Detected;

    /// <summary>最近一次检测到的程序名；没有则为 null。</summary>
    public string? LastProcessName { get; private set; }

    /// <summary>最近一次检测到的级别。</summary>
    public IntegrityLevel LastLevel { get; private set; }

    /// <summary>开始观察。重复调用是幂等的。</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    /// <summary>停止观察，但保留已经检测到的结果。</summary>
    public void Stop()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    private void Poll()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return;
        }

        // 「读不到」也算：普通权限读管理员进程的令牌会被直接拒绝，
        // 这时候级别是 Unknown，但正是最需要提示的情况。
        if (!ProcessIntegrity.IsWindowAboveSelfOrDenied(foreground))
        {
            return;
        }

        var level = ProcessIntegrity.ForWindow(foreground);

        var name = WindowProcess.GetProcessName(foreground);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        LastProcessName = name;
        LastLevel = level;

        if (!_reported.Add(name))
        {
            return;
        }

        Detected?.Invoke(this, new ElevatedAppEventArgs(name, level));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
    }
}

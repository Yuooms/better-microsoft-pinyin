using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 系统主题检测与监听。
/// </summary>
/// <remarks>
/// <para>
/// 读的是「应用模式」而不是「Windows 模式」：<c>AppsUseLightTheme</c> 控制应用窗口，
/// <c>SystemUsesLightTheme</c> 控制任务栏与开始菜单，这里要的是前者。
/// </para>
/// <para>
/// 监听 <see cref="SystemEvents.UserPreferenceChanged"/> 而不是只在开窗时读一次：
/// 常驻程序的云浮窗在启动时就创建了，只在开窗时读会让浮窗长期停留在错误的配色上。
/// 系统在改主题时会连续触发多次事件，所以先起一个 300ms 的计时器去抖，
/// 到点后重新读注册表，值没变就什么都不做。
/// </para>
/// </remarks>
internal static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static DispatcherTimer? _timer;
    private static bool _started;

    /// <summary>系统是否使用浅色应用主题；读不到按浅色处理。</summary>
    /// <returns>是否浅色。</returns>
    public static bool IsAppsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception)
        {
            // 注册表被策略锁定或键不存在：按浅色处理，不影响启动。
            return true;
        }
    }

    /// <summary>开始监听系统主题变化（UI 线程调用）。</summary>
    public static void Start()
    {
        if (_started)
        {
            return;
        }

        try
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += OnTick;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _started = true;
        }
        catch (Exception ex)
        {
            Log.Error("SystemTheme.Start", ex);
        }
    }

    /// <summary>停止监听。进程退出前调用；幂等。</summary>
    public static void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch (Exception)
        {
            // 退出路径上忽略。
        }

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        // 事件可能来自系统线程，且一次主题切换会连发多条，统一回到 UI 线程去抖。
        dispatcher.BeginInvoke(() =>
        {
            if (_timer is null)
            {
                return;
            }

            _timer.Stop();
            _timer.Start();
        });
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        ThemeManager.OnSystemThemeChanged();
    }
}

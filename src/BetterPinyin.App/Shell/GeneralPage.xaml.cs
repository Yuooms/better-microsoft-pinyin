using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 常规设置页。
/// </summary>
/// <remarks>
/// <para>
/// 开机启动有两种登记方式，互斥使用：注册表 Run 键（普通权限，改动不需要提权）
/// 和计划任务（以最高权限运行，改动需要提权）。系统是唯一事实来源：每次进入页面
/// 都重新读一遍，而不是相信配置文件里那两个布尔值——用户可能用别的工具删掉了启动项，
/// 也可能把程序整个目录搬走了。
/// </para>
/// <para>
/// 两个开关不是独立的两件事：第二个开关改变的是第一个开关的登记方式。
/// 关掉「开机启动」时两种登记都要清掉，否则用户以为关了、实际还留着一个。
/// </para>
/// </remarks>
public partial class GeneralPage : Page
{
    private bool _loading;

    /// <summary>创建页面。</summary>
    public GeneralPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadStartupState();
    }

    private void LoadStartupState()
    {
        _loading = true;
        try
        {
            var taskRead = AppServices.StartupTask.TryProbe(out var task, out var taskError);
            var runRead = AppServices.Startup.TryProbe(out var run, out var runError);

            if (!taskRead && !runRead)
            {
                StartupToggle.IsEnabled = false;
                StartupToggle.IsChecked = AppServices.Settings.StartWithWindows;
                ElevatedStartupToggle.IsEnabled = false;
                ElevatedStartupToggle.IsChecked = AppServices.Settings.StartWithWindowsElevated;
                SetStatus($"读取启动项失败：{taskError ?? runError}");
                return;
            }

            var taskMode = taskRead && task.Registered;
            var runMode = !taskMode && runRead && run.Registered;

            StartupToggle.IsEnabled = true;
            StartupToggle.IsChecked = taskMode || runMode;

            // 没开开机启动时，「以管理员身份启动」没有意义，置灰但不改选中状态。
            ElevatedStartupToggle.IsEnabled = taskMode || runMode;
            ElevatedStartupToggle.IsChecked = taskMode;

            var lines = new List<string>();

            if (taskMode)
            {
                lines.Add(task.RunsAsAdmin
                    ? $"已登记计划任务（以管理员权限运行）：{task.Command}"
                    : $"已登记计划任务，但没有登记为最高权限运行：{task.Command}。请在任务计划程序中检查，或关闭再开启一次。");

                if (!task.MatchesCurrent)
                {
                    lines.Add("任务指向别的位置，关闭再开启一次就能改到当前目录。");
                }

                if (!ProcessIntegrity.IsSelfElevated)
                {
                    lines.Add("下次登录生效。要在管理员权限的程序里立刻用上云联想，请到「云联想」页点「以管理员身份重启」。");
                }
            }
            else if (runMode)
            {
                lines.Add(run.MatchesCurrent
                    ? $"已登记：{run.Command}"
                    : $"启动项指向别的位置，下次从当前目录启动时会自动改回来。当前值：{run.Command}");

                lines.Add("这种方式启动的进程是普通权限，在以管理员权限运行的程序里云联想不生效。");
            }
            else
            {
                lines.Add("当前未登记。开启后登录 Windows 就会自动在后台运行。");
            }

            // 两种登记同时存在会让登录时启动两次：第二个实例只会把第一个叫到前台。
            if (taskMode && runRead && run.Registered)
            {
                lines.Add("检测到注册表启动项与计划任务同时存在，登录时会启动两次。关闭再开启一次即可清理。");
            }

            if (!taskRead)
            {
                lines.Add($"(计划任务状态读取失败：{taskError})");
            }

            if (!runRead)
            {
                lines.Add($"(注册表启动项读取失败：{runError})");
            }

            SetStatus(string.Join("\n", lines));

            // 配置和实际登记不一致时以系统为准，免得界面显示的和实际行为对不上。
            // 只有两种登记都读到了才敢同步：读失败时若照样写回，会把用户开着的提权启动
            // 悄悄改成关闭，而任务其实还在按登录触发。
            var startWithWindows = taskMode || runMode;
            var startElevated = taskMode;
            if (taskRead && runRead &&
                (AppServices.Settings.StartWithWindows != startWithWindows ||
                 AppServices.Settings.StartWithWindowsElevated != startElevated))
            {
                AppServices.Settings.StartWithWindows = startWithWindows;
                AppServices.Settings.StartWithWindowsElevated = startElevated;
                AppServices.SaveSettings();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = StartupToggle.IsChecked == true;
        ApplyResult(
            enabled ? EnableRunKeyStartup() : DisableStartup(),
            () => StartupToggle.IsChecked = !enabled);
    }

    private void OnElevatedStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var elevated = ElevatedStartupToggle.IsChecked == true;
        ApplyResult(
            elevated ? SwitchToElevatedStartup() : SwitchToRunKeyStartup(),
            () => ElevatedStartupToggle.IsChecked = !elevated);
    }

    /// <summary>
    /// 收尾：失败就把开关拨回去，成功则按系统实际状态重画页面。
    /// </summary>
    /// <param name="error">失败原因；成功时为 null。</param>
    /// <param name="revert">把开关恢复原状的动作。</param>
    private void ApplyResult(string? error, Action revert)
    {
        if (error is not null)
        {
            // 不能显示「已开启」但实际没登记。
            _loading = true;
            revert();
            _loading = false;
            SetStatus(error);
            return;
        }

        LoadStartupState();
    }

    private static string? EnableRunKeyStartup() =>
        AppServices.Startup.TryEnable(out var error) ? null : $"开启失败：{error}";

    /// <summary>关闭开机启动：两种登记都清掉，避免留下看不见的残留。</summary>
    private static string? DisableStartup()
    {
        var problems = new List<string>();

        if (!AppServices.Startup.TryDisable(out var runError))
        {
            problems.Add($"移除注册表启动项失败：{runError}");
        }

        if (!AppServices.StartupTask.TryDisable(out var taskError))
        {
            problems.Add($"删除启动任务失败：{taskError}");
        }

        return problems.Count == 0 ? null : string.Join("；", problems);
    }

    /// <summary>
    /// 改用计划任务（管理员权限）启动。
    /// </summary>
    /// <remarks>
    /// 先建任务、再删注册表项：反过来的话，建任务失败时用户就什么都不启动了。
    /// </remarks>
    private static string? SwitchToElevatedStartup()
    {
        if (!AppServices.StartupTask.TryEnable(out var taskError))
        {
            return $"登记计划任务失败：{taskError ?? "未知原因"}";
        }

        if (!AppServices.Startup.TryDisable(out var runError))
        {
            return $"计划任务已登记，但移除注册表启动项失败：{runError}。两项同时存在会导致登录时启动两次，请关闭「开机启动」后重新开启。";
        }

        return null;
    }

    /// <summary>改回注册表方式启动。</summary>
    /// <remarks>
    /// 与上面相反，先把注册表项写回去再删任务：中途失败至少还有一条能启动。
    /// </remarks>
    private static string? SwitchToRunKeyStartup()
    {
        if (!AppServices.Startup.TryEnable(out var runError))
        {
            return $"写回注册表启动项失败：{runError}";
        }

        if (!AppServices.StartupTask.TryDisable(out var taskError))
        {
            return $"注册表启动项已恢复，但删除计划任务失败：{taskError}。两项同时存在会导致登录时启动两次，请关闭「开机启动」后重新开启。";
        }

        return null;
    }

    private void SetStatus(string text) => StatusText.Text = text;
}

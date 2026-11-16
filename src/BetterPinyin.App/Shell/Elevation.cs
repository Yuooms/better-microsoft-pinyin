using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 以管理员身份重启本程序。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：Windows 的 UIPI 规定低级键盘钩子收不到高完整性窗口的按键。
/// 于是任何"以管理员身份运行"的程序（任务管理器、regedit、管理员终端）里，
/// 云联想都会静默失效。唯一的解法是让本程序也跑在同一级别，
/// 也就是提权——而提权必须由用户点一次 UAC。
/// </para>
/// <para>
/// 提权只影响这一次进程，写回配置没有意义：下次照常启动仍然是普通权限。
/// 想开机就提权需要改用计划任务，属于另一个话题，界面上会写明。
/// </para>
/// </remarks>
internal static class Elevation
{
    /// <summary>新实例用它来等待旧实例退出。</summary>
    public const string ReplaceArgument = "--replace";

    /// <summary>ERROR_CANCELLED：用户在 UAC 弹窗上点了"否"。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// 以管理员身份启动一个新的实例。
    /// </summary>
    /// <param name="error">失败原因，成功时为 null。</param>
    /// <returns>已经开始启动返回 true；调用方应随后退出当前实例。</returns>
    public static bool TryRestartElevated(out string? error)
    {
        error = null;

        if (ProcessIntegrity.IsSelfElevated)
        {
            error = "当前已经是管理员权限";
            return false;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            error = "取不到程序路径";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = ReplaceArgument,
                WorkingDirectory = AppContext.BaseDirectory,
            });

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // 用户取消不是错误，不该弹报错，只说"没重启"。
            error = "已取消管理员授权";
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            error = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }
}

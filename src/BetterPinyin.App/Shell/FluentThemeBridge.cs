using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 把项目自己的主题状态同步给 WPF-UI。
/// </summary>
/// <remarks>
/// <para>
/// 配色仍由 <see cref="ThemeManager"/> 决定（它也负责候选浮窗的 31 个画刷键）。
/// 这里只负责让设置窗口里的 Fluent 控件跟着变深/变浅。
/// </para>
/// <para>
/// 运行期刻意不直接调用 <see cref="ApplicationThemeManager.Apply"/>：
/// 它会通过 <c>WindowBackgroundManager</c> 重置 <see cref="Application.MainWindow"/> 的背景，
/// 而本程序的 MainWindow 有可能先被候选浮窗占住——那会把浮窗的透明背景刷成实色。
/// 因此运行期只替换主题字典；只有主窗口确实是设置窗口时，才让 WPF-UI 走一遍完整流程。
/// </para>
/// </remarks>
internal static class FluentThemeBridge
{
    private const string ThemeDictionaryMarker = "/Wpf.Ui;component/Resources/Theme/";

    /// <summary>启动时调用一次，必须在创建任何窗口之前。</summary>
    /// <param name="dark">是否使用深色。</param>
    public static void ApplyStartup(bool dark)
    {
        // 此时 MainWindow 还是 null，WPF-UI 不会去动任何窗口的背景。
        ApplicationThemeManager.Apply(
            dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.None,
            updateAccent: false);
    }

    /// <summary>运行期主题变化后调用。</summary>
    /// <param name="dark">是否使用深色。</param>
    public static void ApplyRuntime(bool dark)
    {
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ReplaceThemeDictionary(theme);

        if (Application.Current.MainWindow is SettingsWindow { IsLoaded: true })
        {
            // 让 WPF-UI 更新内部缓存与 ClientAreaBorder 的 1px 边框色；
            // 此时 MainWindow 就是设置窗口，不会波及候选浮窗。
            ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
        }
    }

    /// <summary>只替换应用级主题字典，不触发窗口级副作用。</summary>
    /// <param name="theme">目标主题。</param>
    private static void ReplaceThemeDictionary(ApplicationTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is null || !source.Contains(ThemeDictionaryMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            dictionaries[i] = new ThemesDictionary { Theme = theme };
            return;
        }

        // 没找到说明资源顺序被改过，退回完整应用一次，保证界面至少能正确着色。
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
    }
}

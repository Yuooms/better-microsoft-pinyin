using System.Windows;
using System.Windows.Controls;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 外观设置页。
/// </summary>
/// <remarks>
/// 主题是所见即所得的东西，要求「点保存」才变色是反直觉的，所以这一页没有保存按钮：
/// 选择即刻写入配置并应用。只有两个下拉，误操作成本为零。
/// </remarks>
public partial class AppearancePage : UserControl
{
    private bool _loading;

    /// <summary>创建页面。</summary>
    public AppearancePage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        SelectByTag(ThemeComboBox, AppearanceOptions.ToSettingValue(
            AppearanceOptions.ParseTheme(AppServices.Settings.ThemeMode)));
        SelectByTag(BackdropComboBox, AppearanceOptions.ToSettingValue(
            AppearanceOptions.ParseBackdrop(AppServices.Settings.BackdropMode)));
        _loading = false;
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var theme = AppearanceOptions.ParseTheme(TagOf(ThemeComboBox, "System"));
        AppServices.Settings.ThemeMode = AppearanceOptions.ToSettingValue(theme);
        AppServices.SaveSettings();

        ThemeManager.SetPreference(theme);

        // 同一个主题（例如从「跟随系统」切到与系统一致的「浅色」）不会触发 ThemeChanged，
        // 但窗口仍需要一次重绘确认；这里显式刷新一次。
        if (Window.GetWindow(this) is Window window)
        {
            ShellChrome.Refresh(window);
        }
    }

    private void OnBackdropSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var mode = AppearanceOptions.ParseBackdrop(TagOf(BackdropComboBox, "Auto"));
        AppServices.Settings.BackdropMode = AppearanceOptions.ToSettingValue(mode);
        AppServices.SaveSettings();

        if (Window.GetWindow(this) is Window window)
        {
            ShellChrome.Refresh(window);
        }
    }

    private static string TagOf(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem comboItem &&
                string.Equals(comboItem.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboItem;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }
}

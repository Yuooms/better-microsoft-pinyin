using System.Windows;
using System.Windows.Controls;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 常规设置页。
/// </summary>
/// <remarks>
/// 目前只有开机启动。注册表是唯一事实来源：每次进入页面都重新读一遍，
/// 而不是相信配置文件里那个布尔值——用户可能用别的工具删掉了启动项，
/// 也可能把程序整个目录搬走了。
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
            if (!AppServices.Startup.TryProbe(out var state, out var error))
            {
                StartupToggle.IsEnabled = false;
                StartupToggle.IsChecked = AppServices.Settings.StartWithWindows;
                SetStatus($"读取启动项失败：{error}");
                return;
            }

            StartupToggle.IsEnabled = true;
            StartupToggle.IsChecked = state.Registered;

            if (!state.Registered)
            {
                SetStatus("当前未登记。开启后登录 Windows 就会自动在后台运行。");
            }
            else if (state.MatchesCurrent)
            {
                SetStatus($"已登记：{state.Command}");
            }
            else
            {
                SetStatus($"启动项指向别的位置，下次从当前目录启动时会自动改回来。当前值：{state.Command}");
            }

            // 配置和注册表不一致时以注册表为准，免得界面显示的和实际行为对不上。
            if (AppServices.Settings.StartWithWindows != state.Registered)
            {
                AppServices.Settings.StartWithWindows = state.Registered;
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
        var error = enabled
            ? EnableStartup()
            : DisableStartup();

        if (error is not null)
        {
            // 写失败就把开关拨回去：不能显示「已开启」但实际没登记。
            _loading = true;
            StartupToggle.IsChecked = !enabled;
            _loading = false;
            SetStatus(error);
            return;
        }

        AppServices.Settings.StartWithWindows = enabled;
        AppServices.SaveSettings();
        LoadStartupState();
    }

    private static string? EnableStartup() =>
        AppServices.Startup.TryEnable(out var error) ? null : $"开启失败：{error}";

    private static string? DisableStartup() =>
        AppServices.Startup.TryDisable(out var error) ? null : $"关闭失败：{error}";

    private void SetStatus(string text) => StatusText.Text = text;
}

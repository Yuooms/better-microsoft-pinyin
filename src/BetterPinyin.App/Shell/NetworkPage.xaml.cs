using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterPinyin.Core.Network;
using BetterPinyin.Interop.Ime;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 网络与环境体检页。
/// </summary>
/// <remarks>
/// 这个页面存在的意义是收敛问题反馈：绝大多数"装了没反应"都能在这里一次性看出原因，
/// 例如输入法不是微软拼音、开了双拼、UIA 不可用、或端点全部不通。
/// </remarks>
public partial class NetworkPage : Page
{
    // 颜色必须每次现取：切主题后旧画刷实例不会跟着变。
    private static Brush OkBrush => ThemeManager.ResolveBrush("SuccessBrush") ?? Brushes.Green;
    private static Brush WarnBrush => ThemeManager.ResolveBrush("WarningBrush") ?? Brushes.Orange;
    private static Brush FailBrush => ThemeManager.ResolveBrush("ErrorBrush") ?? Brushes.Red;

    private readonly EndpointProbe _probe = new(Http.Client);
    private bool _probing;

    /// <summary>创建页面。</summary>
    public NetworkPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshEnvironment();
            RefreshSettings();
        };
    }

    private void RefreshEnvironment()
    {
        EnvironmentPanel.Children.Clear();

        var settings = MsPinyinDetector.ReadSettingsSnapshot();
        var installed = MsPinyinDetector.IsMsPinyinInstalled();
        var fuzzy = MsPinyinDetector.IsFuzzyInputEnabled(settings);
        var doublePinyin = MsPinyinDetector.IsDoublePinyinEnabled(settings);

        AddRow("微软拼音", installed, installed ? "已安装" : "未检测到，本功能依赖微软拼音");
        AddRow("候选窗读取 (UIA)", AppServices.UiaAvailable,
            AppServices.UiaAvailable ? "可用，可读取候选词用于去重" : "不可用，将跳过去重但仍可上屏");
        AddRow("模糊音", fuzzy, fuzzy ? "已开启，读音校验会按模糊音放宽" : "未开启，读音校验按严格匹配");
        AddRow("双拼", !doublePinyin, doublePinyin ? "已开启，云联想将自动停用" : "未开启");
        AddRow("输入法状态", true, $"前台键盘布局 {MsPinyinDetector.DescribeLanguage(MsPinyinDetector.GetForegroundLanguageId())}");
    }

    private void RefreshSettings()
    {
        SettingsPanel.Children.Clear();

        AddInfoText($"端点策略：{AppServices.Settings.EndpointMode}（自动模式下按体检结果依次尝试）");
        AddInfoText($"代理：{AppServices.Settings.ProxyMode}");
        AddInfoText($"请求超时：{AppServices.Settings.RequestTimeoutMs} 毫秒");
        AddInfoText($"缓存时长：{AppServices.Settings.CacheMinutes} 分钟");
        AddInfoText($"配置文件：{AppServices.Store.FilePath}");
    }

    private async void OnProbeClick(object sender, RoutedEventArgs e)
    {
        if (_probing)
        {
            return;
        }

        _probing = true;
        ProbeButton.IsEnabled = false;
        ProbeStatusText.Text = "正在检测…";
        EndpointPanel.Children.Clear();

        try
        {
            var results = await _probe.ProbeAllAsync().ConfigureAwait(true);

            foreach (var result in results)
            {
                AddRowToEndpointPanel(
                    $"{result.Endpoint.Host}{result.Endpoint.Path}",
                    result.Reachable,
                    result.Reachable
                        ? $"{result.LatencyMilliseconds} 毫秒，{result.ResultCount} 条结果" +
                          (result.Endpoint.HasContentFilter ? "（有内容过滤）" : string.Empty)
                        : $"不可用：{result.Error}");
            }

            var usable = results.Count(r => r.Reachable);
            ProbeStatusText.Text = usable == 0
                ? "全部端点不可用：请检查网络，或在云联想页确认是否需要代理"
                : $"可用端点 {usable} / {results.Count}，程序会按此顺序自动尝试";
        }
        catch (Exception ex)
        {
            ProbeStatusText.Text = $"检测失败：{ex.Message}";
        }
        finally
        {
            _probing = false;
            ProbeButton.IsEnabled = true;
        }
    }

    private void AddRow(string name, bool ok, string detail)
    {
        var panel = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nameText = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        var statusText = new TextBlock
        {
            Text = ok ? "正常" : "注意",
            Foreground = ok ? OkBrush : WarnBrush,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };
        var detailText = new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        detailText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        Grid.SetColumn(statusText, 1);
        Grid.SetColumn(detailText, 2);
        panel.Children.Add(nameText);
        panel.Children.Add(statusText);
        panel.Children.Add(detailText);

        EnvironmentPanel.Children.Add(panel);
    }

    private void AddInfoText(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
            FontSize = 12,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        // 这些是「当前生效设置」，必须进 SettingsPanel；此前误加到 EnvironmentPanel，
        // 导致页面底部那块永远是空的。
        SettingsPanel.Children.Add(block);
    }

    private void AddRowToEndpointPanel(string name, bool ok, string detail)
    {
        EndpointPanel.Children.Add(new TextBlock
        {
            Text = $"{name} · {(ok ? "可用" : "不可用")} · {detail}",
            Foreground = ok ? OkBrush : FailBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
        });
    }
}

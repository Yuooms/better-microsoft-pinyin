using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterPinyin.Core.Settings;
using BetterPinyin.Core.Statistics;

namespace BetterPinyin.App.Features.Statistics.Views;

/// <summary>柱状图的一根柱子。</summary>
public sealed class ChartBar
{
    /// <summary>X 轴标签。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>柱高（像素）。</summary>
    public double BarHeight { get; init; }

    /// <summary>悬停提示。</summary>
    public string Tooltip { get; init; } = string.Empty;
}

/// <summary>程序排行的一行。</summary>
public sealed class AppBar
{
    /// <summary>进程名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>条形宽度（像素）。</summary>
    public double BarWidth { get; init; }

    /// <summary>右侧的数字。</summary>
    public string Value { get; init; } = string.Empty;
}

/// <summary>
/// 输入统计的设置页。
/// </summary>
/// <remarks>
/// 与云联想页一样直接操作控件、不搭 ViewModel 层：控件数量有限，
/// 显式赋值比双向绑定更容易看出「什么时候生效」。
/// 开关即时生效（统计要能立刻停），保存按钮负责把当前数字落盘。
/// </remarks>
public partial class StatisticsPage : Page
{
    /// <summary>柱子可用的最大高度。</summary>
    private const double ChartHeight = 150;

    /// <summary>程序排行条形轨道的宽度，与 XAML 里保持一致。</summary>
    private const double AppBarMaxWidth = 420;

    private readonly StatisticsFeature _feature;

    // 页面由导航缓存，切走再切回会再次触发 Loaded。
    // 只有第一次才把设置读进控件，否则会覆盖用户尚未保存的修改。
    private bool _initialized;

    // 程序赋值控件时会触发 Checked/SelectionChanged，用这个标记挡掉。
    private bool _loading;

    /// <summary>创建设置页。</summary>
    /// <param name="feature">所属功能模块。</param>
    internal StatisticsPage(StatisticsFeature feature)
    {
        _feature = feature;
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private AppSettings Settings => AppServices.Settings;

    /// <summary>图上的一个点：按小时展示时 <see cref="Hour"/> 有效，按天展示时为 -1。</summary>
    private readonly record struct ChartPoint(DateTime Date, int Hour, string Label);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            _initialized = true;
            LoadFromSettings();
        }

        // 每次回到页面都重新取快照，数字不会停在打开时的样子。
        Refresh();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            EnabledToggle.IsChecked = Settings.StatisticsEnabled;
            PerAppToggle.IsChecked = Settings.StatisticsPerAppEnabled;
            RangeCombo.SelectedIndex = 1;
            MetricCombo.SelectedIndex = 0;

            // 总开关关着时，子选项没有意义，置灰避免让人以为还在记录。
            PerAppToggle.IsEnabled = Settings.StatisticsEnabled;

            RetentionCombo.SelectedIndex = Settings.StatisticsRetentionDays switch
            {
                30 => 0,
                90 => 1,
                0 => 3,
                _ => 2,
            };
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.StatisticsEnabled = EnabledToggle.IsChecked == true;
        Settings.StatisticsPerAppEnabled = PerAppToggle.IsChecked == true;
        AppServices.SaveSettings();

        PerAppToggle.IsEnabled = Settings.StatisticsEnabled;
        _feature.SetEnabled(Settings.StatisticsEnabled);
        Refresh();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        AppServices.SaveSettings();

        SaveStatusText.Text = _feature.SaveNow()
            ? "已保存。"
            : $"保存失败：{_feature.Store.LastError}";

        Refresh();
    }

    private void OnRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RetentionCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag || !int.TryParse(tag, out var days))
        {
            return;
        }

        Settings.StatisticsRetentionDays = days;
        AppServices.SaveSettings();

        // 立刻落盘一次，让「改成更短的保留期」马上生效。
        _feature.SaveNow();
        Refresh();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        var days = _feature.Aggregator.Snapshot();
        var total = days.Sum(d => d.Totals.Compositions);
        var range = days.Count == 0
            ? "暂无记录"
            : $"{days[0].Date} 至 {days[^1].Date}，共 {days.Count} 天、{total} 次组字";

        var confirm = MessageBox.Show(
            $"将删除本机全部输入统计（{range}）。\n\n此操作不可撤销，不会保留备份。设置与词库不受影响。",
            "清空输入统计",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _feature.ClearAll();
        Refresh();
    }

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _loading)
        {
            return;
        }

        Refresh();
    }

    private void Refresh()
    {
        if (!_initialized)
        {
            return;
        }

        var days = _feature.Aggregator.Snapshot();
        var points = ResolvePoints();

        FillToday(days);
        FillChart(days, points);
        FillApps(days, points);
        FillDataInfo(days);
        ShowStartupWarning();
    }

    /// <summary>钩子没装上时把原因说出来，否则用户只能看到一个沉默的 0。</summary>
    private void ShowStartupWarning()
    {
        // 用户主动关掉统计时不该再提示「没在运行」，那是预期状态。
        var error = Settings.StatisticsEnabled ? _feature.LastError : null;
        if (error is null)
        {
            WarningText.Visibility = Visibility.Collapsed;
            return;
        }

        WarningText.Text = $"统计没有在运行：{error}。数字不会增长，可能是安全软件拦截了键盘钩子。";
        WarningText.Visibility = Visibility.Visible;
    }

    private void FillToday(IReadOnlyList<DailyStatistics> days)
    {
        var todayKey = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var today = days.FirstOrDefault(d => d.Date == todayKey) ?? new DailyStatistics { Date = todayKey };

        TodayGrid.Children.Clear();
        TodayGrid.Children.Add(CreateMetricCard("组字次数", today.Totals.Compositions.ToString(), "打了几次词"));
        TodayGrid.Children.Add(CreateMetricCard("拼音字母", today.Totals.Letters.ToString(), "敲了多少字母"));
        TodayGrid.Children.Add(CreateMetricCard("云词采纳", today.Totals.CloudAccepts.ToString(), "从浮窗选了几次"));
        TodayGrid.Children.Add(CreateMetricCard("云词字数", today.Totals.CloudCharacters.ToString(), "唯一算得出汉字数的项"));
    }

    private FrameworkElement CreateMetricCard(string title, string value, string hint)
    {
        var secondary = TryFindBrush("TextFillColorSecondaryBrush");

        var stack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            Foreground = secondary,
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = hint,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = secondary,
        });

        return new Border
        {
            Style = TryFindStyle("Card"),
            Margin = new Thickness(0, 0, 8, 0),
            Child = stack,
        };
    }

    private void FillChart(IReadOnlyList<DailyStatistics> days, IReadOnlyList<ChartPoint> points)
    {
        var useLetters = MetricCombo.SelectedIndex == 1;
        var values = points.Select(p => ReadValue(days, p, useLetters)).ToArray();
        var max = values.Length == 0 ? 0 : values.Max();

        var bars = new List<ChartBar>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            // 点数多的时候标签会挤在一起，按固定间隔显示，首尾必显。
            var showLabel = points.Count <= 10 || i == 0 || i == points.Count - 1 || i % 5 == 0;
            var when = points[i].Hour >= 0
                ? $"{points[i].Date:yyyy-MM-dd} {points[i].Hour}:00"
                : $"{points[i].Date:yyyy-MM-dd}";

            bars.Add(new ChartBar
            {
                Label = showLabel ? points[i].Label : string.Empty,
                BarHeight = max == 0 || values[i] == 0
                    ? 0
                    : Math.Max(3, values[i] / (double)max * ChartHeight),
                Tooltip = $"{when}　{values[i]}",
            });
        }

        ChartItems.ItemsSource = bars;

        var hasData = max > 0;
        EmptyText.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        ChartItems.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;

        ChartHint.Text = useLetters
            ? "柱高 = 拼音字母数。只统计中文布局下、落在组字串里的字母，不含退格与直接上屏的英文。"
            : "柱高 = 组字次数（一段连续拼音算一次）。这是最接近「打了多少个词」的口径。";
    }

    private void FillApps(IReadOnlyList<DailyStatistics> days, IReadOnlyList<ChartPoint> points)
    {
        var keys = new HashSet<string>(
            points.Select(p => p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            StringComparer.Ordinal);

        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in days)
        {
            if (!keys.Contains(day.Date))
            {
                continue;
            }

            foreach (var app in day.Apps)
            {
                totals.TryGetValue(app.Process, out var current);
                totals[app.Process] = current + app.Compositions;
            }
        }

        var ranked = totals
            .Where(p => p.Value > 0)
            .OrderByDescending(p => p.Value)
            .Take(10)
            .ToList();

        var max = ranked.Count == 0 ? 0 : ranked[0].Value;
        AppItems.ItemsSource = ranked
            .Select(p => new AppBar
            {
                Name = p.Key,
                Value = p.Value.ToString(CultureInfo.InvariantCulture),
                BarWidth = max == 0 ? 0 : Math.Max(2, p.Value / (double)max * AppBarMaxWidth),
            })
            .ToList();

        var hasData = ranked.Count > 0;
        AppItems.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        AppEmptyText.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        AppEmptyText.Text = Settings.StatisticsPerAppEnabled
            ? "这段时间还没有按程序统计的数据。"
            : "「按程序分别统计」已关闭，所以没有程序维度的数据。";
    }

    private void FillDataInfo(IReadOnlyList<DailyStatistics> days)
    {
        long bytes = 0;
        try
        {
            var info = new FileInfo(_feature.FilePath);
            if (info.Exists)
            {
                bytes = info.Length;
            }
        }
        catch (Exception)
        {
            // 文件被占用或权限不足时不显示大小，不影响其它内容。
        }

        var size = bytes switch
        {
            <= 0 => "尚无文件",
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / 1024.0 / 1024.0:F1} MB",
        };

        DataInfoText.Text = $"{_feature.FilePath}\n{size}　{days.Count} 天记录";
    }

    /// <summary>按当前范围算出要展示的点。</summary>
    /// <returns>「今天」返回 24 个小时点，其余范围返回逐日的点。</returns>
    private IReadOnlyList<ChartPoint> ResolvePoints()
    {
        var today = DateTime.Today;

        switch (RangeCombo.SelectedIndex)
        {
            case 0:
                return Enumerable.Range(0, DailyStatistics.HourCount)
                    .Select(h => new ChartPoint(today, h, h.ToString(CultureInfo.InvariantCulture)))
                    .ToList();

            case 1:
                return Enumerable.Range(0, 7)
                    .Select(i => today.AddDays(-6 + i))
                    .Select(d => new ChartPoint(d, -1, d.ToString("M-d", CultureInfo.InvariantCulture)))
                    .ToList();

            case 2:
                return Enumerable.Range(0, 30)
                    .Select(i => today.AddDays(-29 + i))
                    .Select(d => new ChartPoint(d, -1, d.ToString("M-d", CultureInfo.InvariantCulture)))
                    .ToList();

            default:
                var first = new DateTime(today.Year, today.Month, 1);
                var length = DateTime.DaysInMonth(today.Year, today.Month);
                return Enumerable.Range(0, length)
                    .Select(i => first.AddDays(i))
                    .Select(d => new ChartPoint(d, -1, d.ToString("M-d", CultureInfo.InvariantCulture)))
                    .ToList();
        }
    }

    /// <summary>取某个点对应的数值。</summary>
    private static int ReadValue(IReadOnlyList<DailyStatistics> days, ChartPoint point, bool useLetters)
    {
        var key = point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var day = days.FirstOrDefault(d => d.Date == key);
        if (day is null)
        {
            return 0;
        }

        if (point.Hour >= 0)
        {
            var (compositions, letters) = day.GetHour(point.Hour);
            return useLetters ? letters : compositions;
        }

        return useLetters ? day.Totals.Letters : day.Totals.Compositions;
    }

    private Brush? TryFindBrush(string key) => TryFindResource(key) as Brush;

    private Style? TryFindStyle(string key) => TryFindResource(key) as Style;
}

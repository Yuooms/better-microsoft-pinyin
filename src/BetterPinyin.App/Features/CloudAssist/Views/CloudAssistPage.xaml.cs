using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BetterPinyin.Core.Settings;

namespace BetterPinyin.App.Features.CloudAssist.Views;

/// <summary>
/// 云联想的设置页。
/// </summary>
/// <remarks>
/// 直接读写 <see cref="AppSettings"/>，不做 ViewModel 层的双向绑定：
/// 这个页面只有十几个控件，显式赋值比搭一套绑定基础设施更易读、更少出错，
/// 也让"点保存才生效"的语义一目了然。
/// </remarks>
public partial class CloudAssistPage : UserControl
{
    private const int MaxDiagnosticLines = 200;

    private readonly CloudAssistFeature _feature;
    private readonly List<string> _diagnosticLines = new();

    /// <summary>创建设置页。</summary>
    /// <param name="feature">所属功能模块。</param>
    internal CloudAssistPage(CloudAssistFeature feature)
    {
        _feature = feature;
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AppSettings Settings => AppServices.Settings;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadFromSettings();
        _feature.Controller.Diagnostics += OnDiagnostics;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _feature.Controller.Diagnostics -= OnDiagnostics;
    }

    private void OnDiagnostics(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _diagnosticLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
            if (_diagnosticLines.Count > MaxDiagnosticLines)
            {
                _diagnosticLines.RemoveRange(0, _diagnosticLines.Count - MaxDiagnosticLines);
            }

            DiagnosticsTextBox.Text = string.Join(Environment.NewLine, _diagnosticLines);
            DiagnosticsTextBox.ScrollToEnd();
        });
    }

    private void LoadFromSettings()
    {
        EnabledCheckBox.IsChecked = Settings.CloudAssistEnabled;
        DoublePinyinCheckBox.IsChecked = Settings.DoublePinyinMode;
        ShowCloudIconCheckBox.IsChecked = Settings.ShowCloudIcon;
        FuzzyCheckBox.IsChecked = Settings.FuzzyPinyin;

        SelectByTag(AcceptKeyComboBox, Settings.AcceptKey);
        SelectByTag(MaxSuggestionsComboBox, Settings.MaxSuggestions.ToString(CultureInfo.InvariantCulture));

        MinLengthTextBox.Text = Settings.MinQueryLength.ToString(CultureInfo.InvariantCulture);
        DebounceTextBox.Text = Settings.DebounceMs.ToString(CultureInfo.InvariantCulture);
        TimeoutTextBox.Text = Settings.RequestTimeoutMs.ToString(CultureInfo.InvariantCulture);
        BlockedTextBox.Text = string.Join(", ", Settings.BlockedProcesses);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Settings.CloudAssistEnabled = EnabledCheckBox.IsChecked == true;
        Settings.DoublePinyinMode = DoublePinyinCheckBox.IsChecked == true;
        Settings.ShowCloudIcon = ShowCloudIconCheckBox.IsChecked == true;
        Settings.FuzzyPinyin = FuzzyCheckBox.IsChecked == true;

        Settings.AcceptKey = (AcceptKeyComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Tab";
        Settings.MaxSuggestions = ParseInt((MaxSuggestionsComboBox.SelectedItem as ComboBoxItem)?.Tag as string, 1, 1, 3);
        Settings.MinQueryLength = ParseInt(MinLengthTextBox.Text, Settings.MinQueryLength, 1, 8);
        Settings.DebounceMs = ParseInt(DebounceTextBox.Text, Settings.DebounceMs, 40, 500);
        Settings.RequestTimeoutMs = ParseInt(TimeoutTextBox.Text, Settings.RequestTimeoutMs, 300, 5000);

        Settings.BlockedProcesses = BlockedTextBox.Text
            .Split(new[] { ',', '，', ';', '；', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var saved = AppServices.SaveSettings();
        _feature.ApplySettings();

        SaveStatusText.Text = saved
            ? "已保存并立即生效"
            : $"已应用，但写入配置失败：{AppServices.Store.LastError}";
    }

    private static int ParseInt(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

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

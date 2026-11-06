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
/// 也让「点保存才生效」的语义一目了然。
/// </remarks>
public partial class CloudAssistPage : Page
{
    private const int MaxDiagnosticLines = 200;

    private readonly CloudAssistFeature _feature;
    private readonly List<string> _diagnosticLines = new();

    // 页面由导航缓存，切走再切回会再次触发 Loaded。
    // 只有第一次才把设置读进控件，否则会覆盖用户尚未保存的修改。
    private bool _initialized;

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
        if (!_initialized)
        {
            _initialized = true;
            LoadFromSettings();
        }

        // 先减后加：缓存页面反复 Loaded 时不会累积重复订阅。
        _feature.Controller.Diagnostics -= OnDiagnostics;
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
        StrictCheckBox.IsChecked = Settings.StrictEnabled;
        StrictDedupeCheckBox.IsChecked = Settings.StrictDedupeLocal;
        ExtendedCheckBox.IsChecked = Settings.ExtendedEnabled;
        ShowCloudIconCheckBox.IsChecked = Settings.ShowCloudIcon;
        FuzzyCheckBox.IsChecked = Settings.FuzzyPinyin;
        DoublePinyinCheckBox.IsChecked = Settings.DoublePinyinMode;
        EnglishCheckBox.IsChecked = Settings.EnglishModeAssistEnabled;

        SelectByTag(AcceptKeyComboBox, Settings.StrictAcceptKey);
        SelectByTag(ExtendedCountComboBox, Settings.ExtendedMaxCount.ToString(CultureInfo.InvariantCulture));
        SelectByTag(ExtendedKeyModeComboBox, Settings.ExtendedKeyMode);
        SelectByTag(PlacementComboBox, Settings.OverlayPlacement);
        SelectByTag(PopupStyleComboBox, Settings.PopupStyle);
        SelectByTag(EnglishCommitComboBox, Settings.EnglishModeCommit);

        ExtendedKeysTextBox.Text = Settings.ExtendedKeys;
        MinLengthTextBox.Text = Settings.MinQueryLength.ToString(CultureInfo.InvariantCulture);
        DebounceTextBox.Text = Settings.DebounceMs.ToString(CultureInfo.InvariantCulture);
        TimeoutTextBox.Text = Settings.RequestTimeoutMs.ToString(CultureInfo.InvariantCulture);
        BlockedTextBox.Text = string.Join(", ", Settings.BlockedProcesses);

        UpdateKeyModeState();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Settings.CloudAssistEnabled = EnabledCheckBox.IsChecked == true;
        Settings.StrictEnabled = StrictCheckBox.IsChecked == true;
        Settings.StrictDedupeLocal = StrictDedupeCheckBox.IsChecked == true;
        Settings.StrictAcceptKey = TagOf(AcceptKeyComboBox, "Oem3");

        Settings.ExtendedEnabled = ExtendedCheckBox.IsChecked == true;
        Settings.ExtendedMaxCount = ParseInt(TagOf(ExtendedCountComboBox, "3"), 3, 1, 4);
        Settings.ExtendedKeyMode = TagOf(ExtendedKeyModeComboBox, "Auto");
        Settings.ExtendedKeys = string.Concat(KeyNames.ParseDigits(ExtendedKeysTextBox.Text).Select(k => (char)k));

        Settings.OverlayPlacement = TagOf(PlacementComboBox, "Below");
        Settings.PopupStyle = TagOf(PopupStyleComboBox, "Card");
        Settings.ShowCloudIcon = ShowCloudIconCheckBox.IsChecked == true;

        Settings.EnglishModeAssistEnabled = EnglishCheckBox.IsChecked == true;
        Settings.EnglishModeCommit = TagOf(EnglishCommitComboBox, "Replace");

        Settings.DoublePinyinMode = DoublePinyinCheckBox.IsChecked == true;
        Settings.FuzzyPinyin = FuzzyCheckBox.IsChecked == true;
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
        UpdateKeyModeState();

        if (!saved)
        {
            SaveStatusText.Text = $"已应用，但写入配置失败：{AppServices.Store.LastError}";
            return;
        }

        var manualKeys = KeyNames.ParseDigits(Settings.ExtendedKeys).Count;
        SaveStatusText.Text = Settings.ExtendedKeyMode == "Manual" && manualKeys < Settings.ExtendedMaxCount
            ? $"已保存；手动键位只有 {manualKeys} 个，扩展最多显示 {manualKeys} 条"
            : "已保存并立即生效";
    }

    private void OnKeyModeChanged(object sender, SelectionChangedEventArgs e) => UpdateKeyModeState();

    private void UpdateKeyModeState()
    {
        if (ExtendedKeysTextBox is null)
        {
            return;
        }

        var manual = string.Equals(
            TagOf(ExtendedKeyModeComboBox, "Auto"),
            "Manual",
            StringComparison.OrdinalIgnoreCase);
        ExtendedKeysTextBox.IsEnabled = manual;
    }

    private static string TagOf(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

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

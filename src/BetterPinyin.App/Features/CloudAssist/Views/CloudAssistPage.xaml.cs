using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterPinyin.App.Shell.Controls;
using BetterPinyin.Core.Settings;

namespace BetterPinyin.App.Features.CloudAssist.Views;

/// <summary>扩展采纳键列表里的一行。</summary>
public sealed class ExtendedKeyItem
{
    /// <summary>创建一行。</summary>
    /// <param name="id">可保存的标识。</param>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <param name="label">界面上显示的键帽。</param>
    public ExtendedKeyItem(string id, int virtualKey, string label)
    {
        Id = id;
        VirtualKey = virtualKey;
        Label = label;
    }

    /// <summary>可保存的标识，例如 <c>D6</c>。</summary>
    public string Id { get; }

    /// <summary>虚拟键码。</summary>
    public int VirtualKey { get; }

    /// <summary>界面上显示的键帽。</summary>
    public string Label { get; }
}

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

    /// <summary>禁用状态的透明度：灰得能看清内容，又能一眼看出点不动。</summary>
    private const double DisabledOpacity = 0.4;

    private readonly CloudAssistFeature _feature;
    private readonly List<string> _diagnosticLines = new();
    private readonly ObservableCollection<ExtendedKeyItem> _extendedKeys = new();

    // 页面由导航缓存，切走再切回会再次触发 Loaded。
    // 只有第一次才把设置读进控件，否则会覆盖用户尚未保存的修改。
    private bool _initialized;

    // 拖动排序用的起点与当前拖动的行。
    private Point _dragStart;
    private ExtendedKeyItem? _dragItem;
    private bool _draggingKeys;

    /// <summary>创建设置页。</summary>
    /// <param name="feature">所属功能模块。</param>
    internal CloudAssistPage(CloudAssistFeature feature)
    {
        _feature = feature;
        InitializeComponent();

        ExtendedKeysList.ItemsSource = _extendedKeys;

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

        StrictKeyBox.KeyId = KeyNames.TryResolve(Settings.StrictAcceptKey, out _)
            ? Settings.StrictAcceptKey
            : "Oem3";

        SelectByTag(ExtendedCountComboBox, Settings.ExtendedMaxCount.ToString(CultureInfo.InvariantCulture));
        SelectByTag(ExtendedKeyModeComboBox, Settings.ExtendedKeyMode);
        SelectByTag(PlacementComboBox, Settings.OverlayPlacement);
        SelectByTag(PopupStyleComboBox, Settings.PopupStyle);
        SelectByTag(EnglishCommitComboBox, Settings.EnglishModeCommit);

        LoadExtendedKeys();
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
        Settings.StrictAcceptKey = string.IsNullOrWhiteSpace(StrictKeyBox.KeyId)
            ? "Oem3"
            : StrictKeyBox.KeyId;

        Settings.ExtendedEnabled = ExtendedCheckBox.IsChecked == true;
        Settings.ExtendedMaxCount = ParseInt(TagOf(ExtendedCountComboBox, "3"), 3, 1, 4);
        Settings.ExtendedKeyMode = TagOf(ExtendedKeyModeComboBox, "Auto");
        Settings.ExtendedKeys = KeyNames.SerializeExtendedKeys(_extendedKeys.Select(item => item.VirtualKey));

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

        SaveStatusText.Text = Settings.ExtendedKeyMode == "Manual" && _extendedKeys.Count == 0
            ? "已保存；手动模式还没有采纳键，扩展云联想不会显示"
            : "已保存并立即生效";
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "将把「云联想」页的所有选项恢复为默认值。\n\n不会影响词库、输入统计、外观与开机启动。",
            "恢复默认设置",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var defaults = new AppSettings();
        Settings.CloudAssistEnabled = defaults.CloudAssistEnabled;
        Settings.StrictEnabled = defaults.StrictEnabled;
        Settings.StrictDedupeLocal = defaults.StrictDedupeLocal;
        Settings.StrictAcceptKey = defaults.StrictAcceptKey;
        Settings.ExtendedEnabled = defaults.ExtendedEnabled;
        Settings.ExtendedMaxCount = defaults.ExtendedMaxCount;
        Settings.ExtendedKeyMode = defaults.ExtendedKeyMode;
        Settings.ExtendedKeys = defaults.ExtendedKeys;
        Settings.OverlayPlacement = defaults.OverlayPlacement;
        Settings.PopupStyle = defaults.PopupStyle;
        Settings.ShowCloudIcon = defaults.ShowCloudIcon;
        Settings.EnglishModeAssistEnabled = defaults.EnglishModeAssistEnabled;
        Settings.EnglishModeCommit = defaults.EnglishModeCommit;
        Settings.DoublePinyinMode = defaults.DoublePinyinMode;
        Settings.FuzzyPinyin = defaults.FuzzyPinyin;
        Settings.MinQueryLength = defaults.MinQueryLength;
        Settings.DebounceMs = defaults.DebounceMs;
        Settings.RequestTimeoutMs = defaults.RequestTimeoutMs;
        Settings.BlockedProcesses = new List<string>(defaults.BlockedProcesses);

        var saved = AppServices.SaveSettings();
        _feature.ApplySettings();
        LoadFromSettings();

        SaveStatusText.Text = saved
            ? "已恢复默认设置"
            : $"已恢复，但写入配置失败：{AppServices.Store.LastError}";
    }

    private void OnKeyModeChanged(object sender, SelectionChangedEventArgs e) => UpdateKeyModeState();

    /// <summary>「键位分配」是否选了手动指定。</summary>
    private bool IsManualKeyMode =>
        string.Equals(TagOf(ExtendedKeyModeComboBox, "Auto"), "Manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按「键位分配」刷新下面几块的可编辑状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 自动模式的键位是「本地候选数 + 显示条数」算出来的，手动列表根本不参与。
    /// 这时如果还能拖动、还能删，用户会以为改动生效了，实际下次查询仍按自动算——所以整块压暗并禁用。
    /// </para>
    /// <para>
    /// 反过来，手动模式下有一个键就是一行，条数没有单独调节的余地：这一项锁掉并跟着列表走，
    /// 增删键位后条数立刻同步，不用再手动对一遍。
    /// </para>
    /// </remarks>
    private void UpdateKeyModeState()
    {
        if (ExtendedKeysList is null || ExtendedCountComboBox is null || ExtendedCountRow is null)
        {
            return;
        }

        var manual = IsManualKeyMode;

        // 只设 IsEnabled 不改变外观，看着还是能点的样子，所以要连透明度一起调。
        ExtendedKeysList.IsEnabled = manual;
        ExtendedKeysList.Opacity = manual ? 1 : DisabledOpacity;
        AddExtendedKeyBox.Opacity = manual ? 1 : DisabledOpacity;
        ExtendedKeysLockedHint.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;

        AddExtendedKeyBox.IsEnabled = manual && _extendedKeys.Count < KeyNames.MaxExtendedKeys;

        if (!manual && _dragItem is not null)
        {
            // 拖到一半切了模式：不放开鼠标捕获的话，指针会被列表一直吃住。
            _dragItem = null;
            _draggingKeys = false;
            ExtendedKeysList.ReleaseMouseCapture();
        }

        ExtendedCountComboBox.IsEnabled = !manual;
        SettingRow.SetDescription(
            ExtendedCountRow,
            manual
                ? "手动指定时这一项跟着采纳键的个数走：有几个键就能显示几条。"
                : "自动模式下最多显示几条扩展候选，采纳键由本地候选数接着往后排。");

        if (manual)
        {
            // 空列表也至少留 1 条：下拉框没有 0 这一项，扩展这时候本来就一条都不显示。
            SelectByTag(
                ExtendedCountComboBox,
                Math.Clamp(_extendedKeys.Count, 1, KeyNames.MaxExtendedKeys)
                    .ToString(CultureInfo.InvariantCulture));
        }
    }

    private void LoadExtendedKeys()
    {
        _extendedKeys.Clear();
        foreach (var virtualKey in KeyNames.ParseExtendedKeys(Settings.ExtendedKeys))
        {
            var id = KeyNames.IdOf(virtualKey);
            if (!string.IsNullOrEmpty(id))
            {
                _extendedKeys.Add(new ExtendedKeyItem(id, virtualKey, KeyNames.Describe(virtualKey)));
            }
        }
    }

    private void OnAddExtendedKeyCaptured(object? sender, KeyCapturedEventArgs e)
    {
        if (_extendedKeys.Count >= KeyNames.MaxExtendedKeys)
        {
            SaveStatusText.Text = $"最多只能设置 {KeyNames.MaxExtendedKeys} 个采纳键。";
            return;
        }

        if (_extendedKeys.Any(item => string.Equals(item.Id, e.KeyId, StringComparison.OrdinalIgnoreCase)))
        {
            SaveStatusText.Text = "这个键已经在列表里了。";
            return;
        }

        // 能走到这里说明当前就是手动模式：自动模式下添加框是禁用的。
        _extendedKeys.Add(new ExtendedKeyItem(e.KeyId, e.VirtualKey, KeyNames.Describe(e.VirtualKey)));
        SaveStatusText.Text = "已添加，记得点「保存并应用」。";
        UpdateKeyModeState();
    }

    private void OnRemoveExtendedKeyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExtendedKeyItem item)
        {
            _extendedKeys.Remove(item);
            SaveStatusText.Text = "已移除，记得点「保存并应用」。";
            UpdateKeyModeState();
        }

        e.Handled = true;
    }

    private void OnExtendedKeysMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        _draggingKeys = false;

        // 自动模式下整块是禁用的，这里再挡一道：禁用元素收不到正常鼠标事件，
        // 但预览事件在命中测试的边界上不一定可靠，别让压暗的列表还能被拖动。
        if (!IsManualKeyMode)
        {
            return;
        }

        // 点在删除按钮上时不启动拖动，否则按钮永远点不中。
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var container = ItemsControl.ContainerFromElement(
            ExtendedKeysList,
            e.OriginalSource as DependencyObject) as ListBoxItem;

        if (container?.DataContext is ExtendedKeyItem item)
        {
            _dragItem = item;
            _dragStart = e.GetPosition(ExtendedKeysList);
            ExtendedKeysList.CaptureMouse();
        }
    }

    private void OnExtendedKeysMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(ExtendedKeysList);
        if (!_draggingKeys &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _draggingKeys = true;

        // 不调用系统 DragDrop：跨进程拖放要经过 OLE，在无显示器/受限会话里可能直接不触发。
        // 这里用鼠标捕获 + 实时换位，行为更可控，也能边拖边看到顺序变化。
        var target = ItemAt(position);
        if (target is null || ReferenceEquals(target, _dragItem))
        {
            return;
        }

        var oldIndex = _extendedKeys.IndexOf(_dragItem);
        var newIndex = _extendedKeys.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        _extendedKeys.Move(oldIndex, newIndex);
        SaveStatusText.Text = "顺序已调整，记得点「保存并应用」。";
        e.Handled = true;
    }

    private void OnExtendedKeysMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem is not null)
        {
            _dragItem = null;
            _draggingKeys = false;
            ExtendedKeysList.ReleaseMouseCapture();
        }
    }

    private void OnExtendedKeysLostCapture(object sender, MouseEventArgs e)
    {
        _dragItem = null;
        _draggingKeys = false;
    }

    private ExtendedKeyItem? ItemAt(Point position)
    {
        var hit = ExtendedKeysList.InputHitTest(position) as DependencyObject;
        var container = ItemsControl.ContainerFromElement(ExtendedKeysList, hit) as ListBoxItem;
        return container?.DataContext as ExtendedKeyItem;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
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

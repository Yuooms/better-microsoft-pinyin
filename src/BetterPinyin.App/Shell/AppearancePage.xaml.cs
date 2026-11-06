using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using BetterPinyin.Core.Settings;
using Microsoft.Win32;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 外观设置页。
/// </summary>
/// <remarks>
/// 主题与背景都是所见即所得的东西，要求「点保存」才生效是反直觉的，所以这一页没有保存按钮：
/// 改动即刻写入配置并应用。透明度用松手落盘的方式，避免拖动时反复写文件。
/// </remarks>
public partial class AppearancePage : Page
{
    private bool _loading;
    private string? _previewPath;

    /// <summary>创建页面。</summary>
    public AppearancePage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFromSettings();
    }

    private static AppSettings Settings => AppServices.Settings;

    private void LoadFromSettings()
    {
        _loading = true;

        SelectByTag(
            ThemeComboBox,
            AppearanceOptions.ToSettingValue(AppearanceOptions.ParseTheme(Settings.ThemeMode)));

        BackgroundToggle.IsChecked = Settings.BackgroundImageEnabled &&
            !string.IsNullOrWhiteSpace(Settings.BackgroundImagePath);
        OpacitySlider.Value = Math.Clamp(Settings.BackgroundImageOpacity, 0.1, 1.0);

        _loading = false;
        UpdateBackgroundUi();
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var theme = AppearanceOptions.ParseTheme(TagOf(ThemeComboBox, "System"));
        Settings.ThemeMode = AppearanceOptions.ToSettingValue(theme);
        AppServices.SaveSettings();

        ThemeManager.SetPreference(theme);
    }

    private void OnBackgroundToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // 打开开关但还没选图时，直接弹选择框，避免出现「开了却什么都不发生」。
        if (BackgroundToggle.IsChecked == true && string.IsNullOrWhiteSpace(Settings.BackgroundImagePath))
        {
            OnBrowseClick(sender, e);
            return;
        }

        Settings.BackgroundImageEnabled = BackgroundToggle.IsChecked == true;
        AppServices.SaveSettings();
        UpdateBackgroundUi();
        RefreshWindow();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        Settings.BackgroundImagePath = dialog.FileName;
        Settings.BackgroundImageEnabled = true;
        AppServices.SaveSettings();

        _loading = true;
        BackgroundToggle.IsChecked = true;
        _loading = false;

        UpdateBackgroundUi();
        RefreshWindow();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Settings.BackgroundImagePath = null;
        Settings.BackgroundImageEnabled = false;
        AppServices.SaveSettings();

        _loading = true;
        BackgroundToggle.IsChecked = false;
        _loading = false;

        UpdateBackgroundUi();
        RefreshWindow();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 加载时设置 Minimum / Maximum 就会触发一次 ValueChanged，
        // 那时后面的 OpacityValueText 还没构造出来，必须挡住。
        if (_loading || OpacityValueText is null)
        {
            return;
        }

        Settings.BackgroundImageOpacity = Math.Round(e.NewValue, 2);
        OpacityValueText.Text = Settings.BackgroundImageOpacity.ToString("P0");
        RefreshWindow();

        // 拖动过程中只预览；松手（失去鼠标捕获）时再落盘。
        if (!OpacitySlider.IsMouseCaptureWithin)
        {
            AppServices.SaveSettings();
        }
    }

    private void OnOpacityCaptureLost(object sender, MouseEventArgs e)
    {
        if (!_loading && OpacityValueText is not null)
        {
            AppServices.SaveSettings();
        }
    }

    private void UpdateBackgroundUi()
    {
        var path = Settings.BackgroundImagePath;
        var hasImage = !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        BackgroundPathTextBox.Text = path ?? string.Empty;
        OpacityValueText.Text = Settings.BackgroundImageOpacity.ToString("P0");

        var usable = hasImage && BackgroundToggle.IsChecked == true;
        OpacitySlider.IsEnabled = usable;
        ClearButton.IsEnabled = hasImage;

        UpdatePreview(hasImage ? path : null);
    }

    private void UpdatePreview(string? path)
    {
        if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _previewPath = path;

        if (path is null)
        {
            PreviewImage.Source = null;
            PreviewHint.Text = "未选择图片";
            PreviewHint.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            PreviewImage.Source = image;
            PreviewHint.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            PreviewImage.Source = null;
            PreviewHint.Text = "图片无法读取";
            PreviewHint.Visibility = Visibility.Visible;
        }
    }

    private void RefreshWindow() =>
        (Window.GetWindow(this) as SettingsWindow)?.RefreshBackground();

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

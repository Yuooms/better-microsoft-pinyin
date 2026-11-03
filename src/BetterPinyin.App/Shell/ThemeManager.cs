using System.Windows;
using System.Windows.Media;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 主题切换。
/// </summary>
/// <remarks>
/// <para>
/// 配色字典固定放在 <see cref="Application.Resources"/> 的 <c>MergedDictionaries[0]</c>，
/// 切换主题时整体替换这一项。整项替换才会触发 WPF 的失效链，让所有
/// <c>DynamicResource</c> 引用重新求值；只改单个 key 或往别的字典里塞值都不会刷新。
/// </para>
/// <para>
/// 因此全项目的颜色属性必须用 <c>DynamicResource</c>。样式本身（<c>Style="{StaticResource ...}"</c>）
/// 可以保持静态：样式对象不随主题变。
/// </para>
/// </remarks>
internal static class ThemeManager
{
    /// <summary>配色字典在合并列表里的固定索引。</summary>
    private const int ColorDictionaryIndex = 0;

    private static readonly string[] RequiredKeys =
    {
        "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush", "TextDisabledBrush", "TextOnAccentBrush",
        "WindowBackgroundBrush", "SurfaceBrush", "SurfaceAltBrush", "SurfaceHoverBrush",
        "BorderBrush", "BorderStrongBrush", "DividerBrush",
        "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentSoftBrush",
        "ControlFillBrush", "ControlStrokeBrush", "ControlStrokeBottomBrush", "ControlFillDisabledBrush",
        "FlyoutBackgroundBrush", "FlyoutStrokeBrush",
        "ShellGlassBrush", "ContentGlassBrush",
        "SuccessBrush", "WarningBrush", "ErrorBrush",
        "PopupBackgroundBrush", "PopupStrokeBrush", "PopupKeyChipBrush",
        "AcrylicTint",
    };

    private static bool _initialized;

    /// <summary>主题切换完成（界面已经重新着色）。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>当前主题偏好。</summary>
    public static AppTheme Preference { get; private set; } = AppTheme.System;

    /// <summary>当前实际是否为深色。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>是否运行在 Windows 11（build &gt;= 22000，决定圆角与控件高度）。</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// 启动时调用一次，必须在创建任何窗口之前。
    /// </summary>
    /// <param name="preference">主题偏好。</param>
    public static void Initialize(AppTheme preference)
    {
        Preference = preference;
        ApplyMetrics();
        IsDark = ResolveDark(preference);
        ApplyColorDictionary(IsDark);
        _initialized = true;
    }

    /// <summary>运行期切换主题。</summary>
    /// <param name="preference">新的偏好。</param>
    public static void SetPreference(AppTheme preference)
    {
        Preference = preference;
        var dark = ResolveDark(preference);
        if (_initialized && dark == IsDark)
        {
            return;
        }

        IsDark = dark;
        ApplyColorDictionary(dark);
        _initialized = true;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>系统主题变化后调用；只在偏好为「跟随系统」时生效。</summary>
    public static void OnSystemThemeChanged()
    {
        if (Preference != AppTheme.System)
        {
            return;
        }

        var dark = ResolveDark(AppTheme.System);
        if (!_initialized || dark == IsDark)
        {
            return;
        }

        IsDark = dark;
        ApplyColorDictionary(dark);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>按 key 取当前主题下的画刷，取不到返回 null。</summary>
    /// <param name="key">资源键。</param>
    /// <returns>画刷。</returns>
    public static Brush? ResolveBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    /// <summary>按 key 取当前主题下的颜色，取不到返回兜底值。</summary>
    /// <param name="key">资源键。</param>
    /// <param name="fallback">兜底颜色。</param>
    /// <returns>颜色。</returns>
    public static Color ResolveColor(string key, Color fallback) =>
        ResolveBrush(key) is SolidColorBrush brush ? brush.Color : fallback;

    /// <summary>
    /// 启动自检：必需键缺失时写日志。
    /// </summary>
    /// <returns>全部存在返回 true。</returns>
    /// <remarks>
    /// <c>DynamicResource</c> 找不到键不会抛异常，只会静默保留原值——界面上表现为
    /// 「某个字还是黑的」。没有这道自检，这类问题只能靠肉眼发现。
    /// </remarks>
    public static bool Validate()
    {
        var missing = new List<string>();
        foreach (var key in RequiredKeys)
        {
            if (Application.Current?.TryFindResource(key) is null)
            {
                missing.Add(key);
            }
        }

        if (missing.Count == 0)
        {
            Log.Write($"主题资源自检通过（{RequiredKeys.Length} 个键）");
            return true;
        }

        Log.Write($"主题资源缺失：{string.Join(", ", missing)}");
        return false;
    }

    private static bool ResolveDark(AppTheme preference) => preference switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => !SystemTheme.IsAppsLightTheme(),
    };

    private static void ApplyColorDictionary(bool dark)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        if (dictionaries.Count <= ColorDictionaryIndex)
        {
            Log.Write("主题字典缺失：App.Resources 的合并列表为空");
            return;
        }

        var source = dark ? "Themes/Colors.Dark.xaml" : "Themes/Colors.Light.xaml";
        dictionaries[ColorDictionaryIndex] = new ResourceDictionary
        {
            Source = new Uri(source, UriKind.Relative),
        };
    }

    /// <summary>Win10 用更小的圆角与控件高度，与系统一致。</summary>
    private static void ApplyMetrics()
    {
        if (IsWindows11)
        {
            return;
        }

        var metrics = FindMetricsDictionary();
        if (metrics is null)
        {
            return;
        }

        metrics["ControlCornerRadius"] = new CornerRadius(2);
        metrics["CardCornerRadius"] = new CornerRadius(2);
        metrics["FlyoutCornerRadius"] = new CornerRadius(4);
        metrics["ControlHeight"] = 30d;
        metrics["RowMinHeight"] = 48d;
        metrics["PagePadding"] = new Thickness(28, 20, 28, 28);
        metrics["ScrollBarThickness"] = 14d;
    }

    private static ResourceDictionary? FindMetricsDictionary()
    {
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null)
        {
            return null;
        }

        foreach (var dictionary in dictionaries)
        {
            if (dictionary.Source?.OriginalString.Contains("Metrics.xaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                return dictionary;
            }
        }

        return null;
    }
}

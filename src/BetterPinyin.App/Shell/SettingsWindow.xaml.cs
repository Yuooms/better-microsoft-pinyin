using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPinyin.App.Features.CloudAssist;
using BetterPinyin.Core.Features;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using UiNavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 设置窗口。
/// </summary>
/// <remarks>
/// <para>
/// 左侧导航由 <see cref="IFeatureModule.SettingsPages"/> 推导：
/// 新增一个功能模块，导航里就自动多一页，不需要改本文件。
/// 「常规」「外观」「网络与环境」「关于」属于外壳，在构造里直接追加。
/// </para>
/// <para>
/// 页面由 <see cref="SettingsPageProvider"/> 缓存。WPF-UI 一旦设了页面提供器，
/// 就不再使用自己的 <c>NavigationCacheMode</c>，切页保留输入要靠提供器实现。
/// </para>
/// </remarks>
public partial class SettingsWindow : FluentWindow, INavigationWindow
{
    private readonly SettingsPageProvider _pageProvider = new();
    private readonly Dictionary<string, Type> _pageTypes = new(StringComparer.Ordinal);
    private readonly INavigationService _navigation;

    private Type? _pendingPageType;
    private bool _navigationReady;

    // 透明度调整只改 Opacity，不必每拖动一格就重新解码整张图。
    private string? _loadedBackgroundPath;

    /// <summary>创建设置窗口。</summary>
    /// <param name="registry">功能模块注册表。</param>
    public SettingsWindow(FeatureRegistry registry)
    {
        InitializeComponent();
        RefreshBackground();

        // 主题桥与 WPF-UI 的 WindowBackgroundManager 都以 MainWindow 为准。
        // 这里显式登记，避免候选浮窗先创建时被误当成主窗口。
        Application.Current.MainWindow = this;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"版本 {version?.ToString(3) ?? "0.1.0"}";

        _navigation = new NavigationService(_pageProvider);
        _navigation.SetNavigationControl(RootNavigation);

        // 顺序即导航顺序：常规在最前，功能模块按注册顺序排在中间，
        // 外壳的外观与页脚项收在最后。功能模块自己无法决定排在哪，
        // 只能由外壳决定，所以「常规」必须在这里先加。
        AddPage("常规", "\uE713", typeof(GeneralPage), () => new GeneralPage(), footer: false);

        foreach (var page in registry.AllSettingsPages)
        {
            AddPage(page.Title, page.Glyph, page.ViewType, page.CreateView, footer: false);
        }

        AddPage("外观", "\uE790", typeof(AppearancePage), () => new AppearancePage(), footer: false);
        AddPage("网络与环境", "\uE774", typeof(NetworkPage), () => new NetworkPage(), footer: true);
        AddPage("关于", "\uE946", typeof(AboutPage), () => new AboutPage(), footer: true);

        Loaded += OnLoaded;
        SourceInitialized += (_, _) => RefreshWindowMaterial();

        // 切主题时材质本身也要换色（Mica 的染色由 DWM 按深浅色算），
        // 而且 WPF-UI 会在 ApplicationThemeManager.Apply 里把窗口背景刷回实色，
        // 必须在那之后再接管一次。订阅顺序保证 App 的处理器先跑完。
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;

        // 键盘钩子是全局的：在设置页里打字会进入组字缓冲，切走之后会带着残留去查询。
        // 进入/离开设置窗口都收口一次，窗口边界就闭合了。
        Activated += (_, _) => AppServices.Controller?.DismissInput();
        Deactivated += (_, _) => AppServices.Controller?.DismissInput();
    }

    /// <summary>
    /// 按当前设置重新加载窗口背景图。
    /// </summary>
    /// <remarks>
    /// 文件缺失或解码失败时退回纯色，不弹窗：用户可能只是暂时拔掉了移动硬盘。
    /// 用 <see cref="BitmapCacheOption.OnLoad"/> 读完即释放文件句柄，方便之后再换图或删除。
    /// </remarks>
    public void RefreshBackground()
    {
        // 背景图的开关会影响内容区那层底色是不是要让开，先同步一次材质。
        RefreshWindowMaterial();

        var settings = AppServices.Settings;

        if (!settings.BackgroundImageEnabled || string.IsNullOrWhiteSpace(settings.BackgroundImagePath))
        {
            ClearBackground();
            return;
        }

        try
        {
            var path = Path.GetFullPath(settings.BackgroundImagePath);
            if (!File.Exists(path))
            {
                Log.Write($"背景图不存在，已回退纯色：{path}");
                ClearBackground();
                return;
            }

            if (!string.Equals(_loadedBackgroundPath, path, StringComparison.OrdinalIgnoreCase))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();

                BackgroundImage.Source = image;
                _loadedBackgroundPath = path;
            }

            BackgroundImage.Opacity = Math.Clamp(settings.BackgroundImageOpacity, 0.05, 1.0);
            BackgroundImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            Log.Write($"背景图加载失败，已回退纯色：{ex.GetType().Name} {ex.Message}");
            ClearBackground();
        }
    }

    private void ClearBackground()
    {
        BackgroundImage.Source = null;
        BackgroundImage.Visibility = Visibility.Collapsed;
        _loadedBackgroundPath = null;
    }

    /// <summary>定位到指定标题的页面（用于从托盘或命令行直接打开某一页）。</summary>
    /// <param name="title">页面标题。</param>
    public void SelectPage(string title)
    {
        if (!_pageTypes.TryGetValue(title, out var pageType))
        {
            Log.Write($"未找到设置页：{title}");
            return;
        }

        if (_navigationReady)
        {
            _navigation.Navigate(pageType);
        }
        else
        {
            // 窗口还没 Loaded，导航控件模板尚未应用，先记下来。
            _pendingPageType = pageType;
        }
    }

    /// <inheritdoc />
    public INavigationView GetNavigation() => RootNavigation;

    /// <inheritdoc />
    public bool Navigate(Type pageType) => _navigation.Navigate(pageType);

    /// <inheritdoc />
    public void SetServiceProvider(IServiceProvider serviceProvider)
    {
        // 本程序不使用依赖注入，页面从 FeatureRegistry 的工厂取。
    }

    /// <inheritdoc />
    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) =>
        RootNavigation.SetPageProviderService(navigationViewPageProvider);

    /// <inheritdoc />
    public void ShowWindow() => Show();

    /// <inheritdoc />
    public void CloseWindow() => Close();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _navigationReady = true;

        // WPF-UI 在模板应用 / 主题刷新时会把窗口背景刷回 ApplicationBackgroundBrush，
        // 覆盖掉 SourceInitialized 里设的透明与材质，所以第一次布局完成后再接管一次。
        RefreshWindowMaterial();

        var target = _pendingPageType ?? _pageTypes.Values.FirstOrDefault();
        if (target is not null)
        {
            _navigation.Navigate(target);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => RefreshWindowMaterial();

    /// <summary>
    /// 按设置应用窗口材质。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 材质走 WPF-UI 自己的 <c>WindowBackdropType</c>：它会同时把窗口背景刷成透明、
    /// 设好 DWM 属性，并在切主题后自己重刷一遍。自己用 DwmSetWindowAttribute 试过一版，
    /// 窗口表面仍然被当作不透明（壁纸透不过来）——缺的是 WPF-UI 在 FluentWindow 模板里
    /// 配套做的那几步，所以这里不再重复实现。
    /// </para>
    /// <para>
    /// 透明不等于可读：Mica 只是把壁纸去饱和、调亮度，深色壁纸配深色文字会糊成一片。
    /// 所以在窗口最底层铺一层 60% 的 <c>WindowGlassBrush</c> 着色，正文压在上面对比度才稳定；
    /// 背景图在这层之上，不会被它盖住。
    /// </para>
    /// </remarks>
    public void RefreshWindowMaterial()
    {
        var mode = AppearanceOptions.ParseBackdropMode(AppServices.Settings.BackdropMode);

        // 只认 DWMSBT（22621+）：更早的 Windows 11 与 Windows 10 走的是未公开的 accent 实现，
        // 拖动时逐帧重算整窗模糊，放在设置窗口上会卡得很难受，宁可继续用纯色。
        var useMaterial = mode != BackdropMode.Solid && Backdrop.SupportsSystemBackdrop;

        var settings = AppServices.Settings;
        var hasBackgroundImage = settings.BackgroundImageEnabled &&
            !string.IsNullOrWhiteSpace(settings.BackgroundImagePath);

        WindowBackdropType = useMaterial ? WindowBackdropType.Mica : WindowBackdropType.None;

        // 属性回调只在值变化时触发，而窗口在 SourceInitialized 时还没 Loaded，
        // 那一次会被 WPF-UI 跳过；值没变又不会再触发，所以这里显式刷一次。
        if (IsLoaded)
        {
            Wpf.Ui.Appearance.WindowBackgroundManager.UpdateBackground(
                this,
                Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme(),
                WindowBackdropType);
        }

        MaterialTint.Visibility = useMaterial ? Visibility.Visible : Visibility.Collapsed;

        // 显式写一遍底色：WindowBackdropType 没变化时属性回调不会触发，
        // 只靠 WPF-UI 的话从「纯色」切回「材质」可能留下上一次的实色底。
        Background = useMaterial
            ? Brushes.Transparent
            : (Brush)FindResource("WindowBackgroundBrush");

        // 导航与页面必须透明，否则会把窗口底下的材质和背景图盖住。
        // NavigationView 的模板里另有一层 NavigationViewContentBackground（内容区底色），
        // 光设控件自身的 Background 盖不掉它——那是模板里独立的一个 Border。
        RootNavigation.Background = Brushes.Transparent;
        // 设了背景图时内容区完全让开，否则 85% 的白底会把图洗得几乎看不见；
        // 这时可读性由用户自己调「透明度」控制（越低文字越清楚）。
        if (hasBackgroundImage)
        {
            RootNavigation.Resources["NavigationViewContentBackground"] = Brushes.Transparent;
        }
        else if (useMaterial)
        {
            // 内容区比导航更实一点：导航只有短标签，正文是长段落，对比度要求不一样。
            RootNavigation.Resources["NavigationViewContentBackground"] = FindResource("ContentGlassBrush");
        }
        else
        {
            RootNavigation.Resources.Remove("NavigationViewContentBackground");
        }

        Log.Write($"窗口材质：{(useMaterial ? "Mica" : "纯色")}，系统 {Environment.OSVersion.Version.Build}");
    }

    private void AddPage(string title, string glyph, Type pageType, Func<object> factory, bool footer)
    {
        _pageTypes[title] = pageType;
        _pageProvider.Register(pageType, factory);

        var item = new UiNavigationViewItem
        {
            Content = title,
            TargetPageType = pageType,
            Icon = CreateIcon(glyph),
        };

        if (footer)
        {
            RootNavigation.FooterMenuItems.Add(item);
        }
        else
        {
            RootNavigation.MenuItems.Add(item);
        }
    }

    /// <summary>
    /// 把 Segoe MDL2 字形映射到 WPF-UI 的图标字体。
    /// </summary>
    /// <remarks>
    /// 模块只声明字形码位，不引用 UI 库；这里做一层兜底，
    /// 未知字形继续用系统字体渲染，不会变成空方块。
    /// </remarks>
    private static IconElement CreateIcon(string glyph) =>
        glyph switch
        {
            "\uE753" => new SymbolIcon(SymbolRegular.Cloud24, 16),
            "\uE790" => new SymbolIcon(SymbolRegular.Color24, 16),
            "\uE774" => new SymbolIcon(SymbolRegular.Globe24, 16),
            "\uE946" => new SymbolIcon(SymbolRegular.Info24, 16),
            _ => new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
            },
        };
}

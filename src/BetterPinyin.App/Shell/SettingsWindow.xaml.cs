using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using BetterPinyin.App.Features.CloudAssist;
using BetterPinyin.Core.Features;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 设置窗口。
/// </summary>
/// <remarks>
/// 左侧导航完全由 <see cref="IFeatureModule.SettingsPages"/> 推导：
/// 新增一个功能模块，导航里就自动多一页，不需要改本文件。
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly List<NavItem> _items = new();

    /// <summary>创建设置窗口。</summary>
    /// <param name="registry">功能模块注册表。</param>
    public SettingsWindow(FeatureRegistry registry)
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"版本 {version?.ToString(3) ?? "0.1.0"}";

        foreach (var page in registry.AllSettingsPages)
        {
            _items.Add(new NavItem(page.Title, page.Glyph, page.Description, page.CreateView));
        }

        _items.Add(new NavItem("网络与环境", "\uE774", "端点体检与环境自检", () => new NetworkPage()));
        _items.Add(new NavItem("关于", "\uE946", "项目说明", () => new AboutPage()));

        SourceInitialized += (_, _) =>
        {
            var enabled = Backdrop.TryApplyAcrylic(this, 0x9AFFFFFF);
            Log.Write(enabled ? "设置窗口已启用亚克力背景" : "亚克力不可用，设置窗口使用实色背景");
        };

        NavList.ItemsSource = _items;
        if (_items.Count > 0)
        {
            NavList.SelectedIndex = 0;
        }
    }

    /// <summary>定位到指定标题的页面（用于从托盘直接打开某一页）。</summary>
    /// <param name="title">页面标题。</param>
    public void SelectPage(string title)
    {
        var index = _items.FindIndex(item => string.Equals(item.Title, title, StringComparison.Ordinal));
        if (index >= 0)
        {
            NavList.SelectedIndex = index;
        }
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavItem item)
        {
            return;
        }

        ContentHost.Content = item.CreateView();
    }

    /// <summary>导航项。</summary>
    private sealed class NavItem
    {
        public NavItem(string title, string glyph, string? description, Func<object> factory)
        {
            Title = title;
            Glyph = glyph;
            Description = description;
            CreateView = factory;
        }

        public string Title { get; }

        public string Glyph { get; }

        public string? Description { get; }

        public Func<object> CreateView { get; }

        public override string ToString() => Title;
    }
}

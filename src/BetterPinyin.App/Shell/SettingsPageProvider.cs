using Wpf.Ui.Abstractions;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 设置页工厂与缓存。
/// </summary>
/// <remarks>
/// <para>
/// WPF-UI 的 <c>NavigationCacheMode</c> 只在「没有页面提供器」时才会被用到：
/// 一旦通过 <c>SetPageProviderService</c> 设了提供器，导航就直接向这里要实例。
/// 因此缓存必须由本类负责——同一页面只创建一次，切走再切回时保留未保存的输入。
/// </para>
/// <para>
/// 页面实例是常驻的，所以页面里订阅的事件必须在卸载时退订，
/// 否则切页次数越多，重复回调越多。
/// </para>
/// </remarks>
internal sealed class SettingsPageProvider : INavigationViewPageProvider
{
    private readonly Dictionary<Type, Func<object>> _factories = new();
    private readonly Dictionary<Type, object> _cache = new();

    /// <summary>登记一个页面类型及其工厂。</summary>
    /// <param name="pageType">页面类型，用作导航键。</param>
    /// <param name="factory">创建实例的工厂，只在首次访问时调用。</param>
    public void Register(Type pageType, Func<object> factory)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        ArgumentNullException.ThrowIfNull(factory);

        _factories[pageType] = factory;
    }

    /// <inheritdoc />
    public object? GetPage(Type pageType)
    {
        if (_cache.TryGetValue(pageType, out var cached))
        {
            return cached;
        }

        if (!_factories.TryGetValue(pageType, out var factory))
        {
            Log.Write($"导航请求了未登记的设置页：{pageType.Name}");
            return null;
        }

        var page = factory();
        _cache[pageType] = page;
        return page;
    }
}

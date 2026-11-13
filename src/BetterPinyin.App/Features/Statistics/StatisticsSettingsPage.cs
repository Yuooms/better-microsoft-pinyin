using BetterPinyin.App.Features.Statistics.Views;
using BetterPinyin.Core.Features;

namespace BetterPinyin.App.Features.Statistics;

/// <summary>
/// 「输入统计」设置页的声明。
/// </summary>
/// <remarks>
/// 只需实现 <see cref="ISettingsPage"/> 并挂到模块的 <c>SettingsPages</c> 上，
/// 设置窗口的导航会自己多出一项，不需要改外壳代码。
/// </remarks>
internal sealed class StatisticsSettingsPage : ISettingsPage
{
    private readonly StatisticsFeature _feature;

    /// <summary>创建设置页声明。</summary>
    /// <param name="feature">所属功能模块。</param>
    public StatisticsSettingsPage(StatisticsFeature feature)
    {
        _feature = feature;
    }

    /// <inheritdoc />
    public string Id => "input-stats.settings";

    /// <inheritdoc />
    public string Title => "输入统计";

    /// <inheritdoc />
    public string Glyph => "\uE9D9";

    /// <inheritdoc />
    public string? Description => "查看每天、每小时的输入量，按程序排行";

    /// <inheritdoc />
    public Type ViewType => typeof(StatisticsPage);

    /// <inheritdoc />
    public object CreateView() => new StatisticsPage(_feature);
}

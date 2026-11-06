using BetterPinyin.Core.Features;
using BetterPinyin.App.Features.CloudAssist.Views;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>
/// 云联想的设置页声明。
/// </summary>
internal sealed class CloudAssistSettingsPage : ISettingsPage
{
    private readonly CloudAssistFeature _feature;

    public CloudAssistSettingsPage(CloudAssistFeature feature)
    {
        _feature = feature;
    }

    /// <inheritdoc />
    public string Id => "cloud-assist.settings";

    /// <inheritdoc />
    public string Title => "云联想";

    /// <inheritdoc />
    public string Glyph => "\uE753";

    /// <inheritdoc />
    public string? Description => "配置云候选的触发、展示与采纳方式";

    /// <inheritdoc />
    public Type ViewType => typeof(CloudAssistPage);

    /// <inheritdoc />
    public object CreateView() => new CloudAssistPage(_feature);
}

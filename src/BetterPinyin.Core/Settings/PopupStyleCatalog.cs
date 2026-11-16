namespace BetterPinyin.Core.Settings;

/// <summary>
/// 候选浮窗的样式标识。
/// </summary>
/// <remarks>
/// 两种样式都只改变外观，不改变浮窗的无焦点、不挡鼠标等行为。
/// 配置里存字符串而不是枚举：JSON 可读、可手工编辑，未知值也能按系统默认降级。
/// </remarks>
public static class PopupStyleCatalog
{
    /// <summary>纯白卡片：圆角实色，贴近 Windows 11 输入法的候选条。</summary>
    public const string Card = "Card";

    /// <summary>经典：直角实色，贴近 Windows 10 输入法的候选条。</summary>
    public const string Classic = "Classic";

    /// <summary>旧版本里「亚克力」样式的配置值，仅用于读取兼容。</summary>
    private const string LegacyAcrylic = "Acrylic";

    /// <summary>
    /// 当前系统的默认样式：Windows 11 用纯白卡片，Windows 10 用经典。
    /// </summary>
    /// <remarks>
    /// 判据与系统材质一致（build 22000）。Win10 的候选条本身是直角实色，
    /// 用圆角卡片会显得比输入法本身更「浮」；Win11 则相反。
    /// </remarks>
    public static string SystemDefault =>
        Environment.OSVersion.Version.Build >= 22000 ? Card : Classic;

    /// <summary>
    /// 把配置值归一化为受支持的样式。
    /// </summary>
    /// <param name="value">配置值。</param>
    /// <returns>样式标识。</returns>
    /// <remarks>
    /// 旧版本把亚克力存成 <c>Acrylic</c>。该样式已经改成 Windows 10 输入法那样的
    /// 直角实色，因此读取时直接映射到 <see cref="Classic"/>，用户不需要重新选一次；
    /// 旧版本里选过纯白卡片的配置保持不动。
    /// </remarks>
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, Card, StringComparison.OrdinalIgnoreCase))
        {
            return Card;
        }

        if (string.Equals(trimmed, Classic, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, LegacyAcrylic, StringComparison.OrdinalIgnoreCase))
        {
            return Classic;
        }

        return SystemDefault;
    }

    /// <summary>该配置值是否使用经典（直角实色）样式。</summary>
    /// <param name="value">配置值。</param>
    /// <returns>是否经典样式。</returns>
    public static bool IsClassic(string? value) =>
        string.Equals(Normalize(value), Classic, StringComparison.OrdinalIgnoreCase);
}

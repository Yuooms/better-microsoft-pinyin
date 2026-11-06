namespace BetterPinyin.App.Shell;

/// <summary>配色主题。</summary>
internal enum AppTheme
{
    /// <summary>跟随系统。</summary>
    System,

    /// <summary>浅色。</summary>
    Light,

    /// <summary>深色。</summary>
    Dark,
}

/// <summary>窗口背景策略。</summary>
internal enum BackdropMode
{
    /// <summary>按系统能力自动选择：Win11 用系统材质，Win10 用纯色。</summary>
    Auto,

    /// <summary>始终纯色。</summary>
    Solid,

    /// <summary>强制尝试亚克力（Win10 上可能导致拖动掉帧）。</summary>
    Acrylic,
}

/// <summary>
/// 外观设置的字符串解析。
/// </summary>
/// <remarks>
/// 设置文件用字符串而不是枚举：JSON 里可读、可手工编辑，未知值也能自然降级到默认分支。
/// 解析集中在这里，避免页面、窗口、背景三处各写一份 <c>string.Equals</c>。
/// </remarks>
internal static class AppearanceOptions
{
    /// <summary>解析主题偏好。</summary>
    /// <param name="value">配置值。</param>
    /// <returns>无法识别时按「跟随系统」处理。</returns>
    public static AppTheme ParseTheme(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };

    /// <summary>把主题偏好写回配置值。</summary>
    /// <param name="theme">主题。</param>
    /// <returns>配置字符串。</returns>
    public static string ToSettingValue(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => "System",
    };

    /// <summary>当前是否应该启用玻璃效果。</summary>
    /// <param name="mode">背景策略。</param>
    /// <returns>是否启用。</returns>
    /// <remarks>
    /// Win11 的材质由合成器绘制，拖动时不重算，代价很低；Win10 的亚克力是未公开实现，
    /// 每帧都要对整窗重新模糊，拖动会明显掉帧，所以自动模式下只在 Win11 启用。
    /// </remarks>
    public static bool ShouldUseGlass(BackdropMode mode) => mode switch
    {
        BackdropMode.Solid => false,
        BackdropMode.Acrylic => true,
        _ => Environment.OSVersion.Version.Build >= 22000,
    };
}

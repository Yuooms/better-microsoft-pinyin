namespace BetterPinyin.App.Shell;

/// <summary>
/// 窗口材质能力探测。
/// </summary>
/// <remarks>
/// 设置窗口的 Mica 由 WPF-UI 的 <c>WindowBackdropType</c> 应用，这里只回答
/// 「系统是否支持 <c>DWMWA_SYSTEMBACKDROP_TYPE</c>」，供设置页决定要不要给出材质开关。
/// 候选浮窗已经不再使用系统材质：两种样式都是分层窗口 + WPF 自绘实色，
/// 见 <see cref="Features.CloudAssist.CloudCandidateWindow"/>。
/// </remarks>
internal static class Backdrop
{
    /// <summary>系统是否支持 <c>DWMWA_SYSTEMBACKDROP_TYPE</c>（Windows 11 22H2+）。</summary>
    public static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;
}

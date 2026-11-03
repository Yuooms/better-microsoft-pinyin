using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Shell;

/// <summary>窗口背景类型。</summary>
internal enum BackdropKind
{
    /// <summary>实色。</summary>
    Solid,

    /// <summary>Mica（Win11 的系统材质）。</summary>
    Mica,

    /// <summary>亚克力。</summary>
    Acrylic,
}

/// <summary>背景应用结果。</summary>
/// <param name="Kind">最终使用的类型。</param>
/// <param name="Applied">是否真的启用了材质；false 表示没有改动任何合成状态，或已完整回滚。</param>
/// <param name="Detail">诊断说明。</param>
internal readonly record struct BackdropResult(BackdropKind Kind, bool Applied, string Detail);

/// <summary>
/// 窗口背景材质。
/// </summary>
/// <remarks>
/// <para>
/// Windows 11（22621+）走公开的 <c>DWMWA_SYSTEMBACKDROP_TYPE</c>：设置窗口用 Mica、
/// 云浮窗用 Acrylic，这正是系统「设置」自己的选择。Windows 10 只有未公开的
/// <c>SetWindowCompositionAttribute</c>，而且它会让 DWM 在拖动时逐帧重算整窗模糊，
/// 窗口会明显拖不动——所以自动模式下 Win10 一律走实色，只有用户显式选择才启用。
/// </para>
/// <para>
/// 失败时必须把 <c>CompositionTarget.BackgroundColor</c> 与边框扩展一起还原。
/// 只还原 <see cref="Window.Background"/> 会留下"透明但没模糊"的窗口，直接透出桌面。
/// </para>
/// </remarks>
internal static class Backdrop
{
    private const int DwmSystemBackdropType = 38;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    private const int BackdropNone = 1;
    private const int BackdropMica = 2;
    private const int BackdropAcrylic = 3;

    private static bool _failureLogged;

    /// <summary>系统是否支持 <c>DWMWA_SYSTEMBACKDROP_TYPE</c>。</summary>
    public static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// 应用窗口背景。
    /// </summary>
    /// <param name="window">目标窗口，必须已创建句柄。</param>
    /// <param name="mode">背景策略。</param>
    /// <param name="dark">是否深色主题。</param>
    /// <param name="isFlyout">是否为浮层（用 Acrylic 而不是 Mica）。</param>
    /// <returns>结果；<see cref="BackdropResult.Applied"/> 为 false 时调用方应使用实色。</returns>
    public static BackdropResult Apply(Window window, BackdropMode mode, bool dark, bool isFlyout = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return new BackdropResult(BackdropKind.Solid, false, "窗口句柄无效");
        }

        // 标题栏与材质无关，任何模式下都要设，否则深色内容配浅色标题栏很割裂。
        ApplyDarkTitleBar(window, dark);

        if (!AppearanceOptions.ShouldUseGlass(mode))
        {
            return new BackdropResult(BackdropKind.Solid, false, "未启用材质");
        }

        var build = Environment.OSVersion.Version.Build;
        if (build >= 22621)
        {
            return ApplySystemBackdrop(handle, dark, isFlyout);
        }

        return ApplyAccent(handle);
    }

    /// <summary>设置深色标题栏。</summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="dark">是否深色。</param>
    /// <returns>系统是否接受。</returns>
    public static bool ApplyDarkTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var attribute = Environment.OSVersion.Version.Build >= 18985
            ? DwmUseImmersiveDarkMode
            : DwmUseImmersiveDarkModeLegacy;

        var value = dark ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) != 0)
        {
            return false;
        }

        // 部分 19041/19042 需要一次 SWP_FRAMECHANGED 才会重绘非客户区。
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        // Win10 在运行中切换深色时，光靠 SWP_FRAMECHANGED 有时不会重画标题栏，
        // 再显式要求一次非客户区立即重绘。
        NativeMethods.RedrawWindow(
            handle,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_FRAME);

        return true;
    }

    /// <summary>给窗口加系统圆角（Win11）。</summary>
    /// <param name="window">目标窗口。</param>
    public static void ApplyRoundedCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        var preference = 2; // DWMWCP_ROUND
        NativeMethods.DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>给窗口加圆角区域（Win10 没有系统圆角，用区域裁剪）。</summary>
    /// <param name="handle">窗口句柄。</param>
    /// <param name="width">宽（物理像素）。</param>
    /// <param name="height">高（物理像素）。</param>
    /// <param name="radius">圆角半径（物理像素）。</param>
    public static void ApplyRoundedRegion(IntPtr handle, int width, int height, int radius)
    {
        if (handle == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        if (Environment.OSVersion.Version.Build >= 22000)
        {
            // Win11 用 DWM 圆角，区域裁剪会把阴影切掉。
            var preference = 2;
            NativeMethods.DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
            return;
        }

        var region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
        if (region != IntPtr.Zero)
        {
            NativeMethods.SetWindowRgn(handle, region, true);
        }
    }

    private static BackdropResult ApplySystemBackdrop(IntPtr handle, bool dark, bool isFlyout)
    {
        // Win11 不需要（也不应该）扩边框：那会把标题栏也变成玻璃，深色标题栏被打乱。
        var source = HwndSource.FromHwnd(handle);
        var original = source?.CompositionTarget?.BackgroundColor;
        if (source?.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        var kind = isFlyout ? BackdropAcrylic : BackdropMica;
        if (NativeMethods.DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref kind, sizeof(int)) == 0)
        {
            var preference = 2;
            NativeMethods.DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
            return new BackdropResult(
                isFlyout ? BackdropKind.Acrylic : BackdropKind.Mica,
                true,
                isFlyout ? "DWMSBT_TRANSIENTWINDOW" : "DWMSBT_MAINWINDOW");
        }

        if (source?.CompositionTarget is not null && original is { } color)
        {
            source.CompositionTarget.BackgroundColor = color;
        }

        LogOnce("Windows 11 系统材质不可用，退回实色背景");
        return new BackdropResult(BackdropKind.Solid, false, "DWMSBT 失败");
    }

    private static BackdropResult ApplyAccent(IntPtr handle)
    {
        var source = HwndSource.FromHwnd(handle);
        var original = source?.CompositionTarget?.BackgroundColor;
        if (source?.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        // 只有 Win10 的 accent 路径需要把整个客户区扩成玻璃帧；失败时一并归零。
        var margins = new NativeMethods.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(handle, ref margins);

        // tint 每次现取，不缓存：切主题后着色要跟着变。
        var tint = ThemeManager.ResolveColor("AcrylicTint", Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
        if (TryApplyAccent(handle, ToAbgr(tint)))
        {
            return new BackdropResult(BackdropKind.Acrylic, true, "ACCENT_ENABLE_ACRYLICBLURBEHIND");
        }

        // 完整回滚：背景色 + 边框扩展，一个都不能留。
        if (source?.CompositionTarget is not null && original is { } color)
        {
            source.CompositionTarget.BackgroundColor = color;
        }

        var zero = new NativeMethods.Margins();
        NativeMethods.DwmExtendFrameIntoClientArea(handle, ref zero);

        LogOnce("Windows 10 亚克力不可用，退回实色背景");
        return new BackdropResult(BackdropKind.Solid, false, "accent 失败");
    }

    /// <summary>
    /// 设置窗口合成属性里的强调色策略。
    /// </summary>
    /// <param name="handle">窗口句柄。</param>
    /// <param name="gradientColor">着色，格式 <c>AABBGGRR</c>（注意不是 ARGB）。</param>
    /// <returns>是否成功。</returns>
    private static bool TryApplyAccent(IntPtr handle, int gradientColor)
    {
        var policy = new NativeMethods.AccentPolicy
        {
            AccentState = NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = gradientColor,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<NativeMethods.AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);

            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WCA_ACCENT_POLICY,
                Data = pointer,
                SizeOfData = size,
            };

            return NativeMethods.SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary><c>Color</c> 是 ARGB，而 accent 的着色是 AABBGGRR。</summary>
    private static int ToAbgr(Color color) =>
        (color.A << 24) | (color.B << 16) | (color.G << 8) | color.R;

    private static void LogOnce(string message)
    {
        if (_failureLogged)
        {
            return;
        }

        _failureLogged = true;
        Log.Write(message);
    }
}

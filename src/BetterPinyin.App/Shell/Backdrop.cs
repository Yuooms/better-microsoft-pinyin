using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 窗口亚克力背景。
/// </summary>
/// <remarks>
/// <para>
/// Windows 11（22621+）走 DWM 的 <c>DWMWA_SYSTEMBACKDROP_TYPE</c>；Windows 10 走未公开的
/// <c>SetWindowCompositionAttribute</c>。两条路都要求窗口自身不画不透明背景，
/// 否则模糊会被 WPF 的绘制盖住，所以成功后把 <see cref="Window.Background"/> 置为透明。
/// </para>
/// <para>
/// 失败时恢复实色背景并返回 false。宁可看起来普通，也不要出现一块黑底或透出桌面。
/// </para>
/// </remarks>
internal static class Backdrop
{
    /// <summary>系统背景类型：亚克力（<c>DWMSBT_TRANSIENTWINDOW</c>）。</summary>
    private const int DwmSystemBackdropType = 38;

    /// <summary>窗口圆角偏好（Win11）。</summary>
    private const int DwmWindowCornerPreference = 33;

    /// <summary>
    /// 尝试给窗口加亚克力背景。
    /// </summary>
    /// <param name="window">目标窗口，必须已经创建句柄。</param>
    /// <param name="tint">着色，格式 AABBGGRR；值越白越接近磨砂白。</param>
    /// <returns>成功返回 true。</returns>
    public static bool TryApplyAcrylic(Window window, uint tint)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null)
        {
            return false;
        }

        // 客户区不画底色，DWM 的模糊才能透出来。
        source.CompositionTarget.BackgroundColor = Colors.Transparent;

        var margins = new NativeMethods.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(handle, ref margins);

        var build = Environment.OSVersion.Version.Build;
        if (build >= 22621)
        {
            var backdrop = 3;
            if (NativeMethods.DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref backdrop, sizeof(int)) == 0 &&
                TrySetRoundedCorners(handle))
            {
                window.Background = Brushes.Transparent;
                return true;
            }
        }

        if (TryApplyAccent(handle, tint))
        {
            window.Background = Brushes.Transparent;
            return true;
        }

        // 恢复实色，避免透明窗口透出桌面。
        window.Background = window.TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;
        return false;
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
            TrySetRoundedCorners(handle);
            return;
        }

        var region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
        if (region != IntPtr.Zero)
        {
            NativeMethods.SetWindowRgn(handle, region, true);
        }
    }

    private static bool TrySetRoundedCorners(IntPtr handle)
    {
        var preference = 2; // DWMWCP_ROUND
        return NativeMethods.DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int)) == 0;
    }

    private static bool TryApplyAccent(IntPtr handle, uint tint)
    {
        var policy = new NativeMethods.AccentPolicy
        {
            AccentState = NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = unchecked((int)tint),
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
}

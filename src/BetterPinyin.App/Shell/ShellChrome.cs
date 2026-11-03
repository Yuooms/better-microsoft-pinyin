using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 外壳窗口的背景与标题栏。
/// </summary>
/// <remarks>
/// 把「什么时候应用材质、失败后画什么底色」收敛在一处：
/// 窗口只需要提供一个铺满客户区的根 <see cref="Border"/>，其余由这里负责。
/// 主题切换后必须重新应用一次——着色值、标题栏颜色、圆角都跟着主题走，
/// 而且代码里赋过的画刷不会自动换成新主题的实例。
/// </remarks>
internal static class ShellChrome
{
    /// <summary>把窗口接到主题与背景管理上。</summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="root">铺满客户区的根 Border，用于失败时铺实色。</param>
    public static void Attach(Window window, Border root)
    {
        var attached = false;

        EventHandler handler = (_, _) => Apply(window, root);

        window.SourceInitialized += (_, _) =>
        {
            if (attached)
            {
                return;
            }

            attached = true;
            ThemeManager.ThemeChanged += handler;
            Apply(window, root);
        };

        window.Closed += (_, _) =>
        {
            if (attached)
            {
                ThemeManager.ThemeChanged -= handler;
            }
        };
    }

    /// <summary>重新应用一次背景。切换「窗口背景」设置后调用。</summary>
    /// <param name="window">目标窗口。</param>
    public static void Refresh(Window window)
    {
        if (window.FindName("WindowRoot") is Border root)
        {
            Apply(window, root);
        }
    }

    private static void Apply(Window window, Border root)
    {
        var mode = AppearanceOptions.ParseBackdrop(AppServices.Settings.BackdropMode);
        var result = Backdrop.Apply(window, mode, ThemeManager.IsDark);

        // 材质生效时根层必须全透明，否则 WPF 画的底色会把模糊盖住。
        // 实色模式下这里用当前主题的窗口底色，切主题时会重新取一次。
        root.Background = result.Applied
            ? Brushes.Transparent
            : ThemeManager.ResolveBrush("WindowBackgroundBrush") ?? Brushes.White;

        Log.Write(result.Applied
            ? $"窗口材质：{result.Kind}（{result.Detail}）"
            : $"窗口材质：实色（{result.Detail}）");
    }
}

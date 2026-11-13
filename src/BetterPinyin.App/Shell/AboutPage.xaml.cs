using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace BetterPinyin.App.Shell;

/// <summary>关于页。</summary>
public partial class AboutPage : Page
{
    /// <summary>创建页面。</summary>
    public AboutPage()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = $"版本 {version?.ToString(3) ?? "0.1.0"}";

        // 页面里的超链接是可聚焦元素：导航进来时 WPF 会把它 BringIntoView，
        // 导致页面一打开就跳到下方的作者信息。等布局和焦点都稳定后再回到顶部。
        Loaded += (_, _) =>
        {
            // 页面由导航缓存，切走再回来可能停在上次的悬停状态，先复位两张图。
            IconImage.BeginAnimation(OpacityProperty, null);
            HeroImage.BeginAnimation(OpacityProperty, null);
            IconImage.Opacity = 1;
            HeroImage.Opacity = 0;

            Dispatcher.BeginInvoke(
                new Action(() => RootScroll.ScrollToTop()),
                DispatcherPriority.ContextIdle);
        };
    }

    /// <summary>鼠标移入：主视觉淡入、应用图标淡出。</summary>
    private void OnLogoEnter(object sender, MouseEventArgs e) => CrossFade(toHero: true);

    /// <summary>鼠标移出：换回应用图标。</summary>
    private void OnLogoLeave(object sender, MouseEventArgs e) => CrossFade(toHero: false);

    /// <summary>
    /// 两张图交叉淡入淡出。
    /// </summary>
    /// <param name="toHero">true 表示切到主视觉，false 表示切回应用图标。</param>
    /// <remarks>
    /// 用 <see cref="DoubleAnimation"/> 直接改 Opacity，不建 Storyboard：
    /// 只有两个属性、两条动画，Storyboard 的样板代码反而更难读。
    /// 时长取 200ms 左右——再快会像闪烁，再慢会跟不上鼠标。
    /// </remarks>
    private void CrossFade(bool toHero)
    {
        var duration = TimeSpan.FromMilliseconds(200);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        HeroImage.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(toHero ? 1 : 0, duration) { EasingFunction = easing });

        IconImage.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(toHero ? 0 : 1, duration) { EasingFunction = easing });
    }

    /// <summary>
    /// 用系统默认浏览器打开链接。
    /// </summary>
    /// <remarks>
    /// WPF 的 <see cref="Hyperlink"/> 只负责发事件，不会自己启动浏览器；
    /// 这里显式走 <c>UseShellExecute</c>，由系统决定用哪个浏览器打开。
    /// </remarks>
    private void OnLinkClick(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"打开链接失败：{e.Uri.AbsoluteUri} {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            e.Handled = true;
        }
    }
}

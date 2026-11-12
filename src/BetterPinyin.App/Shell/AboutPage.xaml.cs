using System.Diagnostics;
using System.Windows.Controls;
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

        // 页面里的超链接是可聚焦元素：导航进来时 WPF 会把它 BringIntoView，
        // 导致页面一打开就跳到「作者与开源」。等布局和焦点都稳定后再回到顶部。
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(() => RootScroll.ScrollToTop()),
            DispatcherPriority.ContextIdle);
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

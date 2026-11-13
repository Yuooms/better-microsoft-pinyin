using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using BetterPinyin.App.Updates;
using BetterPinyin.Core.Updates;

namespace BetterPinyin.App.Shell;

/// <summary>关于页。</summary>
public partial class AboutPage : Page
{
    private ReleaseInfo? _latestRelease;
    private bool _updateBusy;

    /// <summary>创建页面。</summary>
    public AboutPage()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = $"版本 {version?.ToString(3) ?? "0.1.0"}";
        UpdateTitleText.Text = $"检查 GitHub 上的最新发布（当前 {version?.ToString(3) ?? "0.1.0"}）";
        UpdateAutoCheckSwitch.IsChecked = AppServices.Settings.UpdateCheckEnabled;
        RefreshCacheInfo();

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

            _ = SyncUpdateStateAsync();
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
        OpenInBrowser(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    /// <summary>
    /// 打开页面时把更新状态补上。
    /// </summary>
    /// <remarks>
    /// 启动时的后台检查与这里共用 <see cref="UpdateService.LastOutcome"/>：
    /// 刚查过就直接显示结论，没查过（或关掉了自动检查）才现发一次请求。
    /// </remarks>
    private async Task SyncUpdateStateAsync()
    {
        var cached = AppServices.Updates.LastOutcome;
        if (cached is not null)
        {
            ShowUpdateOutcome(cached);
            return;
        }

        await RunUpdateCheckAsync(announceStart: false);
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e) =>
        await RunUpdateCheckAsync(announceStart: true);

    private async Task RunUpdateCheckAsync(bool announceStart)
    {
        if (_updateBusy)
        {
            return;
        }

        _updateBusy = true;
        CheckUpdateButton.IsEnabled = false;

        if (announceStart)
        {
            UpdateStatusText.Text = "正在检查…";
        }

        try
        {
            var outcome = await AppServices.Updates.CheckAsync(CancellationToken.None);
            ShowUpdateOutcome(outcome);

            // 手动检查也记一次时间：自动检查据此节流，不用重复发请求。
            AppServices.Settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.ToString("O");
            AppServices.SaveSettings();

            if (!outcome.Succeeded)
            {
                Log.Write($"检查更新失败：{outcome.Message}");
            }
        }
        finally
        {
            _updateBusy = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ShowUpdateOutcome(UpdateCheckOutcome outcome)
    {
        // 每次换结论都重画缓存区：缓存里可能还留着上一个版本，
        // 这里显示的必须是「现在这个局面」下的真实状态。
        RefreshCacheInfo();

        if (!outcome.Succeeded || outcome.Latest is null)
        {
            _latestRelease = null;
            UpdateStatusText.Text = outcome.Message;
            UpdateActionsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _latestRelease = outcome.HasUpdate ? outcome.Latest : null;

        if (!outcome.HasUpdate)
        {
            var published = outcome.Latest.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd");
            UpdateStatusText.Text = string.IsNullOrEmpty(published)
                ? outcome.Message
                : $"{outcome.Message}，最新发布 {published}";
            UpdateActionsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var date = outcome.Latest.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd");
        UpdateActionsPanel.Visibility = Visibility.Visible;
        UpdateApplyState();
        UpdateStatusText.Text = BuildUpdateStatus(outcome.Latest, date);
    }

    /// <summary>刷新「下载 / 安装」两个按钮的可见性。</summary>
    /// <remarks>
    /// 下载与安装分成两步：下载只往缓存目录写文件，装不装、什么时候装由用户决定。
    /// 这样更新包看得见、删得掉，也不会出现「点一下程序就没了」。
    /// </remarks>
    private void UpdateApplyState()
    {
        if (_latestRelease is null)
        {
            return;
        }

        var downloaded = UpdateService.IsDownloaded(_latestRelease);
        DownloadUpdateButton.Visibility = downloaded ? Visibility.Collapsed : Visibility.Visible;
        ApplyUpdateButton.Visibility = downloaded ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildUpdateStatus(ReleaseInfo release, string? date)
    {
        var headline = string.IsNullOrEmpty(date)
            ? $"发现新版本 {release.Tag}"
            : $"发现新版本 {release.Tag}（{date}）";

        return UpdateService.IsDownloaded(release)
            ? $"{headline}，已下载到本地，点「安装并重启」完成更新。"
            : $"{headline}，点「下载更新」保存到本地，下载完再决定是否安装。";
    }

    /// <summary>刷新缓存大小与路径。</summary>
    private void RefreshCacheInfo()
    {
        var size = UpdateService.GetCacheSize();
        UpdateCacheSizeText.Text = size > 0
            ? $"更新缓存：{FormatSize(size)}"
            : "更新缓存：空";
        UpdateCachePathText.Text = UpdateService.CacheRoot;
        ClearCacheButton.IsEnabled = size > 0;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value:0.#} {units[unit]}";
    }

    /// <summary>
    /// 下载更新包到缓存目录。
    /// </summary>
    /// <remarks>
    /// 只下载、不安装：文件落在「关于」页显示的那个路径下，
    /// 用户可以先确认版本号、也可以随时清掉，装不装由下一次点击决定。
    /// </remarks>
    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _latestRelease is null)
        {
            return;
        }

        _updateBusy = true;
        DownloadUpdateButton.IsEnabled = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"正在下载 {_latestRelease.Tag} 到 {UpdateService.CacheRoot}…";

        try
        {
            var progress = new Progress<double>(value => UpdateProgressBar.Value = value);
            await AppServices.Updates.DownloadAsync(_latestRelease, progress, CancellationToken.None);

            RefreshCacheInfo();
            UpdateApplyState();

            var size = UpdateService.GetCacheSize();
            UpdateStatusText.Text =
                $"已下载 {_latestRelease.Tag}（{FormatSize(size)}）。点「安装并重启」完成更新，程序会自动退出再启动。";
            Log.Write($"更新包已下载到 {UpdateService.GetReleaseDirectory(_latestRelease.Tag)}，等待用户确认安装");
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            UpdateStatusText.Text = $"下载失败：{ex.Message}";
            Log.Write($"下载更新失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _updateBusy = false;
            DownloadUpdateButton.IsEnabled = true;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 用缓存里的更新包替换当前安装目录，然后退出让脚本接管。
    /// </summary>
    private async void OnApplyUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _latestRelease is null)
        {
            return;
        }

        _updateBusy = true;
        ApplyUpdateButton.IsEnabled = false;

        try
        {
            var directory = Path.Combine(
                UpdateService.GetReleaseDirectory(_latestRelease.Tag),
                "app");

            if (AppServices.Updates.TryApplyAndRestart(_latestRelease, directory, out var message))
            {
                UpdateStatusText.Text = message;
                Log.Write($"开始更新到 {_latestRelease.Tag}，即将退出");

                // 给 cmd 一点时间把脚本拉起来，再退出，避免脚本刚启动就失去父进程。
                await Task.Delay(600);
                Application.Current.Shutdown();
                return;
            }

            UpdateStatusText.Text = message;
            Log.Write($"未能自动更新：{message}");
        }
        finally
        {
            _updateBusy = false;
            ApplyUpdateButton.IsEnabled = true;
        }
    }

    private void OnOpenCacheFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(UpdateService.CacheRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{UpdateService.CacheRoot}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            UpdateStatusText.Text = $"打不开缓存目录：{ex.Message}";
            Log.Write($"打开更新缓存目录失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var size = UpdateService.ClearCache();
        RefreshCacheInfo();
        UpdateApplyState();

        if (_latestRelease is not null)
        {
            var date = _latestRelease.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd");
            UpdateStatusText.Text = BuildUpdateStatus(_latestRelease, date);
        }

        var note = size > 0 ? $"已清理 {FormatSize(size)} 缓存。" : "缓存本来就是空的。";
        var baseText = UpdateStatusText.Text;
        UpdateStatusText.Text = baseText.EndsWith("。", StringComparison.Ordinal)
            ? baseText + note
            : $"{baseText}。{note}";
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        var url = _latestRelease?.PageUrl;
        if (string.IsNullOrEmpty(url))
        {
            url = $"https://github.com/{UpdateService.Repository}/releases";
        }

        OpenInBrowser(url);
    }

    private void OnAutoCheckChanged(object sender, RoutedEventArgs e)
    {
        // 这个开关即时生效：关于页没有「保存」按钮，改动就该立刻写回去。
        var enabled = UpdateAutoCheckSwitch.IsChecked == true;
        if (AppServices.Settings.UpdateCheckEnabled == enabled)
        {
            return;
        }

        AppServices.Settings.UpdateCheckEnabled = enabled;
        if (AppServices.SaveSettings())
        {
            UpdateStatusText.Text = enabled ? "已开启启动时检查更新" : "已关闭启动时检查更新";
        }
        else
        {
            UpdateStatusText.Text = $"写入配置失败：{AppServices.Store.LastError}";
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"打开链接失败：{url} {ex.GetType().Name} {ex.Message}");
        }
    }
}

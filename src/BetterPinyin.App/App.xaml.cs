using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterPinyin.App.Updates;
using BetterPinyin.Interop.Input;
using BetterPinyin.Interop.Windowing;
using BetterPinyin.App.Features.CloudAssist;
using BetterPinyin.App.Features.Statistics;
using BetterPinyin.App.Shell;
using BetterPinyin.Core.Features;

namespace BetterPinyin.App;

/// <summary>
/// 程序入口与生命周期。
/// </summary>
/// <remarks>
/// 常驻托盘，主窗口只作为设置界面按需打开。
/// 云联想模块在启动后立即运行，与设置窗口是否可见无关。
/// </remarks>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\BetterMicrosoftPinyin.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private TrayIcon? _trayIcon;
    private CloudAssistFeature? _cloudAssist;
    private StatisticsFeature? _statistics;
    private KeyboardHub? _keyboardHub;
    private FeatureRegistry? _registry;
    private SettingsWindow? _settingsWindow;
    private string? _startupPage;
    private string? _uiShotPath;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            if (!AcquireSingleInstance())
            {
                MessageBox.Show(
                    "Better 微软拼音已经在运行，请在系统托盘中打开设置。",
                    "Better 微软拼音",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Error("AppDomain", args.ExceptionObject as Exception ?? new Exception("未知异常"));

            // 兜底：OnExit 里抛异常时仍能把日志队列刷完。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Shutdown(500);

            Log.Write("程序启动");

            AppServices.Initialize();
            Log.Write($"设置已加载：{AppServices.Store.FilePath}；UIA 可用={AppServices.UiaAvailable}");

            // 主题必须在创建任何窗口之前定下来：窗口的 SourceInitialized 会读当前主题
            // 去设标题栏与背景材质。
            // --theme=dark 只影响本次运行，用于截图与回归，不写回配置。
            var themeOverride = e.Args
                .FirstOrDefault(arg => arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
                ?["--theme=".Length..];

            ThemeManager.Initialize(themeOverride is null
                ? AppearanceOptions.ParseTheme(AppServices.Settings.ThemeMode)
                : AppearanceOptions.ParseTheme(themeOverride));
            ThemeManager.Validate();

            // 设置窗口的 Fluent 控件由 WPF-UI 着色，必须与 ThemeManager 同步。
            // 启动调用要在任何窗口创建之前，运行期只换主题字典，避免动到候选浮窗。
            FluentThemeBridge.ApplyStartup(ThemeManager.IsDark);
            ThemeManager.ThemeChanged += OnThemeChanged;

            SystemTheme.Start();

            _registry = new FeatureRegistry();

            // 键盘钩子是进程级独占资源，必须只有一个持有者：
            // 云联想和统计都订阅它，谁先需要谁触发安装，最后一个走了才卸载。
            _keyboardHub = new KeyboardHub();
            _cloudAssist = new CloudAssistFeature(AppServices.Settings, _keyboardHub);
            _registry.Register(_cloudAssist);

            _statistics = new StatisticsFeature(
                AppServices.Settings,
                _keyboardHub,
                _cloudAssist.Controller);
            _registry.Register(_statistics);

            _trayIcon = new TrayIcon(
                onOpenSettings: () => ShowSettings(),
                onOpenAbout: () => ShowSettings("关于"),
                onToggle: OnToggleEnabled,
                onExit: Shutdown,
                initiallyEnabled: AppServices.Settings.CloudAssistEnabled);

            // 模块启动不能阻塞 UI 线程，否则第一帧会卡在钩子安装上。
            _ = StartFeaturesAsync();

            // 更新检查同样放到后台：网络慢的时候不能挡住设置窗口出现。
            ScheduleUpdateCheck();

            // 首次运行（还没有配置文件）直接把设置打开，避免用户以为程序没启动。
            // --settings 便于开发时直接进设置页核对界面；--page=外观 可直达某一页。
            _startupPage = e.Args
                .FirstOrDefault(arg => arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                ?["--page=".Length..];

            // 开机启动时不弹设置窗口：正常启动本来就只驻留托盘，
            // 但首次运行（还没有配置文件）默认会打开设置，自启动必须跳过这一条。
            var launchedByStartup = e.Args.Any(
                arg => arg.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

            // 诊断入口：把设置窗口渲染成 PNG 后退出，便于在没有截图权限的环境里做界面回归。
            _uiShotPath = e.Args
                .FirstOrDefault(arg => arg.StartsWith("--ui-shot=", StringComparison.OrdinalIgnoreCase))
                ?["--ui-shot=".Length..];

            if ((!File.Exists(AppServices.Store.FilePath) && !launchedByStartup) ||
                e.Args.Any(arg => arg.Equals("--settings", StringComparison.OrdinalIgnoreCase)) ||
                _startupPage is not null ||
                _uiShotPath is not null)
            {
                ShowSettings();

                if (_uiShotPath is not null)
                {
                    CaptureUi(_uiShotPath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("OnStartup", ex);
            MessageBox.Show(
                $"程序启动失败：{ex.Message}\n\n详细日志：{Log.FilePath}",
                "Better 微软拼音",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _registry?.StopAllAsync().GetAwaiter().GetResult();
            _cloudAssist?.Dispose();
            _statistics?.Dispose();
            _keyboardHub?.Dispose();
            _trayIcon?.Dispose();
            AppServices.SaveSettings();
            AppServices.Shutdown();
        }
        catch (Exception)
        {
            // 退出路径上的异常不再向上抛，避免关闭时弹窗。
        }
        finally
        {
            SystemTheme.Stop();

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();

            // 日志是后台线程写的：这里等它把队列刷完，最多 1.5 秒。
            Log.Shutdown();
        }

        base.OnExit(e);
    }

    private static void OnThemeChanged(object? sender, EventArgs e) =>
        FluentThemeBridge.ApplyRuntime(ThemeManager.IsDark);

    /// <summary>两次自动检查之间的最小间隔。</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// 按设置与节流间隔决定要不要在后台查一次新版本。
    /// </summary>
    /// <remarks>
    /// GitHub 的未认证接口有速率限制，程序也没必要反复问，所以每天最多一次。
    /// 失败只写日志不打扰用户——离线、代理阻断、被墙都是常态。
    /// </remarks>
    private void ScheduleUpdateCheck()
    {
        if (!AppServices.Settings.UpdateCheckEnabled)
        {
            return;
        }

        if (DateTimeOffset.TryParse(
                AppServices.Settings.LastUpdateCheckUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var last) &&
            DateTimeOffset.UtcNow - last < UpdateCheckInterval)
        {
            return;
        }

        _ = CheckUpdatesInBackgroundAsync();
    }

    private async Task CheckUpdatesInBackgroundAsync()
    {
        var outcome = await AppServices.Updates.CheckAsync(CancellationToken.None).ConfigureAwait(true);

        if (!outcome.Succeeded)
        {
            Log.Write($"检查更新失败：{outcome.Message}");
            return;
        }

        AppServices.Settings.LastUpdateCheckUtc =
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        AppServices.SaveSettings();

        Log.Write($"更新检查：{outcome.Message}");
        if (!outcome.HasUpdate)
        {
            return;
        }

        // 设置窗口开着的时候用户自己能看见，不用再弹气泡。
        if (_settingsWindow is { IsVisible: true })
        {
            return;
        }

        _trayIcon?.ShowBalloon(
            $"发现新版本 {outcome.Latest?.Tag}",
            "打开设置 → 关于，可以手动下载（不会自动下载）");
    }

    /// <summary>
    /// 把设置窗口按当前布局渲染成 PNG，然后退出。
    /// </summary>
    /// <param name="path">输出路径。</param>
    /// <remarks>
    /// 只是诊断入口（<c>--ui-shot=</c>），不参与正常启动。
    /// 用 <see cref="RenderTargetBitmap"/> 是因为它不需要屏幕截图权限，
    /// 在远程桌面、CI 或安全策略禁止截屏的环境里也能核对布局。
    /// </remarks>
    private void CaptureUi(string path)
    {
        Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                // 等模板应用、页面导航和第一次布局完成。
                await Task.Delay(600);

                if (_settingsWindow is null ||
                    _settingsWindow.ActualWidth <= 0 ||
                    _settingsWindow.ActualHeight <= 0)
                {
                    Log.Write("界面截图失败：设置窗口尚未完成布局");
                    Shutdown();
                    return;
                }

                try
                {
                    var width = (int)Math.Ceiling(_settingsWindow.ActualWidth);
                    var height = (int)Math.Ceiling(_settingsWindow.ActualHeight);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(_settingsWindow);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));

                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    using var stream = File.Create(path);
                    encoder.Save(stream);
                    Log.Write($"界面截图：{path}（{width}x{height}）");
                }
                catch (Exception ex)
                {
                    Log.Error("界面截图", ex);
                }
                finally
                {
                    Shutdown();
                }
            }),
            DispatcherPriority.ApplicationIdle);
    }

    private async Task StartFeaturesAsync()
    {
        if (_registry is null)
        {
            return;
        }

        try
        {
            await _registry.StartAllAsync().ConfigureAwait(true);
            Log.Write("功能模块已启动");
        }
        catch (Exception ex)
        {
            Log.Error("StartFeatures", ex);
        }

        if (!AppServices.UiaAvailable)
        {
            _trayIcon?.ShowBalloon(
                "候选窗读取不可用",
                "将跳过候选去重，云联想与上屏仍可正常使用。可在「网络与环境」页查看详情。");
        }
    }

    /// <summary>
    /// 打开设置窗口。
    /// </summary>
    /// <param name="page">要定位到的页面标题；为空时沿用命令行指定的页。</param>
    private void ShowSettings(string? page = null)
    {
        try
        {
            if (_settingsWindow is null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(_registry!);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }

            _settingsWindow.Show();
            _settingsWindow.WindowState = WindowState.Normal;

            // 托盘启动的程序默认没有前台权限，仅 Activate() 会被系统拦下，
            // 窗口会开在别的程序后面，用户会以为没反应。这里借前台线程的输入队列强制置前。
            var handle = new WindowInteropHelper(_settingsWindow).Handle;
            if (!NativeMethods.ForceForegroundWindow(handle))
            {
                _settingsWindow.Activate();
            }

            Log.Write("设置窗口已打开");

            var target = page ?? _startupPage;
            if (target is not null)
            {
                _settingsWindow.SelectPage(target);
                _startupPage = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("ShowSettings", ex);
            MessageBox.Show(
                $"打开设置窗口失败：{ex.Message}\n\n详细日志：{Log.FilePath}",
                "Better 微软拼音",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnToggleEnabled(bool enabled)
    {
        AppServices.Settings.CloudAssistEnabled = enabled;
        AppServices.SaveSettings();

        if (_cloudAssist is not null)
        {
            _cloudAssist.IsEnabled = enabled;
            _cloudAssist.SetEnabled(enabled);
        }

        _trayIcon?.SetEnabledState(enabled);
    }

    private bool AcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        return createdNew;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 线程", e.Exception);
        e.Handled = true;
    }
}

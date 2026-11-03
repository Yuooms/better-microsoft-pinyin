using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BetterPinyin.Interop.Windowing;
using BetterPinyin.App.Features.CloudAssist;
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
    private FeatureRegistry? _registry;
    private SettingsWindow? _settingsWindow;

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

            Log.Write("程序启动");

            AppServices.Initialize();
            Log.Write($"设置已加载：{AppServices.Store.FilePath}；UIA 可用={AppServices.UiaAvailable}");

            _registry = new FeatureRegistry();
            _cloudAssist = new CloudAssistFeature(AppServices.Settings);
            _registry.Register(_cloudAssist);

            _trayIcon = new TrayIcon(
                onOpenSettings: ShowSettings,
                onToggle: OnToggleEnabled,
                onExit: Shutdown,
                initiallyEnabled: AppServices.Settings.CloudAssistEnabled);

            // 模块启动不能阻塞 UI 线程，否则第一帧会卡在钩子安装上。
            _ = StartFeaturesAsync();

            // 首次运行（还没有配置文件）直接把设置打开，避免用户以为程序没启动。
            if (!File.Exists(AppServices.Store.FilePath))
            {
                ShowSettings();
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
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }

        base.OnExit(e);
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

    private void ShowSettings()
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
            _cloudAssist.ApplySettings();
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

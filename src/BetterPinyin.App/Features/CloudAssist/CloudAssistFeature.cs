using System.Windows;
using BetterPinyin.Core.Features;
using BetterPinyin.Core.Settings;
using BetterPinyin.Core.Wordbook;
using BetterPinyin.Interop.Input;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>
/// 云联想功能模块。
/// </summary>
/// <remarks>
/// 把控制器（逻辑）、浮窗（展示）与设置页（配置）绑在一起，
/// 并作为 <see cref="IFeatureModule"/> 注册进框架，使设置窗口的导航自动出现"云联想"页。
/// </remarks>
internal sealed class CloudAssistFeature : IFeatureModule, IDisposable
{
    private readonly AppSettings _settings;
    private readonly CloudAssistController _controller;
    private readonly ElevatedForegroundWatch _elevatedWatch = new();
    private CloudCandidateWindow? _window;
    private bool _started;
    private bool _disposed;

    public CloudAssistFeature(AppSettings settings, KeyboardHub hub)
    {
        _settings = settings;
        _controller = new CloudAssistController(settings, hub);
        SettingsPages = new ISettingsPage[]
        {
            new CloudAssistSettingsPage(this),
            new WordbookSettingsPage(),
        };
    }

    /// <summary>发现前台程序权限高于本程序（云联想在它里面必然不生效）。</summary>
    public event EventHandler<ElevatedAppEventArgs>? ElevatedAppDetected
    {
        add => _elevatedWatch.Detected += value;
        remove => _elevatedWatch.Detected -= value;
    }

    /// <summary>最近一次检测到的更高权限程序；没有则为 null。</summary>
    public string? ElevatedApp => _elevatedWatch.LastProcessName;

    /// <summary>该程序的完整性级别。</summary>
    public IntegrityLevel ElevatedAppLevel => _elevatedWatch.LastLevel;

    /// <summary>用户是否已经关掉了这条提示（仅本次运行）。</summary>
    public bool ElevatedNoticeDismissed { get; private set; }

    /// <summary>关闭权限提示。同一进程里不再自动弹出。</summary>
    public void DismissElevatedNotice() => ElevatedNoticeDismissed = true;

    /// <inheritdoc />
    public string Id => "cloud-assist";

    /// <inheritdoc />
    public string DisplayName => "云联想";

    /// <inheritdoc />
    public string Description => "为微软拼音补充云端联想词";

    /// <inheritdoc />
    public bool IsEnabled
    {
        get => _settings.CloudAssistEnabled;
        set => _settings.CloudAssistEnabled = value;
    }

    /// <inheritdoc />
    public IReadOnlyList<ISettingsPage> SettingsPages { get; }

    /// <summary>控制器，供设置页展示诊断信息与触发即时生效。</summary>
    public CloudAssistController Controller => _controller;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 幂等：框架启动时调一次，运行期开关还会调，重复订阅会让浮窗收到两份事件。
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _window = new CloudCandidateWindow(_settings.PopupStyle);

        _controller.SuggestionsReady += OnSuggestionsReady;
        _controller.SuggestionsCleared += OnSuggestionsCleared;
        _controller.Start();

        // 与键盘无关的一条观察线：被 UIPI 拦掉时，按键那边什么都不剩。
        _elevatedWatch.Start();

        AppServices.Controller = _controller;

        // 升级前学到的词还停在「待同步」，这里补一次；失败只记日志，下次采纳还会重试。
        if (_settings.PhraseAutoWrite && AppServices.Wordbook.Words.Any(w => w.UdpState == UdpSyncState.Pending))
        {
            _ = Task.Run(AppServices.SyncPendingPhrases);
        }
        else if (_settings.PhraseWriteInitials)
        {
            // 短语库是升级前写的，只有全拼；补一遍简拼，打首字母才出得来。
            _ = Task.Run(AppServices.BackfillInitialPhrases);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        if (!_started)
        {
            return Task.CompletedTask;
        }

        _started = false;
        _controller.SuggestionsReady -= OnSuggestionsReady;
        _controller.SuggestionsCleared -= OnSuggestionsCleared;
        _controller.Stop();
        _elevatedWatch.Stop();

        if (_window is not null)
        {
            _window.HideSuggestion();
            _window.Close();
            _window = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 运行期开关。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    /// <remarks>
    /// 必须显式调用：框架只在启动时按 <see cref="IsEnabled"/> 决定要不要 <see cref="StartAsync"/>，
    /// 之后改配置不会重新走一遍生命周期。少了这一步，托盘里「关掉再打开」要重启程序才生效。
    /// </remarks>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _started)
        {
            ApplySettings();
            return;
        }

        if (enabled)
        {
            _ = StartAsync(CancellationToken.None);
        }
        else
        {
            _ = StopAsync();
        }

        ApplySettings();
    }

    /// <summary>设置变更后让控制器重建排序参数，并在样式变化时重建浮窗。</summary>
    public void ApplySettings()
    {
        _controller.ApplySettings();

        // 分层窗口可以运行时换参数，但阴影/圆角都在构造时设一次，
        // 样式变了仍换一个窗口最省事；比较前先归一化，旧配置里的 Acrylic 也能对上。
        if (_window is null ||
            string.Equals(
                _window.PopupStyle,
                PopupStyleCatalog.Normalize(_settings.PopupStyle),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _window.HideSuggestion();
        _window.Close();
        _window = new CloudCandidateWindow(_settings.PopupStyle);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _elevatedWatch.Dispose();
        _controller.Dispose();
    }

    private void OnSuggestionsReady(object? sender, CloudSuggestionsShownEventArgs e)
    {
        // 事件来自后台线程，必须切回 UI 线程操作窗口。
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _window?.ShowSuggestions(e.Slots, e.Anchor, e.EnglishMode, _settings);
        });
    }

    private void OnSuggestionsCleared(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => _window?.HideSuggestion());
    }
}

using System.Windows;
using BetterPinyin.Core.Features;
using BetterPinyin.Core.Settings;

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
    private CloudCandidateWindow? _window;
    private bool _disposed;

    public CloudAssistFeature(AppSettings settings)
    {
        _settings = settings;
        _controller = new CloudAssistController(settings);
        SettingsPages = new ISettingsPage[]
        {
            new CloudAssistSettingsPage(this),
        };
    }

    /// <inheritdoc />
    public string Id => "cloud-assist";

    /// <inheritdoc />
    public string DisplayName => "云联想";

    /// <inheritdoc />
    public string Description => "为微软拼音补充 Bing 联想词";

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
        _window = new CloudCandidateWindow();

        _controller.SuggestionReady += OnSuggestionReady;
        _controller.SuggestionCleared += OnSuggestionCleared;

        if (!_controller.Start())
        {
            return Task.CompletedTask;
        }

        AppServices.Controller = _controller;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        _controller.SuggestionReady -= OnSuggestionReady;
        _controller.SuggestionCleared -= OnSuggestionCleared;
        _controller.Stop();

        if (_window is not null)
        {
            _window.HideSuggestion();
            _window.Close();
            _window = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>设置变更后让控制器重建排序参数。</summary>
    public void ApplySettings() => _controller.ApplySettings();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.Dispose();
    }

    private void OnSuggestionReady(object? sender, CloudSuggestionShownEventArgs e)
    {
        // 事件来自后台线程，必须切回 UI 线程操作窗口。
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _window?.ShowSuggestion(
                e.Suggestion,
                e.Anchor,
                DescribeAcceptKey(_settings.AcceptKey),
                _settings.ShowCloudIcon);
        });
    }

    private void OnSuggestionCleared(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => _window?.HideSuggestion());
    }

    private static string DescribeAcceptKey(string key) => key switch
    {
        "Semicolon" => ";",
        "Backquote" => "`",
        "Comma" => ",",
        _ => "Tab",
    };
}

using BetterPinyin.App.Features.CloudAssist;
using BetterPinyin.Core.Features;
using BetterPinyin.Core.Settings;
using BetterPinyin.Core.Statistics;
using BetterPinyin.Interop.Input;

namespace BetterPinyin.App.Features.Statistics;

/// <summary>
/// 输入统计功能模块。
/// </summary>
/// <remarks>
/// <para>
/// 只统计、不联网、不记录输入内容。开关默认关闭，因为它是本程序里唯一
/// 会持续往本地写文件的模块，应当由用户明确打开。
/// </para>
/// <para>
/// 关闭开关只是停止累计：已有的历史仍然保留，用户仍能查看与清空。
/// 这样「我不想再记了」和「把之前记的删掉」是两个独立的决定。
/// </para>
/// </remarks>
internal sealed class StatisticsFeature : IFeatureModule, IDisposable
{
    private readonly AppSettings _settings;
    private readonly KeyboardHub _hub;
    private readonly CloudAssistController? _controller;
    private readonly StatisticsStore _store;
    private readonly StatisticsAggregator _aggregator;

    private StatisticsCollector? _collector;
    private string? _startError;
    private bool _started;
    private bool _disposed;

    /// <summary>创建统计模块。</summary>
    /// <param name="settings">设置。</param>
    /// <param name="hub">键盘中枢。</param>
    /// <param name="controller">云联想控制器，用来把采纳计入统计；可为空。</param>
    public StatisticsFeature(AppSettings settings, KeyboardHub hub, CloudAssistController? controller)
    {
        _settings = settings;
        _hub = hub;
        _controller = controller;

        _store = new StatisticsStore();
        _aggregator = new StatisticsAggregator(_store.Load());

        SettingsPages = new ISettingsPage[] { new StatisticsSettingsPage(this) };
    }

    /// <inheritdoc />
    public string Id => "input-stats";

    /// <inheritdoc />
    public string DisplayName => "输入统计";

    /// <inheritdoc />
    public string Description => "在本机累计每天的输入量，不上传";

    /// <inheritdoc />
    public bool IsEnabled
    {
        get => _settings.StatisticsEnabled;
        set => _settings.StatisticsEnabled = value;
    }

    /// <inheritdoc />
    public IReadOnlyList<ISettingsPage> SettingsPages { get; }

    /// <summary>累加器，供设置页读取快照。</summary>
    public StatisticsAggregator Aggregator => _aggregator;

    /// <summary>存储，供设置页展示路径与执行清空。</summary>
    public StatisticsStore Store => _store;

    /// <summary>统计文件路径。</summary>
    public string FilePath => _store.FilePath;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _startError = null;
        _collector = new StatisticsCollector(_aggregator, _settings, _hub, _controller, ScheduleSave);
        if (!_collector.Start())
        {
            // 安装失败时不能假装在统计：钩子装不上就一个数字都不会有，
            // 必须留下痕迹，否则用户只会看到「一直是 0」。
            _startError = _collector.LastError;
            Log.Write($"[统计] 启动失败：{_startError}");
            _collector.Dispose();
            _collector = null;
            _started = false;
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
        _collector?.Dispose();
        _collector = null;

        // 退出前必须刷盘，否则最后 1.5 秒里累计的数字会丢。
        SaveNow();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 运行期开关。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    /// <remarks>
    /// 与云联想一样必须显式调用：框架只在启动时按 <see cref="IsEnabled"/> 决定要不要启动模块。
    /// </remarks>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _started)
        {
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
    }

    /// <summary>按当前设置立刻落盘（保留期在这里生效）。</summary>
    /// <returns>成功返回 true。</returns>
    public bool SaveNow()
    {
        return _store.Save(_aggregator.Snapshot(), _settings.StatisticsRetentionDays);
    }

    /// <summary>启动失败的原因；成功时为 null。</summary>
    public string? LastError => _startError;

    /// <summary>清空全部历史。</summary>
    /// <returns>成功返回 true。</returns>
    public bool ClearAll()
    {
        var deleted = _store.DeleteAll();
        _aggregator.Clear();
        return deleted;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _collector?.Dispose();
        _collector = null;
        _store.Dispose();
    }

    private void ScheduleSave()
    {
        _store.ScheduleSave(_aggregator.Snapshot(), _settings.StatisticsRetentionDays);
    }
}

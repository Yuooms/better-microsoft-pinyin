namespace BetterPinyin.Core.Features;

/// <summary>
/// 一个设置页面。
/// </summary>
/// <remarks>
/// 用 <see cref="object"/> 返回视图，是为了让 Core 不依赖 WPF：
/// 具体类型由 App 层提供，Core 只负责声明"有哪些页、叫什么、怎么创建"。
/// </remarks>
public interface ISettingsPage
{
    /// <summary>页面标识，同一模块内唯一。</summary>
    string Id { get; }

    /// <summary>导航栏显示的名称。</summary>
    string Title { get; }

    /// <summary>导航栏图标（Segoe Fluent Icons 字形），可为空。</summary>
    string Glyph { get; }

    /// <summary>页面副标题，可为空。</summary>
    string? Description { get; }

    /// <summary>
    /// 视图类型，用作导航与页面缓存的键。App 层保证它实现 <see cref="CreateView"/> 返回的类型。
    /// </summary>
    Type ViewType { get; }

    /// <summary>创建视图对象（App 层按 Page 使用）。</summary>
    /// <returns>视图实例。</returns>
    object CreateView();
}

/// <summary>
/// 一个功能模块。
/// </summary>
/// <remarks>
/// 新增功能 = 实现本接口 + 在 <see cref="FeatureRegistry"/> 注册，
/// 设置窗口的导航会自动出现对应页面，不需要改外壳代码。
/// </remarks>
public interface IFeatureModule
{
    /// <summary>模块标识。</summary>
    string Id { get; }

    /// <summary>模块显示名。</summary>
    string DisplayName { get; }

    /// <summary>一句话说明。</summary>
    string Description { get; }

    /// <summary>模块是否启用。</summary>
    bool IsEnabled { get; set; }

    /// <summary>本模块提供的设置页。</summary>
    IReadOnlyList<ISettingsPage> SettingsPages { get; }

    /// <summary>启动模块。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>停止模块并释放资源。</summary>
    Task StopAsync();
}

/// <summary>
/// 功能模块注册表。
/// </summary>
public sealed class FeatureRegistry
{
    private readonly List<IFeatureModule> _modules = new();

    /// <summary>已注册的模块。</summary>
    public IReadOnlyList<IFeatureModule> Modules => _modules;

    /// <summary>全部模块提供的设置页（按模块顺序展开）。</summary>
    public IEnumerable<ISettingsPage> AllSettingsPages =>
        _modules.SelectMany(m => m.SettingsPages);

    /// <summary>注册一个模块。</summary>
    /// <param name="module">模块实例。</param>
    public void Register(IFeatureModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (_modules.Any(m => string.Equals(m.Id, module.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"模块标识重复：{module.Id}");
        }

        _modules.Add(module);
    }

    /// <summary>按标识查找模块。</summary>
    /// <typeparam name="T">模块类型。</typeparam>
    /// <param name="id">模块标识。</param>
    /// <returns>模块实例；找不到返回 null。</returns>
    public T? Find<T>(string id)
        where T : class, IFeatureModule =>
        _modules.OfType<T>().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>启动所有已启用的模块。单个模块失败不影响其它模块。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var module in _modules.Where(m => m.IsEnabled))
        {
            try
            {
                await module.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 一个模块起不来不能拖垮整个程序，用户仍可用其它功能。
            }
        }
    }

    /// <summary>停止所有模块。</summary>
    public async Task StopAllAsync()
    {
        foreach (var module in _modules)
        {
            try
            {
                await module.StopAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 退出路径上的异常不再向上抛。
            }
        }
    }
}

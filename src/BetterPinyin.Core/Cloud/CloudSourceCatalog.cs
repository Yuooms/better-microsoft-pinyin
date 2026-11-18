namespace BetterPinyin.Core.Cloud;

/// <summary>
/// 一个云来源的能力与显示信息。
/// </summary>
/// <param name="Id">存进设置里的稳定标识，例如 <c>bing</c>。</param>
/// <param name="DisplayName">设置页显示的名字。</param>
/// <param name="Summary">一句话说明这个来源能给什么。</param>
/// <param name="SupportsExtended">是否能提供「以输入为前缀的更长词」。</param>
/// <remarks>
/// 扩展能力由来源自己声明，而不是在查询链路里写死 Bing。
/// 以后接入 Google / Yandex 这类天生带前缀联想的搜索建议源时，只要把这里设为 true，
/// 它就能自动顶替扩展槽，查询代码不用改。
/// </remarks>
public sealed record CloudSourceDefinition(
    string Id,
    string DisplayName,
    string Summary,
    bool SupportsExtended);

/// <summary>
/// 设置里保存的一个来源及其启用状态。
/// </summary>
/// <remarks>
/// 列表顺序就是取词优先级。关掉的来源留在列表里，用户随时能再打开，
/// 也保留了他排好的位置。
/// </remarks>
public sealed class CloudSourceSetting
{
    /// <summary>来源标识，见 <see cref="CloudSourceCatalog"/>。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>是否参与取词。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 云联想来源清单。
/// </summary>
/// <remarks>
/// 「来源」分两层：家族（<c>bing</c> / <c>sogou</c>）由设置选择，
/// 端点（<c>bing-qsonhs</c> 等）由客户端内部决定。
/// 设置里只存家族 id，端点级信息随 <see cref="CloudProviderAnswer.SourceId"/> 返回。
/// </remarks>
public static class CloudSourceCatalog
{
    /// <summary>必应搜索建议。</summary>
    public const string BingId = "bing";

    /// <summary>搜狗网页输入法（二进制帧）。</summary>
    public const string SogouId = "sogou";

    /// <summary>默认来源。</summary>
    public const string DefaultId = BingId;

    private static readonly CloudSourceDefinition[] Definitions =
    {
        new(BingId, "必应 Bing", "整词与更长的扩展词都支持", SupportsExtended: true),
        new(SogouId, "搜狗", "只返回整词，不提供扩展词", SupportsExtended: false),
    };

    /// <summary>全部已知来源，按默认优先级排列。</summary>
    public static IReadOnlyList<CloudSourceDefinition> All => Definitions;

    /// <summary>按 id 查来源定义。</summary>
    /// <param name="id">来源标识，大小写与首尾空白都会忽略。</param>
    /// <returns>找到返回定义，未知来源返回 null。</returns>
    public static CloudSourceDefinition? Find(string? id) =>
        Definitions.FirstOrDefault(definition =>
            string.Equals(definition.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>这个来源是否支持扩展云联想。</summary>
    /// <param name="id">来源标识。</param>
    /// <returns>支持返回 true；未知来源按不支持处理。</returns>
    public static bool SupportsExtended(string? id) => Find(id)?.SupportsExtended == true;

    /// <summary>
    /// 归一化单个来源取值。
    /// </summary>
    /// <param name="id">设置里读到的原始值，可能为空或已被用户改坏。</param>
    /// <returns>合法的来源 id。</returns>
    /// <remarks>
    /// 用户手改过 settings.json、或将来删掉某个来源时，一律回落到 Bing——
    /// 它是默认值，也是永远可用的那一个。
    /// </remarks>
    public static string Normalize(string? id) => Find(id)?.Id ?? DefaultId;

    /// <summary>来源的显示名，用于设置页与诊断。</summary>
    /// <param name="id">来源 id。</param>
    /// <returns>中文显示名。</returns>
    public static string DisplayName(string? id) =>
        Find(id)?.DisplayName ?? Find(DefaultId)!.DisplayName;

    /// <summary>默认来源列表：Bing 启用，其余禁用。</summary>
    /// <returns>可直接放进设置的新列表。</returns>
    public static List<CloudSourceSetting> CreateDefaultList() =>
        Definitions
            .Select(definition => new CloudSourceSetting
            {
                Id = definition.Id,
                Enabled = definition.Id == DefaultId,
            })
            .ToList();

    /// <summary>
    /// 归一化来源列表：去重、丢弃未知、补齐缺失、保留用户顺序。
    /// </summary>
    /// <param name="list">设置里的列表，可能为 null 或为空。</param>
    /// <param name="legacyProvider">旧版单选来源，仅在列表为空时用来迁移。</param>
    /// <returns>一定非空、只含已知来源的新列表。</returns>
    /// <remarks>
    /// 列表为空有两种情况：首次运行，或从只有一个 <c>StrictProvider</c> 的旧配置升级。
    /// 两种都按旧值迁移，因此不需要额外的版本号判断。
    /// 缺的来源补在末尾并禁用——用户没启用它，不能因为我们新增了来源就偷偷打开。
    /// </remarks>
    public static List<CloudSourceSetting> NormalizeList(
        IEnumerable<CloudSourceSetting>? list,
        string? legacyProvider = null)
    {
        var result = new List<CloudSourceSetting>(Definitions.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, bool enabled)
        {
            if (!seen.Add(id))
            {
                return;
            }

            result.Add(new CloudSourceSetting { Id = id, Enabled = enabled });
        }

        if (list is not null)
        {
            foreach (var item in list)
            {
                if (item is null)
                {
                    continue;
                }

                var definition = Find(item.Id);
                if (definition is not null)
                {
                    Add(definition.Id, item.Enabled);
                }
            }
        }

        if (result.Count == 0)
        {
            Add(Normalize(legacyProvider), enabled: true);
        }

        foreach (var definition in Definitions)
        {
            Add(definition.Id, enabled: false);
        }

        return result;
    }

    /// <summary>按用户顺序取出启用的来源。</summary>
    /// <param name="list">设置里的来源列表。</param>
    /// <returns>启用来源的 id，顺序与设置一致；全关时为空。</returns>
    public static IReadOnlyList<string> ResolveEnabled(IEnumerable<CloudSourceSetting>? list) =>
        NormalizeList(list)
            .Where(item => item.Enabled)
            .Select(item => item.Id)
            .ToList();
}

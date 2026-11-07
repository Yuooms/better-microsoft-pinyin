namespace BetterPinyin.Core.Wordbook;

/// <summary>词条的权重档位。</summary>
public enum WordAdjustment
{
    /// <summary>默认。</summary>
    Normal = 0,

    /// <summary>提升：简拼展开与候选排序靠前。</summary>
    Boosted = 1,

    /// <summary>降低：排在默认之后。</summary>
    Reduced = 2,

    /// <summary>遗忘：从候选与展开里彻底移除，但列表里仍可见、可恢复。</summary>
    Hidden = 3,
}

/// <summary>用户词在微软拼音自定义短语里的同步状态。</summary>
public enum UdpSyncState
{
    /// <summary>还没写过，或写过之后又被改过。</summary>
    Pending = 0,

    /// <summary>已写入且未被外部改动。</summary>
    Synced = 1,

    /// <summary>同拼音下已有别人的短语，未覆盖。</summary>
    Conflict = 2,

    /// <summary>写入失败（文件占用、格式不符等）。</summary>
    Failed = 3,
}

/// <summary>
/// 用户词条：采纳云联想时自动学到，或在词库页手动添加。
/// </summary>
/// <remarks>
/// 不可变。改动一律走「生成新实例 + 整体替换」，这样跨线程读取拿到的永远是自洽的一份。
/// <see cref="Pinyin"/> 与 <see cref="Initials"/> 都是无调小写，前者音节不分隔。
/// </remarks>
public sealed record UserWord
{
    /// <summary>词面。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>无调全拼，音节之间不分隔，例如 <c>yisitalu</c>。</summary>
    public string Pinyin { get; init; } = string.Empty;

    /// <summary>归一后的声母串，例如 <c>ystl</c>。</summary>
    public string Initials { get; init; } = string.Empty;

    /// <summary>用户最后一次输入这个音时的原始按键串，仅用于展示。</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>被采纳或手动使用过的次数。</summary>
    public int Hits { get; init; }

    /// <summary>权重档位。</summary>
    public WordAdjustment Adjustment { get; init; } = WordAdjustment.Normal;

    /// <summary>写入微软拼音时的候选位置 1~9；0 表示跟随全局默认。</summary>
    public int UdpPosition { get; init; }

    /// <summary>最近一次同步状态。</summary>
    public UdpSyncState UdpState { get; init; } = UdpSyncState.Pending;

    /// <summary>首次学到的时刻。</summary>
    public DateTimeOffset LearnedAt { get; init; }

    /// <summary>最近一次使用或改动的时刻，用于超限淘汰。</summary>
    public DateTimeOffset LastUsedAt { get; init; }

    /// <summary>是否被用户遗忘。</summary>
    public bool IsHidden => Adjustment == WordAdjustment.Hidden;
}

/// <summary>
/// 内置词的覆盖记录：只调权重/遗忘，不改词库文件本身。
/// </summary>
/// <remarks>
/// 必须带上 <see cref="Pinyin"/>：简拼展开返回的是拼音串而不是词面，
/// 没有拼音就没办法把「被遗忘的词」从展开结果里剔掉。
/// </remarks>
public sealed record WordOverride
{
    /// <summary>词面。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>无调全拼。</summary>
    public string Pinyin { get; init; } = string.Empty;

    /// <summary>权重档位。</summary>
    public WordAdjustment Adjustment { get; init; } = WordAdjustment.Normal;
}

using BetterPinyin.Core.Wordbook;

namespace BetterPinyin.App.Features.CloudAssist.Views;

/// <summary>
/// 词库列表的一行。不可变：状态变化时整行替换，靠 <c>Replace</c> 通知界面，
/// 不实现 <c>INotifyPropertyChanged</c>。
/// </summary>
/// <param name="Text">词面。</param>
/// <param name="Pinyin">无调全拼。</param>
/// <param name="IsUser">是否来自用户词库。</param>
/// <param name="Rank">内置词的热度排名；用户词为 -1。</param>
/// <param name="Hits">用户词的使用次数。</param>
/// <param name="Adjustment">权重档位。</param>
/// <param name="SyncState">同步状态。</param>
internal sealed record WordbookRow(
    string Text,
    string Pinyin,
    bool IsUser,
    int Rank,
    int Hits,
    WordAdjustment Adjustment,
    UdpSyncState SyncState)
{
    /// <summary>来源列。</summary>
    public string Source => IsUser ? "我的词" : "内置";

    /// <summary>热度列。</summary>
    public string Heat => IsUser
        ? (Hits > 1 ? $"用过 {Hits} 次" : "新词")
        : $"#{Rank + 1}";

    /// <summary>状态列。</summary>
    public string StateText => Adjustment switch
    {
        WordAdjustment.Hidden => "已遗忘",
        WordAdjustment.Boosted => "已提升",
        WordAdjustment.Reduced => "已降低",
        _ => IsUser
            ? SyncState switch
            {
                UdpSyncState.Synced => "已同步",
                UdpSyncState.Pending => "待同步",
                UdpSyncState.Conflict => "冲突",
                UdpSyncState.Failed => "失败",
                _ => "正常",
            }
            : "—",
    };
}

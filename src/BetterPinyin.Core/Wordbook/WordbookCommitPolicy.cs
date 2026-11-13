using BetterPinyin.Core.Settings;

namespace BetterPinyin.Core.Wordbook;

/// <summary>
/// 一次云候选采纳之后，要不要把它记下来（进用户词库、进而写自定义短语）。
/// </summary>
/// <remarks>
/// 单独抽出来是因为这条判断牵连三处：词库条目、短语文件，以及升级后对「待同步」的补写。
/// 只跳过写短语而照常学词，会让词条停在 Pending，下次启动又被补写回去，开关等于没做。
/// 所以「学不学」必须和「写不写」用同一个判断，放在一个地方。
/// </remarks>
public static class WordbookCommitPolicy
{
    /// <summary>
    /// 判断这次采纳是否需要记录。
    /// </summary>
    /// <param name="isExtended">是否为扩展云联想的候选（通常是整句）。</param>
    /// <param name="settings">当前设置。</param>
    /// <returns>需要记录返回 true。</returns>
    public static bool ShouldRecord(bool isExtended, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.WordbookLearnOnAccept)
        {
            return false;
        }

        // 扩展词默认只上屏、不记账：整句写进短语库会长期占住原生候选的第一位，
        // 而它往往只用一次。严格云联想（单个词）不受影响。
        return !isExtended || settings.PhraseWriteExtended;
    }
}

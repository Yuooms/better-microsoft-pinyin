namespace BetterPinyin.App.Features.CloudAssist.Views;

/// <summary>词库页里的一行：微软拼音自定义短语。</summary>
/// <param name="Pinyin">触发拼音（全拼）。</param>
/// <param name="Text">输出文本。</param>
/// <param name="Source">来源标注：本程序学到的 / 手动添加的 / 系统里原有的。</param>
/// <param name="Position">记录里的位置值（输入法不按它排序，仅展示）。</param>
/// <param name="Initials">同时写入的简拼记录；没有时为空串。</param>
internal sealed record PhraseRow(string Pinyin, string Text, string Source, int Position, string Initials = "")
{
    /// <summary>列表里显示的触发码：同时有简拼时写成「全拼 · 简拼」。</summary>
    public string Code => Initials.Length > 0 ? $"{Pinyin} · {Initials}" : Pinyin;
}

/// <summary>词库页「自学习」里的一行：输入法自己记下的词。</summary>
/// <param name="Text">学到的词。</param>
/// <param name="Pinyin">按单字读音与简拼推断出的全拼；推不出时为空。</param>
/// <param name="Jianpin">文件里保存的简拼。</param>
/// <param name="Length">字数。</param>
internal sealed record LearnedRow(string Text, string Pinyin, string Jianpin, int Length);

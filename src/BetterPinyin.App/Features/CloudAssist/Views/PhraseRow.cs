namespace BetterPinyin.App.Features.CloudAssist.Views;

/// <summary>词库页里的一行：微软拼音自定义短语。</summary>
/// <param name="Pinyin">触发拼音。</param>
/// <param name="Text">输出文本。</param>
/// <param name="Source">来源标注：本程序学到的 / 手动添加的 / 系统里原有的。</param>
/// <param name="Position">记录里的位置值（输入法不按它排序，仅展示）。</param>
internal sealed record PhraseRow(string Pinyin, string Text, string Source, int Position);

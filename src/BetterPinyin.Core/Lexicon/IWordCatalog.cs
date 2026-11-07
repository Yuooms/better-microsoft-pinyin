namespace BetterPinyin.Core.Lexicon;

/// <summary>
/// 词表的一条记录，供词库管理界面展示与排序使用。
/// </summary>
/// <param name="Text">汉字词面。</param>
/// <param name="Pinyin">无调全拼，音节之间不分隔。</param>
/// <param name="Index">词表下标，同时也是热度排名（0 为最高频）。</param>
/// <param name="Frequency">构建期的原始词频，仅用于展示。</param>
public readonly record struct WordEntryView(string Text, string Pinyin, int Index, int Frequency);

/// <summary>
/// 只读词表：能按热度顺序遍历，也能按汉字或拼音检索。
/// </summary>
/// <remarks>
/// 与 <see cref="BetterPinyin.Core.Pinyin.IPinyinLexicon"/> 分开，是因为「读音校验 / 简拼展开」
/// 和「浏览词表」是两件事：前者每次按键都要用，必须极快；后者只在设置页打开时用，
/// 可以慢一点、可以分页。合并进同一个接口会逼着所有实现（包括测试里的假词库）
/// 都去实现一堆用不到的成员。
/// </remarks>
public interface IWordCatalog
{
    /// <summary>是否带词表。旧版格式或解析失败时为 false，此时其余成员返回空。</summary>
    bool HasWordTable { get; }

    /// <summary>词条数量。</summary>
    int Count { get; }

    /// <summary>
    /// 按下标取一条记录。
    /// </summary>
    /// <param name="index">词表下标。</param>
    /// <param name="entry">取到的记录。</param>
    /// <returns>下标越界或没有词表时返回 false。</returns>
    bool TryGet(int index, out WordEntryView entry);

    /// <summary>
    /// 检索词表，结果按下标升序（即热度从高到低）。
    /// </summary>
    /// <param name="query">
    /// 查询串。全 ASCII 字母时同时匹配「全拼前缀」与「声母前缀」（<c>ystl</c> 与 <c>yisita</c>
    /// 都能命中「伊斯塔露」）；含汉字时按 UTF-8 子串匹配。
    /// </param>
    /// <param name="maxResults">最多返回多少条。</param>
    /// <returns>命中的词表下标。</returns>
    IReadOnlyList<int> Search(string query, int maxResults);
}

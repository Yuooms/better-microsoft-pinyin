namespace BetterPinyin.Core.Pinyin;

/// <summary>
/// 读音词库：提供单字读音集合与简拼展开。
/// </summary>
/// <remarks>
/// 读音校验只需要「每个字有哪几种读法」，不需要词组读音：
/// 候选词是云端给的、输入是已知的，对齐本来就该由匹配器做。
/// 词表因此只在两件事上有用——简拼展开、以及给候选一个「像不像真词」的置信度。
/// </remarks>
public interface IPinyinLexicon
{
    /// <summary>词库是否已加载。</summary>
    bool IsLoaded { get; }

    /// <summary>词条数量（单字表不计）。</summary>
    int WordCount { get; }

    /// <summary>
    /// 取一个字的读音集合。
    /// </summary>
    /// <param name="value">汉字。</param>
    /// <param name="syllableIds">
    /// 固定长度槽位，0 表示空槽，非 0 表示「音节 id + 1」。
    /// 一个字的读法通常有 1~3 种，按常用程度排序。
    /// </param>
    /// <returns>该字是否收录。</returns>
    bool TryGetReadings(char value, out ReadOnlySpan<ushort> syllableIds);

    /// <summary>
    /// 把简拼展开成全拼串。
    /// </summary>
    /// <param name="initials">声母串，例如 <c>ystl</c>。</param>
    /// <param name="max">最多返回几条。</param>
    /// <returns>全拼串，例如 <c>yisitalu</c>；未命中返回空。</returns>
    IReadOnlyList<string> ExpandAbbreviation(string initials, int max);
}

/// <summary>空词库：词库缺失或加载失败时使用，匹配器退回「放行」模式。</summary>
public sealed class NullPinyinLexicon : IPinyinLexicon, Lexicon.IWordCatalog
{
    /// <summary>共享实例。</summary>
    public static NullPinyinLexicon Instance { get; } = new();

    /// <inheritdoc />
    public bool IsLoaded => false;

    /// <inheritdoc />
    public int WordCount => 0;

    /// <inheritdoc />
    public bool HasWordTable => false;

    /// <inheritdoc />
    public int Count => 0;

    /// <inheritdoc />
    public bool TryGet(int index, out Lexicon.WordEntryView entry)
    {
        entry = default;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<int> Search(string query, int maxResults) => Array.Empty<int>();

    /// <inheritdoc />
    public bool TryGetReadings(char value, out ReadOnlySpan<ushort> syllableIds)
    {
        syllableIds = default;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ExpandAbbreviation(string initials, int max) => Array.Empty<string>();
}

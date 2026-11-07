using BetterPinyin.Core.Lexicon;
using BetterPinyin.Core.Wordbook;

namespace BetterPinyin.Core.Pinyin;

/// <summary>
/// 把用户词库叠在内置词库之上。
/// </summary>
/// <remarks>
/// <para>
/// 只做两件事：简拼展开时把用户词排在内置词前面；读音校验原样委托给内置层。
/// 这样 <see cref="PinyinMatcher"/> 与云联想控制器一行都不用改。
/// </para>
/// <para>
/// <see cref="IsLoaded"/> 必须跟随内置层而不是「或」：内置词库损坏时匹配器要退回
/// <c>Permissive</c>（全部放行），如果这里返回 true，匹配器会以为读音表可用，
/// 结果每个候选都因为查不到读音被判 <c>None</c>——比没有词库更糟。
/// </para>
/// </remarks>
public sealed class CompositePinyinLexicon : IPinyinLexicon, IWordCatalog
{
    private readonly IPinyinLexicon _builtin;
    private readonly IWordCatalog _catalog;
    private readonly UserWordbook _book;

    /// <summary>构造组合词库。</summary>
    /// <param name="builtin">内置词库。</param>
    /// <param name="book">用户词库。</param>
    public CompositePinyinLexicon(IPinyinLexicon builtin, UserWordbook book)
    {
        ArgumentNullException.ThrowIfNull(builtin);
        ArgumentNullException.ThrowIfNull(book);

        _builtin = builtin;
        _book = book;
        _catalog = builtin as IWordCatalog ?? NullPinyinLexicon.Instance;
    }

    /// <inheritdoc />
    public bool IsLoaded => _builtin.IsLoaded;

    /// <inheritdoc />
    public int WordCount => _builtin.WordCount + _book.Count;

    /// <inheritdoc />
    public bool TryGetReadings(char value, out ReadOnlySpan<ushort> syllableIds) =>
        _builtin.TryGetReadings(value, out syllableIds);

    /// <inheritdoc />
    public IReadOnlyList<string> ExpandAbbreviation(string initials, int max)
    {
        if (string.IsNullOrEmpty(initials) || max <= 0)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>(max);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 用户自己的词优先：这是「输入法记住了这个词」最直接的体现。
        foreach (var word in _book.MatchInitials(initials, max))
        {
            if (seen.Add(word.Pinyin))
            {
                results.Add(word.Pinyin);
            }
        }

        if (results.Count < max)
        {
            var builtin = _builtin.ExpandAbbreviation(initials, max + 8);
            foreach (var pinyin in builtin
                         .Where(p => !_book.IsPinyinHidden(p))
                         .OrderBy(p => Weight(_book.AdjustmentOfPinyin(p))))
            {
                if (seen.Add(pinyin))
                {
                    results.Add(pinyin);
                    if (results.Count >= max)
                    {
                        break;
                    }
                }
            }
        }

        return results.Count <= max ? results : results.GetRange(0, max);
    }

    /// <inheritdoc />
    public bool HasWordTable => _catalog.HasWordTable;

    /// <inheritdoc />
    public int Count => _catalog.Count;

    /// <inheritdoc />
    public bool TryGet(int index, out WordEntryView entry) => _catalog.TryGet(index, out entry);

    /// <inheritdoc />
    public IReadOnlyList<int> Search(string query, int maxResults) => _catalog.Search(query, maxResults);

    private static int Weight(WordAdjustment adjustment) => adjustment switch
    {
        WordAdjustment.Boosted => 0,
        WordAdjustment.Normal => 1,
        WordAdjustment.Reduced => 2,
        _ => 3,
    };
}

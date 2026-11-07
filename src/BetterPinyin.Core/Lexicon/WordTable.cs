using System.Buffers.Binary;
using System.Text;
using BetterPinyin.Core.Pinyin;

namespace BetterPinyin.Core.Lexicon;

/// <summary>
/// 词库文件里的「词表」区段：汉字词面 + 词频 + 音节序列。
/// </summary>
/// <remarks>
/// <para>
/// 区段布局（小端，偏移相对区段起点）：
/// <code>
/// +0   u32 wordCount
/// +4   u32 textPoolBytes
/// +8   u32 syllablePoolBytes
/// +12  u32 recordSize（必须为 16）
/// +16  记录区 wordCount × 16 字节
///       +0  u32 textOffset（相对文本池）
///       +4  u32 syllableOffset（相对音节池，字节）
///       +8  u32 frequency
///       +12 u8  textByteLength
///       +13 u8  syllableCount
///       +14 u16 flags（保留）
///       文本池：UTF-8 连续存放，无分隔符
///       音节池：u16 原始音节 id
/// </code>
/// </para>
/// <para>
/// 解析全程只用 <see cref="ReadOnlySpan{T}"/> 切片，不建对象；只有真正取某一条时才解码字符串。
/// 因此 15 万条也只是一次 4.5 MB 的文件读取，常驻内存等于文件本身。
/// </para>
/// </remarks>
public sealed class WordTable
{
    private readonly byte[] _data;
    private readonly SyllableTable _table;
    private readonly int _count;
    private readonly int _recordsOffset;
    private readonly int _textPoolOffset;
    private readonly int _textPoolBytes;
    private readonly int _syllablesOffset;
    private readonly int _syllablesBytes;

    private WordTable(
        byte[] data,
        SyllableTable table,
        int count,
        int recordsOffset,
        int textPoolOffset,
        int textPoolBytes,
        int syllablesOffset,
        int syllablesBytes)
    {
        _data = data;
        _table = table;
        _count = count;
        _recordsOffset = recordsOffset;
        _textPoolOffset = textPoolOffset;
        _textPoolBytes = textPoolBytes;
        _syllablesOffset = syllablesOffset;
        _syllablesBytes = syllablesBytes;
    }

    /// <summary>词条数量。</summary>
    public int Count => _count;

    /// <summary>
    /// 解析词表区段。
    /// </summary>
    /// <param name="data">整个词库文件。</param>
    /// <param name="offset">区段起始偏移。</param>
    /// <param name="length">区段长度。</param>
    /// <param name="syllables">音节表。</param>
    /// <param name="table">解析结果；失败为 null。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryCreate(
        byte[] data,
        int offset,
        int length,
        SyllableTable syllables,
        out WordTable? table,
        out string? error)
    {
        table = null;
        error = null;

        if (length < 16)
        {
            error = "词表区段过短";
            return false;
        }

        var span = data.AsSpan(offset, length);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(span);
        var textPoolBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4));
        var syllablesBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8));
        var recordSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));

        if (recordSize != 16)
        {
            error = $"词表记录长度不支持：{recordSize}";
            return false;
        }

        if (count < 0)
        {
            error = "词表条数为负";
            return false;
        }

        var recordsOffset = offset + 16;
        var recordsEnd = 16L + ((long)count * recordSize);
        if (recordsEnd > length)
        {
            error = "词表记录区越界";
            return false;
        }

        var textPoolOffset = offset + (int)recordsEnd;
        if ((long)textPoolBytes < 0 || recordsEnd + textPoolBytes > length)
        {
            error = "词表文本池越界";
            return false;
        }

        var syllablesOffset = textPoolOffset + textPoolBytes;
        if ((long)syllablesBytes < 0 || recordsEnd + textPoolBytes + syllablesBytes > length)
        {
            error = "词表音节池越界";
            return false;
        }

        table = new WordTable(
            data,
            syllables,
            count,
            recordsOffset,
            textPoolOffset,
            textPoolBytes,
            syllablesOffset,
            syllablesBytes);
        return true;
    }

    /// <summary>
    /// 按下标取一条记录。
    /// </summary>
    /// <param name="index">词表下标。</param>
    /// <param name="entry">取到的记录。</param>
    /// <returns>下标越界返回 false。</returns>
    public bool TryGet(int index, out WordEntryView entry)
    {
        entry = default;
        if (index < 0 || index >= _count)
        {
            return false;
        }

        if (!TryReadRecord(index, out var textOffset, out var textLength, out var syllableOffset, out var syllableCount, out var frequency))
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(_data.AsSpan(_textPoolOffset + textOffset, textLength));
        entry = new WordEntryView(text, ReadPinyin(syllableOffset, syllableCount), index, (int)frequency);
        return true;
    }

    /// <summary>
    /// 检索词表，结果按下标升序（热度从高到低）。
    /// </summary>
    /// <param name="query">查询串。</param>
    /// <param name="maxResults">最多返回多少条。</param>
    /// <returns>命中的词表下标。</returns>
    public IReadOnlyList<int> Search(string query, int maxResults)
    {
        if (maxResults <= 0)
        {
            return Array.Empty<int>();
        }

        var text = query?.Trim() ?? string.Empty;
        var results = new List<int>(Math.Min(maxResults, 64));

        if (text.Length == 0)
        {
            var take = Math.Min(maxResults, _count);
            for (var i = 0; i < take; i++)
            {
                results.Add(i);
            }

            return results;
        }

        if (IsAsciiLetters(text))
        {
            Span<char> lower = text.Length <= 32 ? stackalloc char[text.Length] : new char[text.Length];
            for (var i = 0; i < text.Length; i++)
            {
                lower[i] = char.ToLowerInvariant(text[i]);
            }

            for (var i = 0; i < _count && results.Count < maxResults; i++)
            {
                if (MatchesAscii(i, lower))
                {
                    results.Add(i);
                }
            }

            return results;
        }

        // 含汉字：把查询编成 UTF-8，在文本池里做子串匹配，不逐条解码。
        var needle = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < _count && results.Count < maxResults; i++)
        {
            if (!TryReadRecord(i, out var textOffset, out var textLength, out _, out _, out _))
            {
                continue;
            }

            if (textLength < needle.Length)
            {
                continue;
            }

            var slice = _data.AsSpan(_textPoolOffset + textOffset, textLength);
            if (slice.IndexOf(needle) >= 0)
            {
                results.Add(i);
            }
        }

        return results;
    }

    /// <summary>取某个下标对应的完整拼音串，供诊断与测试使用。</summary>
    /// <param name="index">词表下标。</param>
    /// <returns>拼接后的全拼；越界返回空串。</returns>
    public string GetPinyin(int index)
    {
        if (index < 0 || index >= _count)
        {
            return string.Empty;
        }

        return TryReadRecord(index, out _, out _, out var syllableOffset, out var syllableCount, out _)
            ? ReadPinyin(syllableOffset, syllableCount)
            : string.Empty;
    }

    private bool TryReadRecord(
        int index,
        out int textOffset,
        out int textLength,
        out int syllableOffset,
        out int syllableCount,
        out uint frequency)
    {
        textOffset = 0;
        textLength = 0;
        syllableOffset = 0;
        syllableCount = 0;
        frequency = 0;

        var span = _data.AsSpan(_recordsOffset + (index * 16), 16);
        textOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span);
        syllableOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4));
        frequency = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8));
        textLength = span[12];
        syllableCount = span[13];

        if (textOffset < 0 || textLength <= 0 || textOffset + textLength > _textPoolBytes)
        {
            return false;
        }

        if (syllableOffset < 0 || (syllableOffset & 1) != 0 || (syllableOffset + (syllableCount * 2)) > _syllablesBytes)
        {
            return false;
        }

        return true;
    }

    private string ReadPinyin(int syllableOffset, int syllableCount)
    {
        var builder = new StringBuilder(syllableCount * 5);
        for (var i = 0; i < syllableCount; i++)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(
                _data.AsSpan(_syllablesOffset + syllableOffset + (i * 2), 2));
            if (id < _table.Count)
            {
                builder.Append(_table.GetCanonical(id));
            }
        }

        return builder.ToString();
    }

    private bool MatchesAscii(int index, ReadOnlySpan<char> query)
    {
        if (!TryReadRecord(index, out _, out _, out var syllableOffset, out var syllableCount, out _))
        {
            return false;
        }

        return MatchesFullPinyin(syllableOffset, syllableCount, query)
            || MatchesInitials(syllableOffset, syllableCount, query);
    }

    /// <summary>全拼前缀匹配：<c>yisita</c> → 伊斯塔露。</summary>
    private bool MatchesFullPinyin(int syllableOffset, int syllableCount, ReadOnlySpan<char> query)
    {
        var position = 0;
        for (var i = 0; i < syllableCount; i++)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(
                _data.AsSpan(_syllablesOffset + syllableOffset + (i * 2), 2));
            if (id >= _table.Count)
            {
                return false;
            }

            var spelling = _table.GetCanonical(id);
            for (var c = 0; c < spelling.Length; c++)
            {
                if (position >= query.Length)
                {
                    return true;
                }

                if (query[position] != spelling[c])
                {
                    return false;
                }

                position++;
            }
        }

        return position == query.Length;
    }

    /// <summary>声母前缀匹配：<c>ystl</c> → 伊斯塔露。</summary>
    private bool MatchesInitials(int syllableOffset, int syllableCount, ReadOnlySpan<char> query)
    {
        if (syllableCount < query.Length)
        {
            return false;
        }

        for (var i = 0; i < query.Length; i++)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(
                _data.AsSpan(_syllablesOffset + syllableOffset + (i * 2), 2));
            if (id >= _table.Count)
            {
                return false;
            }

            if (SyllableTable.NormalizeInitial(_table.GetInitial(id)) != query[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetters(string value)
    {
        foreach (var ch in value)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z')))
            {
                return false;
            }
        }

        return true;
    }
}

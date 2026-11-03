using System.Buffers.Binary;
using BetterPinyin.Core.Pinyin;

namespace BetterPinyin.Core.Lexicon;

/// <summary>
/// 从内置二进制词库读取读音与简拼索引。
/// </summary>
/// <remarks>
/// 加载只做三件事：读文件、校验头部、记录各区段的偏移。真正查询时直接在字节数组上
/// 切片与二分，因此加载在 10~30 ms 量级，常驻内存就是文件本身的大小。
/// 文件缺失、魔数不符、版本不符、音节表对不上，一律返回 null，由调用方降级为空词库。
/// </remarks>
public sealed class BinaryLexicon : IPinyinLexicon
{
    private readonly byte[] _data;
    private readonly SyllableTable _table;
    private readonly SectionRef[] _sections;
    private readonly CharRange[] _charRanges;
    private readonly int _wordCount;

    private BinaryLexicon(byte[] data, SyllableTable table, SectionRef[] sections, CharRange[] ranges, int wordCount)
    {
        _data = data;
        _table = table;
        _sections = sections;
        _charRanges = ranges;
        _wordCount = wordCount;
    }

    /// <inheritdoc />
    public bool IsLoaded => true;

    /// <inheritdoc />
    public int WordCount => _wordCount;

    /// <summary>词库文件字节数。</summary>
    public int ByteLength => _data.Length;

    /// <summary>
    /// 尝试加载词库。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="table">音节表，用于校验文件里的音节是否与当前版本一致。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>成功返回实例，失败返回 null。</returns>
    public static BinaryLexicon? TryLoad(string path, SyllableTable table, out string? error)
    {
        error = null;

        if (!File.Exists(path))
        {
            error = $"词库不存在：{path}";
            return null;
        }

        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < LexiconFormat.HeaderSize)
            {
                error = "文件过短";
                return null;
            }

            if (!data.AsSpan(0, 4).SequenceEqual(LexiconFormat.Magic))
            {
                error = "魔数不符";
                return null;
            }

            var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
            if (version != LexiconFormat.Version)
            {
                error = $"版本不符：文件 {version}，程序 {LexiconFormat.Version}";
                return null;
            }

            var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2));
            if (sectionCount > (int)LexiconFormat.Section.Count)
            {
                error = $"区段数异常：{sectionCount}";
                return null;
            }

            var sections = new SectionRef[(int)LexiconFormat.Section.Count];
            for (var i = 0; i < sectionCount; i++)
            {
                var entry = LexiconFormat.HeaderSize + (i * LexiconFormat.SectionEntrySize);
                var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry, 4));
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 4, 4));
                if (offset < 0 || length < 0 || offset + length > data.Length)
                {
                    error = $"区段 {i} 越界";
                    return null;
                }

                sections[i] = new SectionRef(offset, length);
            }

            if (!ValidateSyllables(data, sections[(int)LexiconFormat.Section.Syllables], table))
            {
                error = "音节表与程序不一致";
                return null;
            }

            var ranges = ReadRanges(data, sections[(int)LexiconFormat.Section.CharReadings], out error);
            if (ranges is null)
            {
                return null;
            }

            var wordCount = ReadWordCount(data, sections[(int)LexiconFormat.Section.Initials]);

            return new BinaryLexicon(data, table, sections, ranges, wordCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// 在程序目录下加载默认词库。
    /// </summary>
    /// <param name="table">音节表。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>词库实例；找不到时返回空词库。</returns>
    public static IPinyinLexicon LoadDefault(SyllableTable table, out string? error)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "lexicon.bin");
        var lexicon = TryLoad(path, table, out error);
        return (IPinyinLexicon?)lexicon ?? NullPinyinLexicon.Instance;
    }

    /// <inheritdoc />
    public bool TryGetReadings(char value, out ReadOnlySpan<ushort> syllableIds)
    {
        syllableIds = default;

        foreach (var range in _charRanges)
        {
            if (value < range.Start || value > range.End)
            {
                continue;
            }

            // char 与 uint 相减会提升为 long，必须先截回 int，否则 AsSpan 选不到重载。
            var index = (int)((value - range.Start) * range.Slots);
            var slots = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                _data.AsSpan(range.DataOffset + (index * 2), range.Slots * 2));

            // 槽位里 0 表示空，非 0 是「音节 id + 1」。
            foreach (var raw in slots)
            {
                if (raw != 0)
                {
                    syllableIds = slots;
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ExpandAbbreviation(string initials, int max)
    {
        if (string.IsNullOrEmpty(initials) || max <= 0)
        {
            return Array.Empty<string>();
        }

        var section = _sections[(int)LexiconFormat.Section.Initials];
        if (section.Length < 8)
        {
            return Array.Empty<string>();
        }

        var span = _data.AsSpan(section.Offset, section.Length);
        var entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        if (entryCount <= 0)
        {
            return Array.Empty<string>();
        }

        // 索引按归一后的声母存（n→l、f→h），查询也要归一，否则「你」的简拼对不上。
        Span<char> key = initials.Length <= 16 ? stackalloc char[initials.Length] : new char[initials.Length];
        for (var i = 0; i < initials.Length; i++)
        {
            key[i] = SyllableTable.NormalizeInitial(initials[i]);
        }

        var offsetsStart = 8;
        var lo = 0;
        var hi = entryCount - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var entryOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                span.Slice(offsetsStart + (mid * 4), 4));

            var keyLength = span[entryOffset];
            var keyText = span.Slice(entryOffset + 1, keyLength);
            var comparison = CompareKey(keyText, key);

            if (comparison == 0)
            {
                found = entryOffset;
                break;
            }

            if (comparison < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
        {
            return Array.Empty<string>();
        }

        var cursor = found + 1 + span[found];
        var optionCount = span[cursor];
        cursor++;

        if (optionCount == 0)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>(Math.Min(optionCount, max));
        for (var i = 0; i < optionCount && results.Count < max; i++)
        {
            var syllableCount = span[cursor];
            cursor++;

            var builder = new System.Text.StringBuilder(syllableCount * 5);
            for (var s = 0; s < syllableCount; s++)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(cursor, 2));
                cursor += 2;

                if (id < _table.Count)
                {
                    builder.Append(_table.GetCanonical(id));
                }
            }

            if (builder.Length > 0)
            {
                results.Add(builder.ToString());
            }
        }

        return results;
    }

    private static bool ValidateSyllables(byte[] data, SectionRef section, SyllableTable table)
    {
        if (section.Length < 2)
        {
            return false;
        }

        var span = data.AsSpan(section.Offset, section.Length);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(span);
        if (count != table.Count)
        {
            return false;
        }

        var cursor = 2;
        for (var i = 0; i < count; i++)
        {
            var length = span[cursor];
            cursor++;
            var spelling = System.Text.Encoding.ASCII.GetString(span.Slice(cursor, length));
            cursor += length;

            if (!spelling.Equals(table.GetCanonical(i), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static CharRange[]? ReadRanges(byte[] data, SectionRef section, out string? error)
    {
        error = null;
        if (section.Length < 2)
        {
            error = "缺少单字读音区段";
            return null;
        }

        var span = data.AsSpan(section.Offset, section.Length);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var ranges = new CharRange[count];
        var cursor = 2;

        for (var i = 0; i < count; i++)
        {
            var start = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor, 4));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor + 4, 4));
            var slots = span[cursor + 8];
            // 9 字节头 + 1 字节对齐填充，保证槽位数据从偶数偏移开始。
            cursor += 10;

            var bytes = (int)length * slots * 2;
            if (cursor + bytes > span.Length)
            {
                error = "单字读音区段越界";
                return null;
            }

            ranges[i] = new CharRange(start, start + length - 1, slots, section.Offset + cursor);
            cursor += bytes;
        }

        return ranges;
    }

    private static int ReadWordCount(byte[] data, SectionRef section)
    {
        if (section.Length < 8)
        {
            return 0;
        }

        return (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(section.Offset, 4));
    }

    /// <summary>按 ASCII 序比较词库里的字节键与字符串，避免二分查找产生临时字符串。</summary>
    private static int CompareKey(ReadOnlySpan<byte> key, ReadOnlySpan<char> text)
    {
        var length = Math.Min(key.Length, text.Length);
        for (var i = 0; i < length; i++)
        {
            var left = key[i];
            var right = text[i];
            if (left != right)
            {
                return left < right ? -1 : 1;
            }
        }

        return key.Length.CompareTo(text.Length);
    }

    private readonly record struct SectionRef(int Offset, int Length);

    private readonly record struct CharRange(uint Start, uint End, byte Slots, int DataOffset);
}

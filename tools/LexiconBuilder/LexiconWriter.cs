using System.Text;
using BetterPinyin.Core.Lexicon;
using BetterPinyin.Core.Pinyin;

namespace BetterPinyin.Tools.LexiconBuilder;

/// <summary>把中间结构写成二进制词库。</summary>
internal static class LexiconWriter
{
    private const int Slots = 3;
    private const int MaxOptionsPerInitials = 4;

    private static readonly (int Start, int Count)[] CharBlocks =
    {
        (0x3400, 0x4DBF - 0x3400 + 1),
        (0x4E00, 0x9FFF - 0x4E00 + 1),
        (0xF900, 0xFAFF - 0xF900 + 1),
    };

    public static void Write(
        string path,
        SyllableTable table,
        Dictionary<int, List<int>> characters,
        List<WordEntry> words,
        BuildReport report)
    {
        var index = BuildIndex(words, table);

        var sections = new List<byte[]>
        {
            BuildSyllables(table),
            BuildCharacters(characters),
            BuildInitials(words.Count, index),
        };

        WriteFile(path, sections);
    }

    /// <summary>
    /// 简拼索引：归一化声母串 → 最多 4 个候选词。
    /// </summary>
    private static List<KeyValuePair<string, List<WordEntry>>> BuildIndex(
        List<WordEntry> words,
        SyllableTable table)
    {
        var grouped = new Dictionary<string, Dictionary<string, WordEntry>>(StringComparer.Ordinal);

        foreach (var word in words)
        {
            var key = string.Concat(word.Syllables.Select(id =>
                SyllableTable.NormalizeInitial(table.GetInitial(id))));

            if (key.Length == 0)
            {
                continue;
            }

            if (!grouped.TryGetValue(key, out var options))
            {
                options = new Dictionary<string, WordEntry>(StringComparer.Ordinal);
                grouped[key] = options;
            }

            if (!options.TryGetValue(word.Pinyin, out var existing) || existing.Frequency < word.Frequency)
            {
                options[word.Pinyin] = word;
            }
        }

        var result = new List<KeyValuePair<string, List<WordEntry>>>(grouped.Count);
        foreach (var pair in grouped)
        {
            var top = pair.Value.Values
                .OrderByDescending(w => w.Frequency)
                .ThenBy(w => w.Pinyin, StringComparer.Ordinal)
                .Take(MaxOptionsPerInitials)
                .ToList();
            result.Add(new KeyValuePair<string, List<WordEntry>>(pair.Key, top));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return result;
    }

    private static byte[] BuildSyllables(SyllableTable table)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write((ushort)table.Count);
        for (var id = 0; id < table.Count; id++)
        {
            var bytes = Encoding.ASCII.GetBytes(table.GetCanonical(id));
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildCharacters(Dictionary<int, List<int>> characters)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write((ushort)CharBlocks.Length);

        foreach (var (start, count) in CharBlocks)
        {
            writer.Write((uint)start);
            writer.Write((uint)count);
            writer.Write((byte)Slots);
            writer.Write((byte)0); // 对齐填充，保证槽位从偶数偏移开始

            for (var offset = 0; offset < count; offset++)
            {
                characters.TryGetValue(start + offset, out var ids);
                for (var slot = 0; slot < Slots; slot++)
                {
                    var value = ids is not null && slot < ids.Count ? ids[slot] + 1 : 0;
                    writer.Write((ushort)value);
                }
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildInitials(
        int wordCount,
        List<KeyValuePair<string, List<WordEntry>>> index)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write((uint)wordCount);
        writer.Write((uint)index.Count);

        var cursor = 8 + (index.Count * 4);
        foreach (var pair in index)
        {
            writer.Write((uint)cursor);
            cursor += EntryLength(pair.Key, pair.Value);
        }

        foreach (var pair in index)
        {
            var keyBytes = Encoding.ASCII.GetBytes(pair.Key);
            writer.Write((byte)keyBytes.Length);
            writer.Write(keyBytes);
            writer.Write((byte)pair.Value.Count);

            foreach (var word in pair.Value)
            {
                writer.Write((byte)word.Syllables.Length);
                foreach (var id in word.Syllables)
                {
                    writer.Write((ushort)id);
                }
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static int EntryLength(string key, List<WordEntry> options)
    {
        var length = 1 + Encoding.ASCII.GetByteCount(key) + 1;
        foreach (var word in options)
        {
            length += 1 + (word.Syllables.Length * 2);
        }

        return length;
    }

    private static void WriteFile(string path, List<byte[]> sections)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(LexiconFormat.Magic);
        writer.Write(LexiconFormat.Version);
        writer.Write((ushort)sections.Count);

        var offsets = new int[sections.Count];
        var cursor = LexiconFormat.HeaderSize + (sections.Count * LexiconFormat.SectionEntrySize);
        for (var i = 0; i < sections.Count; i++)
        {
            offsets[i] = cursor;
            cursor = Align4(cursor + sections[i].Length);
        }

        for (var i = 0; i < sections.Count; i++)
        {
            writer.Write((uint)offsets[i]);
            writer.Write((uint)sections[i].Length);
        }

        for (var i = 0; i < sections.Count; i++)
        {
            writer.Write(sections[i]);
            while (stream.Position % 4 != 0)
            {
                writer.Write((byte)0);
            }
        }

        writer.Flush();
    }

    private static int Align4(int value) => (value + 3) & ~3;
}

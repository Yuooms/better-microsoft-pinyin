using System.Globalization;
using System.Text;
using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Pinyin;

namespace BetterPinyin.Tools.LexiconBuilder;

/// <summary>一条词表记录。</summary>
/// <param name="Word">词。</param>
/// <param name="Frequency">词频，越大越常用。</param>
/// <param name="Syllables">音节 id 序列。</param>
/// <param name="Pinyin">无调全拼，用于简拼展开后去查云端。</param>
internal sealed record WordEntry(string Word, int Frequency, int[] Syllables, string Pinyin);

/// <summary>解析四份源数据，产出内存中的中间结构。</summary>
internal static class SourceLoader
{
    private const int MaxReadingsPerChar = 3;

    /// <summary>单字读音。U+4E00: yī, yí, yǐ</summary>
    public static Dictionary<int, List<int>> LoadCharacters(string path, SyllableTable table, BuildReport report)
    {
        var map = new Dictionary<int, List<int>>(32_768);

        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("U+", StringComparison.Ordinal))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 3)
            {
                continue;
            }

            if (!int.TryParse(line.AsSpan(2, colon - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
            {
                continue;
            }

            var hash = line.IndexOf('#', colon);
            var body = hash > colon ? line[(colon + 1)..hash] : line[(colon + 1)..];

            var ids = new List<int>(MaxReadingsPerChar);
            foreach (var raw in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var syllable = ToneStripper.Normalize(raw);
                if (syllable.Length == 0)
                {
                    continue;
                }

                if (!table.TryGetCanonicalId(syllable, out var id))
                {
                    report.UnknownSyllables.Add(syllable);
                    continue;
                }

                if (!ids.Contains(id) && ids.Count < MaxReadingsPerChar)
                {
                    ids.Add(id);
                }
            }

            if (ids.Count > 0)
            {
                map[codePoint] = ids;
            }
        }

        return map;
    }

    /// <summary>词组读音。两份数据合并，多音字词表（后者）覆盖前者。</summary>
    public static Dictionary<string, int[]> LoadWordReadings(
        string phrasePath,
        string pinyinProPath,
        SyllableTable table,
        BuildReport report)
    {
        var map = new Dictionary<string, int[]>(64_000, StringComparer.Ordinal);

        foreach (var line in File.ReadLines(phrasePath))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var word = line[..separator].Trim();
            var reading = line[(separator + 2)..].Trim();
            TryAdd(word, reading.Split(' ', StringSplitOptions.RemoveEmptyEntries), table, map, report, overwrite: false);
        }

        foreach (var line in File.ReadLines(pinyinProPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is 'i' or 'c' or '}' or '/')
            {
                continue;
            }

            var separator = trimmed.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var word = trimmed[..separator].Trim();
            var rest = trimmed[(separator + 2)..].Trim().TrimEnd(',');
            if (rest.Length < 2 || (rest[0] != '\'' && rest[0] != '"'))
            {
                continue;
            }

            var quote = rest[0];
            var end = rest.LastIndexOf(quote);
            if (end <= 1)
            {
                continue;
            }

            var reading = rest[1..end];
            TryAdd(word, reading.Split(' ', StringSplitOptions.RemoveEmptyEntries), table, map, report, overwrite: true);
        }

        return map;
    }

    /// <summary>
    /// 词表 + 词频。按词频降序取前 N 个，读音优先用词组表，缺失时按单字首读音拼合。
    /// </summary>
    public static List<WordEntry> LoadWords(
        string path,
        Dictionary<int, List<int>> characters,
        Dictionary<string, int[]> readings,
        SyllableTable table,
        int maxWords,
        BuildReport report)
    {
        var raw = new List<(string Word, int Frequency)>(350_000);

        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var word = parts[0];
            if (word.Length is < 2 or > 6 || !ResultSanitizer.IsAllChinese(word))
            {
                continue;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frequency) ||
                frequency <= 0)
            {
                continue;
            }

            raw.Add((word, frequency));
        }

        raw.Sort((a, b) => b.Frequency.CompareTo(a.Frequency));

        var words = new List<WordEntry>(maxWords);
        foreach (var (word, frequency) in raw)
        {
            if (words.Count >= maxWords)
            {
                break;
            }

            var syllables = Resolve(word, characters, readings);
            if (syllables is null)
            {
                report.WordsWithoutReading++;
                continue;
            }

            var pinyin = string.Concat(syllables.Select(id => table.GetCanonical(id)));
            words.Add(new WordEntry(word, frequency, syllables, pinyin));
        }

        return words;
    }

    private static void TryAdd(
        string word,
        string[] syllables,
        SyllableTable table,
        Dictionary<string, int[]> map,
        BuildReport report,
        bool overwrite)
    {
        if (word.Length is < 2 or > 8 || !ResultSanitizer.IsAllChinese(word) || syllables.Length == 0)
        {
            return;
        }

        var ids = new int[syllables.Length];
        for (var i = 0; i < syllables.Length; i++)
        {
            var syllable = ToneStripper.Normalize(syllables[i]);
            if (!table.TryGetCanonicalId(syllable, out ids[i]))
            {
                report.UnknownSyllables.Add(syllable);
                return;
            }
        }

        if (overwrite || !map.ContainsKey(word))
        {
            map[word] = ids;
        }
    }

    private static int[]? Resolve(
        string word,
        Dictionary<int, List<int>> characters,
        Dictionary<string, int[]> readings)
    {
        if (readings.TryGetValue(word, out var known))
        {
            return known;
        }

        var ids = new int[word.Length];
        for (var i = 0; i < word.Length; i++)
        {
            if (!characters.TryGetValue(word[i], out var options) || options.Count == 0)
            {
                return null;
            }

            ids[i] = options[0];
        }

        return ids;
    }
}

/// <summary>把带调拼音转成小写无调 a~z，ü 统一写成 v。</summary>
internal static class ToneStripper
{
    private static readonly Dictionary<char, char> Map = new()
    {
        ['ā'] = 'a', ['á'] = 'a', ['ǎ'] = 'a', ['à'] = 'a',
        ['ē'] = 'e', ['é'] = 'e', ['ě'] = 'e', ['è'] = 'e', ['ê'] = 'e',
        ['ī'] = 'i', ['í'] = 'i', ['ǐ'] = 'i', ['ì'] = 'i',
        ['ō'] = 'o', ['ó'] = 'o', ['ǒ'] = 'o', ['ò'] = 'o',
        ['ū'] = 'u', ['ú'] = 'u', ['ǔ'] = 'u', ['ù'] = 'u',
        ['ǖ'] = 'v', ['ǘ'] = 'v', ['ǚ'] = 'v', ['ǜ'] = 'v', ['ü'] = 'v',
        ['ń'] = 'n', ['ň'] = 'n', ['ǹ'] = 'n', ['ḿ'] = 'm',
    };

    /// <summary>归一化单个音节。</summary>
    /// <param name="raw">带调写法，例如 <c>lǜ</c> 或 <c>nu:e</c>。</param>
    /// <returns>无调小写音节。</returns>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == ':' && builder.Length > 0 && builder[^1] == 'u')
            {
                builder[^1] = 'v';
                continue;
            }

            if (Map.TryGetValue(ch, out var mapped))
            {
                builder.Append(mapped);
                continue;
            }

            if (ch is >= 'a' and <= 'z')
            {
                builder.Append(ch);
                continue;
            }

            if (ch is >= 'A' and <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}

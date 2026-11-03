using System.Globalization;
using System.Text;
using BetterPinyin.Core.Pinyin;

namespace BetterPinyin.Tools.LexiconBuilder;

/// <summary>
/// 词库构建入口。
/// </summary>
/// <remarks>
/// 输入是四份 MIT 许可的公开数据，输出是单文件二进制词库。
/// 构建器只在开发机上运行，运行期不依赖它，也不依赖任何第三方包。
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = BuildOptions.Parse(args);
        if (options is null)
        {
            PrintUsage();
            return 2;
        }

        var table = SyllableTable.Default;
        var report = new BuildReport();

        Console.WriteLine($"音节表：{table.Count} 个规范音节");
        Console.WriteLine($"单字读音：{options.PinyinData}");
        var characters = SourceLoader.LoadCharacters(options.PinyinData, table, report);

        Console.WriteLine($"词组读音：{options.PhraseData} + {options.PinyinPro}");
        var readings = SourceLoader.LoadWordReadings(options.PhraseData, options.PinyinPro, table, report);

        Console.WriteLine($"词表与词频：{options.WordFrequency}");
        var words = SourceLoader.LoadWords(
            options.WordFrequency, characters, readings, table, options.MaxWords, report);

        var indexPath = Path.GetFullPath(options.Output);
        var directory = Path.GetDirectoryName(indexPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LexiconWriter.Write(indexPath, table, characters, words, report);

        var size = new FileInfo(indexPath).Length;
        Console.WriteLine();
        Console.WriteLine($"单字表：{characters.Count} 字");
        Console.WriteLine($"词条：{words.Count}（上限 {options.MaxWords}）");
        Console.WriteLine($"输出：{indexPath}");
        Console.WriteLine($"体积：{size / 1024.0 / 1024.0:F2} MB");

        if (report.UnknownSyllables.Count > 0)
        {
            Console.WriteLine($"未识别音节：{report.UnknownSyllables.Count} 种（示例 {string.Join(", ", report.UnknownSyllables.Take(8))}）");
        }

        if (report.WordsWithoutReading > 0)
        {
            Console.WriteLine($"缺少读音而跳过：{report.WordsWithoutReading} 条");
        }

        const long limit = 8L * 1024 * 1024;
        if (size > limit)
        {
            Console.WriteLine($"✗ 超出 8 MB 上限，请降低 --max-words 或裁剪词表");
            return 3;
        }

        Console.WriteLine("✓ 构建完成");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            用法：LexiconBuilder --pinyin-data <pinyin.txt> --phrase-data <pinyin.txt>
                                 --pinyin-pro <dicts.txt> --word-freq <dict.txt>
                                 --out <lexicon.bin> [--max-words 150000]
            """);
    }
}

/// <summary>构建统计。</summary>
internal sealed class BuildReport
{
    public HashSet<string> UnknownSyllables { get; } = new(StringComparer.Ordinal);

    public int WordsWithoutReading { get; set; }
}

/// <summary>命令行参数。</summary>
internal sealed class BuildOptions
{
    public required string PinyinData { get; init; }

    public required string PhraseData { get; init; }

    public required string PinyinPro { get; init; }

    public required string WordFrequency { get; init; }

    public required string Output { get; init; }

    public int MaxWords { get; init; } = 150_000;

    public static BuildOptions? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                return null;
            }

            values[key[2..]] = args[i + 1];
            i++;
        }

        if (!values.TryGetValue("pinyin-data", out var pinyin) ||
            !values.TryGetValue("phrase-data", out var phrase) ||
            !values.TryGetValue("pinyin-pro", out var pro) ||
            !values.TryGetValue("word-freq", out var freq) ||
            !values.TryGetValue("out", out var output))
        {
            return null;
        }

        var maxWords = 150_000;
        if (values.TryGetValue("max-words", out var maxText) &&
            int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            maxWords = Math.Clamp(parsed, 1_000, 400_000);
        }

        return new BuildOptions
        {
            PinyinData = pinyin,
            PhraseData = phrase,
            PinyinPro = pro,
            WordFrequency = freq,
            Output = output,
            MaxWords = maxWords,
        };
    }
}

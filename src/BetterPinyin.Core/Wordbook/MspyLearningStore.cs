using System.Buffers.Binary;
using System.Text;
using BetterPinyin.Core.Pinyin;

namespace BetterPinyin.Core.Wordbook;

/// <summary>微软拼音自学习词库里的一条记录。</summary>
/// <param name="Text">学到的中文词或短句。</param>
/// <param name="Jianpin">文件里存的简拼，最多 3 个字母（超过 3 个音节只保留前 3 个）。</param>
/// <param name="Length">字数，等于音节数。</param>
public readonly record struct MspyLearnedWord(string Text, string Jianpin, int Length);

/// <summary>
/// 只读解析微软拼音的「自学习」词库文件
/// <c>%APPDATA%\Microsoft\InputMethod\Chs\ChsPinyinUDL.dat</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这个文件是输入法在用户反复输入某个词之后**自己**记下来的，和「用户定义的短语」
/// （<see cref="MspyPhraseStore"/>）是两套独立数据：短语由用户显式添加、原样进候选；
/// 自学习由输入法按使用行为累积，用来调整候选顺序，也会被「清除输入历史记录」清空。
/// </para>
/// <para>
/// 格式是社区逆向的结果，这里只做**读取**，任何字段不符就整份放弃，绝不写入：
/// <code>
/// 0x00  u8[4] 魔数 55 AA 88 81
/// 0x04  u8[4] 固定 02 00 60 00（记录长度 60）
/// 0x08  u8[4] 校验 55 AA 55 AA
/// 0x0C  u32   记录数（含已释放的空槽）
/// 0x10  u32   保留
/// 0x14  u32   导出时间戳 / 校验值（用途未确认，原样忽略）
/// 0x18~0x23FF 零填充
/// 0x2400 起，每条 60 字节：
///       +0  u8[4] 条目指纹（疑似哈希，用途未确认）
///       +4  u8[3] 简拼（ASCII）
///       +7  u8[2] 保留
///       +9  u8    固定 0x04
///       +10 u8    字数 n（1~12）
///       +11 u8    0x5A 有效 / 0xAA 空槽
///       +12 汉字 UTF-16LE，n × 2 字节
///       随后 n × u16 拼音索引（指向输入法内置的 442 音节表，本程序不解码）
/// </code>
/// </para>
/// <para>
/// 拼音索引没有对应的公开音节表，这里改用「本程序词库的单字读音 + 文件里的简拼首字母」
/// 反推拼音：简拼给出每个音节的声母，正好能消掉多音字（重 zhòng/chóng）的歧义。
/// 因此 <see cref="ResolvePinyin"/> 的结果是**推断值**，个别生僻字可能不完整。
/// </para>
/// </remarks>
public sealed class MspyLearningStore
{
    private const int HeaderLength = 0x2400;
    private const int RecordLength = 60;
    private const int MaxWordLength = 12;

    private static readonly byte[] Magic = { 0x55, 0xAA, 0x88, 0x81 };
    private static readonly byte[] Magic2 = { 0x55, 0xAA, 0x55, 0xAA };

    /// <summary>用默认路径创建。</summary>
    public MspyLearningStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "InputMethod",
            "Chs",
            "ChsPinyinUDL.dat"))
    {
    }

    /// <summary>用指定路径创建，便于测试。</summary>
    /// <param name="filePath">词库文件路径。</param>
    public MspyLearningStore(string filePath) => FilePath = filePath;

    /// <summary>词库文件路径。</summary>
    public string FilePath { get; }

    /// <summary>文件是否存在。</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>文件最后写入时间；不存在时为 null。</summary>
    public DateTimeOffset? LastWriteTime
    {
        get
        {
            try
            {
                return File.Exists(FilePath) ? File.GetLastWriteTime(FilePath) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 读出全部有效条目，保持文件顺序（文件按简拼排序）。
    /// </summary>
    /// <param name="words">解析结果；失败时为空列表。</param>
    /// <param name="error">失败原因；成功为 null。</param>
    /// <returns>读取成功返回 true。</returns>
    public bool TryRead(out IReadOnlyList<MspyLearnedWord> words, out string? error)
    {
        words = Array.Empty<MspyLearnedWord>();
        error = null;

        byte[] data;
        try
        {
            // 输入法可能正开着这个文件，必须允许读写共享，否则会拿到「文件被占用」。
            using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > int.MaxValue)
            {
                error = "文件异常大";
                return false;
            }

            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            error = ex is FileNotFoundException or DirectoryNotFoundException
                ? "尚未生成（输入法还没学到任何词）"
                : $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }

        if (data.Length < HeaderLength)
        {
            error = $"文件过短：{data.Length} 字节";
            return false;
        }

        if (!data.AsSpan(0, 4).SequenceEqual(Magic) || !data.AsSpan(8, 4).SequenceEqual(Magic2))
        {
            error = "文件头不是 mschxudl 格式";
            return false;
        }

        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x0C, 4));
        var capacity = (data.Length - HeaderLength) / RecordLength;
        if (count < 0 || count > capacity)
        {
            error = $"记录数 {count} 超出文件容量 {capacity}";
            return false;
        }

        var list = new List<MspyLearnedWord>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderLength + (i * RecordLength);
            var length = data[offset + 10];
            if (data[offset + 11] != 0x5A || length is 0 or > MaxWordLength)
            {
                continue; // 空槽（0xAA）或已损坏的条目
            }

            var jianpin = Encoding.ASCII.GetString(data, offset + 4, 3).TrimEnd('\0');
            var text = Encoding.Unicode.GetString(data, offset + 12, length * 2);
            if (text.Length != length || text.Contains('\0'))
            {
                continue;
            }

            list.Add(new MspyLearnedWord(text, jianpin, length));
        }

        words = list;
        return true;
    }

    /// <summary>
    /// 按「单字读音 + 简拼首字母」推断一条记录的拼音。
    /// </summary>
    /// <param name="text">汉字词面。</param>
    /// <param name="jianpin">文件里的简拼，长度可能小于字数。</param>
    /// <param name="lexicon">读音来源。</param>
    /// <param name="table">音节表；传 null 用默认表。</param>
    /// <returns>无调全拼；完全无法推断时返回空串。</returns>
    public static string ResolvePinyin(
        string text,
        string jianpin,
        IPinyinLexicon lexicon,
        SyllableTable? table = null)
    {
        table ??= SyllableTable.Default;
        var builder = new StringBuilder(text.Length * 4);

        for (var i = 0; i < text.Length; i++)
        {
            if (!lexicon.TryGetReadings(text[i], out var readings) || readings.Length == 0)
            {
                return string.Empty;
            }

            var wanted = i < jianpin.Length ? char.ToLowerInvariant(jianpin[i]) : '\0';
            var chosen = -1;
            foreach (var raw in readings)
            {
                // TryGetReadings 的约定是「0 为空，非 0 = 音节 id + 1」。
                if (raw == 0)
                {
                    continue;
                }

                var id = raw - 1;
                if (wanted != '\0' && char.ToLowerInvariant(table.GetInitial(id)) != wanted)
                {
                    continue;
                }

                // 简拼给的是声母，用它把多音字收敛到正确读音。
                chosen = id;
                break;
            }

            if (chosen < 0)
            {
                // 没有声母对得上的读音（简拼与词库不一致），退回第一个可用读音。
                foreach (var raw in readings)
                {
                    if (raw != 0)
                    {
                        chosen = raw - 1;
                        break;
                    }
                }
            }

            if (chosen < 0)
            {
                return string.Empty;
            }

            builder.Append(table.GetCanonical(chosen));
        }

        return builder.ToString();
    }

    /// <summary>把条目导出成 CSV（UTF-8 BOM，Excel 可直接打开）。</summary>
    /// <param name="words">要导出的条目。</param>
    /// <param name="path">目标路径。</param>
    /// <param name="lexicon">读音来源。</param>
    /// <returns>写入成功返回 true。</returns>
    public static bool ExportCsv(
        IEnumerable<MspyLearnedWord> words,
        string path,
        IPinyinLexicon lexicon)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("词,拼音,简拼,字数");
            foreach (var word in words)
            {
                var pinyin = ResolvePinyin(word.Text, word.Jianpin, lexicon);
                builder.Append(Escape(word.Text)).Append(',')
                    .Append(Escape(pinyin)).Append(',')
                    .Append(Escape(word.Jianpin)).Append(',')
                    .Append(word.Length).AppendLine();
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        static string Escape(string value) =>
            value.Contains(',') || value.Contains('"')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace BetterPinyin.Core.Wordbook;

/// <summary>微软拼音自定义短语里的一条记录。</summary>
/// <param name="Pinyin">触发拼音，小写、无空格。</param>
/// <param name="Position">候选位置 1~9。</param>
/// <param name="Text">输出文本。</param>
public readonly record struct MspyPhrase(string Pinyin, int Position, string Text);

/// <summary>
/// 读写微软拼音的「用户定义的短语」文件
/// <c>%APPDATA%\Microsoft\InputMethod\Chs\ChsPinyinEUDPv1.lex</c>。
/// </summary>
/// <remarks>
/// <para>
/// 格式是社区逆向的结果（魔数 <c>mschxudp</c>），微软不保证兼容：
/// <code>
/// 0x00  "mschxudp"（8B）
/// 0x08  u16 版本=2 | u16 0x0060 | u32 1
/// 0x10  u32 0x40
/// 0x14  u32 64 + count*4
/// 0x18  u32 文件总长
/// 0x1C  u32 count
/// 0x20  48B 机器相关字段（原样保留）
/// 0x44  偏移表 count × u32（最后一条隐含，不写）
/// 记录: [16B 头][拼音 UTF-16LE][00 00][短语 UTF-16LE][00 00]
///       16B 头 = u16 0x10 | u16 0x10 | u16 (16+拼音字节+2) | u32 位置 | u16 6 | u16 0 | u8[2]
/// </code>
/// </para>
/// <para>
/// 因此这里的策略是「最小改动」：只改 0x14/0x18/0x1C 三个计数与记录区，
/// 0x00~0x43 的其它字节（含每台机器不同的字段）原样写回。任何格式不符一律拒绝写入。
/// </para>
/// </remarks>
public sealed class MspyPhraseStore
{
    private const int HeaderLength = 0x44;
    private const int RecordHeaderLength = 16;
    private static readonly byte[] Magic = "mschxudp"u8.ToArray();
    private static readonly byte[] Separator = { 0, 0 };

    /// <summary>用默认路径创建。</summary>
    public MspyPhraseStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "InputMethod",
            "Chs",
            "ChsPinyinEUDPv1.lex"))
    {
    }

    /// <summary>用指定路径创建，便于测试。</summary>
    /// <param name="filePath">短语文件路径。</param>
    /// <param name="backupDirectory">备份目录；为空时用 <c>%APPDATA%\BetterPinyin\udp-backup</c>。</param>
    public MspyPhraseStore(string filePath, string? backupDirectory = null)
    {
        FilePath = filePath;
        BackupDirectory = backupDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterPinyin",
            "udp-backup");
    }

    /// <summary>短语文件路径。</summary>
    public string FilePath { get; }

    /// <summary>备份目录。</summary>
    public string BackupDirectory { get; }

    /// <summary>最近一次错误。</summary>
    public string? LastError { get; private set; }

    /// <summary>文件是否存在。</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// 读取全部短语。文件缺失或为空库时返回空列表。
    /// </summary>
    /// <param name="error">失败原因。</param>
    /// <returns>短语列表，按拼音排序。</returns>
    public IReadOnlyList<MspyPhrase> Read(out string? error)
    {
        error = null;
        var result = new List<MspyPhrase>();
        if (!File.Exists(FilePath))
        {
            return result;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return result;
        }

        if (data.Length < HeaderLength)
        {
            // 空库 stub（本机实测 64 字节）算正常，不报错。
            return result;
        }

        if (!data.AsSpan(0, 8).SequenceEqual(Magic))
        {
            error = "短语文件魔数不符";
            return result;
        }

        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x1C, 4));
        if (count <= 0 || count > 10_000)
        {
            return result;
        }

        var tableStart = HeaderLength;
        var recordsStart = tableStart + ((count - 1) * 4);
        if (recordsStart > data.Length)
        {
            error = "短语偏移表越界";
            return result;
        }

        var previous = 0;
        for (var i = 0; i < count; i++)
        {
            var next = i == count - 1
                ? data.Length - recordsStart
                : (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(tableStart + (i * 4), 4));

            var start = recordsStart + previous;
            var length = next - previous;
            previous = next;

            if (start < 0 || length < RecordHeaderLength || start + length > data.Length)
            {
                continue;
            }

            var record = data.AsSpan(start, length);
            if (record[9] != 0)
            {
                // 已删除的记录。
                continue;
            }

            var position = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(6, 4));
            var body = record.Slice(RecordHeaderLength);
            var parts = SplitByDoubleZero(body);
            if (parts.Count < 2)
            {
                continue;
            }

            var pinyin = Encoding.Unicode.GetString(parts[0]).Trim();
            var text = Encoding.Unicode.GetString(parts[1]).Trim();
            if (pinyin.Length > 0 && text.Length > 0)
            {
                result.Add(new MspyPhrase(pinyin, position, text));
            }
        }

        return result;
    }

    /// <summary>
    /// 把一批短语并入现有短语库。
    /// </summary>
    /// <param name="phrases">要写入的短语（自己的词条）。</param>
    /// <param name="overwriteOthers">同拼音不同文本时是否覆盖。默认 false，判为冲突不写。</param>
    /// <param name="conflicts">被判为冲突、没有写入的短语。</param>
    /// <returns>成功返回 true。</returns>
    public bool Merge(IEnumerable<MspyPhrase> phrases, bool overwriteOthers, out IReadOnlyList<MspyPhrase> conflicts)
    {
        conflicts = Array.Empty<MspyPhrase>();
        LastError = null;

        var existing = Read(out var readError);
        if (readError is not null)
        {
            LastError = readError;
            return false;
        }

        var map = new Dictionary<string, MspyPhrase>(StringComparer.OrdinalIgnoreCase);
        foreach (var phrase in existing)
        {
            map[phrase.Pinyin] = phrase;
        }

        var blocked = new List<MspyPhrase>();
        foreach (var phrase in phrases)
        {
            var key = phrase.Pinyin.ToLowerInvariant();
            if (key.Length == 0 || phrase.Text.Length == 0)
            {
                continue;
            }

            if (map.TryGetValue(key, out var current) &&
                !string.Equals(current.Text, phrase.Text, StringComparison.Ordinal) &&
                !overwriteOthers)
            {
                blocked.Add(phrase);
                continue;
            }

            map[key] = phrase with { Pinyin = key };
        }

        conflicts = blocked;
        return Write(map.Values);
    }

    /// <summary>导出成系统设置可导入的 TXT（每行「拼音 TAB 短语」）。</summary>
    /// <param name="path">目标路径。</param>
    /// <returns>成功返回 true。</returns>
    public bool ExportText(string path)
    {
        var phrases = Read(out var error);
        if (error is not null)
        {
            LastError = error;
            return false;
        }

        try
        {
            var lines = phrases.Select(p => $"{p.Pinyin}\t{p.Text}");
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
    }

    private bool Write(IEnumerable<MspyPhrase> phrases)
    {
        var list = phrases
            .Where(p => p.Pinyin.Length > 0 && p.Text.Length > 0)
            .GroupBy(p => p.Pinyin, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Pinyin, StringComparer.Ordinal)
            .ToList();

        byte[] header;
        try
        {
            header = ReadHeader();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }

        if (!Backup())
        {
            return false;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);

        stream.Write(header, 0, HeaderLength);
        stream.Position = HeaderLength;

        // 先算偏移表：最后一条不写，隐含等于文件末尾。
        var lengths = list
            .Select(p => RecordHeaderLength
                         + Encoding.Unicode.GetByteCount(p.Pinyin) + 2
                         + Encoding.Unicode.GetByteCount(p.Text) + 2)
            .ToList();

        var cursor = 0;
        for (var i = 0; i < list.Count; i++)
        {
            cursor += lengths[i];
            if (i < list.Count - 1)
            {
                writer.Write((uint)cursor);
            }
        }

        foreach (var phrase in list)
        {
            writer.Write(BuildRecord(phrase));
        }

        var total = (int)stream.Length;
        var data = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), (uint)(64 + (list.Count * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x18), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1C), (uint)list.Count);

        try
        {
            var temp = FilePath + ".bp.tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
    }

    private byte[] ReadHeader()
    {
        if (!File.Exists(FilePath))
        {
            return CreateEmptyHeader();
        }

        var data = File.ReadAllBytes(FilePath);
        if (data.Length < HeaderLength)
        {
            var header = CreateEmptyHeader();
            Array.Copy(data, header, Math.Min(data.Length, header.Length));
            return header;
        }

        if (!data.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new IOException("短语文件魔数不符");
        }

        return data.AsSpan(0, HeaderLength).ToArray();
    }

    private static byte[] CreateEmptyHeader()
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 0x0060);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1C), 0);
        return header;
    }

    private static byte[] BuildRecord(MspyPhrase phrase)
    {
        var pinyin = Encoding.Unicode.GetBytes(phrase.Pinyin);
        var text = Encoding.Unicode.GetBytes(phrase.Text);

        using var stream = new MemoryStream(RecordHeaderLength + pinyin.Length + text.Length + 4);
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);

        writer.Write((ushort)0x0010);
        writer.Write((ushort)0x0010);
        writer.Write((ushort)(RecordHeaderLength + pinyin.Length + 2));
        writer.Write((uint)Math.Clamp(phrase.Position, 1, 9));
        writer.Write((ushort)0x0006);
        writer.Write((ushort)0x0000);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(pinyin);
        writer.Write(Separator);
        writer.Write(text);
        writer.Write(Separator);

        return stream.ToArray();
    }

    private bool Backup()
    {
        if (!File.Exists(FilePath))
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var name = $"ChsPinyinEUDPv1.{DateTime.Now:yyyyMMdd_HHmmss}.bak.lex";
            File.Copy(FilePath, Path.Combine(BackupDirectory, name), overwrite: true);

            // 只保留最近 10 份，避免长期使用把用户目录撑大。
            foreach (var old in Directory.GetFiles(BackupDirectory, "*.bak.lex")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(10))
            {
                File.Delete(old);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"备份失败，已中止写入：{ex.Message}";
            return false;
        }
    }

    private static List<byte[]> SplitByDoubleZero(ReadOnlySpan<byte> body)
    {
        var parts = new List<byte[]>(2);
        var current = new List<byte>(32);
        for (var i = 0; i + 1 < body.Length; i += 2)
        {
            if (body[i] == 0 && body[i + 1] == 0)
            {
                if (current.Count >= 2)
                {
                    parts.Add(current.ToArray());
                }

                current.Clear();
            }
            else
            {
                current.Add(body[i]);
                current.Add(body[i + 1]);
            }
        }

        if (current.Count >= 2)
        {
            parts.Add(current.ToArray());
        }

        return parts;
    }
}

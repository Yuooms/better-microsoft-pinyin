using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterPinyin.Core.Wordbook;

/// <summary>一份整包备份的内容。</summary>
/// <param name="Phrases">自定义短语（含自动补出来的简拼记录）。</param>
/// <param name="LearnedData">自学习词库的原始字节；备份里没有时为 null。</param>
/// <param name="LearnedWordCount">自学习词条数，仅用于提示。</param>
/// <param name="ExportedAt">导出时刻；老备份或手改过的文件可能缺失。</param>
/// <param name="Version">备份格式版本。</param>
public sealed record BackupBundle(
    IReadOnlyList<MspyPhrase> Phrases,
    byte[]? LearnedData,
    int LearnedWordCount,
    DateTimeOffset? ExportedAt,
    int Version);

/// <summary>
/// 把「自定义短语」和「自学习词库」打成一个文件，换机器时一次搬完。
/// </summary>
/// <remarks>
/// <para>
/// 两份数据的可写程度不同，所以容器里各存各的样子：
/// 短语能逐条读写，存成 JSON；自学习只能整份替换（格式里的拼音索引指向输入法的内部音节表，
/// 且没有公开的写入口，见 <see cref="MspyLearningStore"/>），所以原样存一份 <c>learned.dat</c>，
/// 恢复时按字节回写。
/// </para>
/// <para>
/// 用 zip 而不是单个自定义格式，是因为这两个文件都可以被普通工具打开看：
/// 出问题时用户能把 <c>phrases.json</c> 发过来，或者自己确认备份里到底有没有东西。
/// </para>
/// </remarks>
public static class MspyBackupBundle
{
    /// <summary>格式标识，写进 manifest 用来认领文件。</summary>
    public const string FormatId = "betterpinyin.mspy-backup";

    /// <summary>当前格式版本。</summary>
    public const int FormatVersion = 1;

    /// <summary>建议的文件名，带导出时刻。</summary>
    /// <param name="now">导出时刻。</param>
    /// <returns>形如 <c>mspy-backup-20261116-2046.zip</c>。</returns>
    public static string SuggestedFileName(DateTimeOffset now) =>
        $"mspy-backup-{now:yyyyMMdd-HHmm}.zip";

    private const string ManifestEntry = "manifest.json";
    private const string PhrasesEntry = "phrases.json";
    private const string LearnedEntry = "learned.dat";

    /// <summary>备份里最多接受多少条短语，防止畸形文件把内存吃满。</summary>
    private const int MaxPhrases = 20_000;

    /// <summary>自学习词库最多接受多大，正常本机文件不到 100 KB。</summary>
    private const int MaxLearnedBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 写出一份整包备份。
    /// </summary>
    /// <param name="path">目标文件（会被整体覆盖）。</param>
    /// <param name="phrases">要写入的短语，原样保存（包括自动补出来的简拼记录）。</param>
    /// <param name="learnedData">自学习词库的原始字节；传 null 表示本机还没有这份文件。</param>
    /// <param name="learnedWordCount">自学习词条数，只写进 manifest 供导入时提示。</param>
    /// <param name="now">导出时刻。</param>
    /// <param name="error">失败原因；成功为 null。</param>
    /// <returns>成功返回 true。</returns>
    /// <remarks>
    /// 先写临时文件再整体换名：导出到一半失败时不会留下一个「看起来正常、其实打不开」的备份。
    /// </remarks>
    public static bool TryWrite(
        string path,
        IReadOnlyList<MspyPhrase> phrases,
        byte[]? learnedData,
        int learnedWordCount,
        DateTimeOffset now,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(phrases);

        error = null;
        var temp = path + ".bp-tmp";

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifest = new ManifestDto(
                    FormatId,
                    FormatVersion,
                    now,
                    phrases.Count,
                    learnedWordCount,
                    learnedData?.Length ?? 0);

                WriteJson(archive, ManifestEntry, manifest);
                WriteJson(
                    archive,
                    PhrasesEntry,
                    phrases
                        .Where(p => p.Pinyin.Length > 0 && p.Text.Length > 0)
                        .Select(p => new PhraseDto(p.Pinyin, p.Position, p.Text))
                        .ToList());

                if (learnedData is { Length: > 0 })
                {
                    var entry = archive.CreateEntry(LearnedEntry, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    entryStream.Write(learnedData, 0, learnedData.Length);
                }
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// 读一份整包备份，只做解析，不往输入法的文件里写任何东西。
    /// </summary>
    /// <param name="path">备份文件。</param>
    /// <param name="bundle">解析结果。</param>
    /// <param name="error">失败原因；成功为 null。</param>
    /// <returns>格式可识别返回 true。</returns>
    public static bool TryRead(string path, out BackupBundle bundle, out string? error)
    {
        bundle = new BackupBundle(Array.Empty<MspyPhrase>(), null, 0, null, FormatVersion);
        error = null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var manifestEntry = archive.GetEntry(ManifestEntry);
            if (manifestEntry is null)
            {
                error = "压缩包里没有 manifest.json，不是本程序的备份";
                return false;
            }

            var manifest = ReadJson<ManifestDto>(manifestEntry);
            if (manifest is null || !string.Equals(manifest.Format, FormatId, StringComparison.Ordinal))
            {
                error = "manifest.json 里的格式标识对不上，不是本程序的备份";
                return false;
            }

            if (manifest.Version > FormatVersion)
            {
                error = $"备份来自更新的版本（格式 {manifest.Version}），请先升级程序再导入";
                return false;
            }

            var phrases = new List<MspyPhrase>();
            var phrasesEntry = archive.GetEntry(PhrasesEntry);
            if (phrasesEntry is not null)
            {
                var items = ReadJson<List<PhraseDto>>(phrasesEntry) ?? new List<PhraseDto>();
                foreach (var item in items.Take(MaxPhrases))
                {
                    var pinyin = item.Pinyin?.Trim().ToLowerInvariant() ?? string.Empty;
                    var text = item.Text?.Trim() ?? string.Empty;
                    if (pinyin.Length == 0 || text.Length == 0)
                    {
                        continue; // 手改坏的行直接跳过，别让它挡住整份备份
                    }

                    phrases.Add(new MspyPhrase(pinyin, Math.Clamp(item.Position, 1, 9), text));
                }
            }

            byte[]? learned = null;
            var learnedEntry = archive.GetEntry(LearnedEntry);
            if (learnedEntry is not null && learnedEntry.Length > 0)
            {
                if (learnedEntry.Length > MaxLearnedBytes)
                {
                    error = $"learned.dat 有 {learnedEntry.Length} 字节，超出正常范围";
                    return false;
                }

                using var entryStream = learnedEntry.Open();
                using var buffer = new MemoryStream((int)learnedEntry.Length);
                entryStream.CopyTo(buffer);
                learned = buffer.ToArray();
            }

            if (phrases.Count == 0 && learned is null)
            {
                // 空备份导进去只会让人以为「导入成功了但什么都没变」，不如直接说清楚。
                error = "备份里没有任何数据（既没有短语，也没有自学习词库）";
                return false;
            }

            bundle = new BackupBundle(
                phrases,
                learned,
                manifest.LearnedCount,
                manifest.ExportedAt,
                manifest.Version);
            return true;
        }
        catch (InvalidDataException)
        {
            error = "不是 zip 文件，或者文件已损坏";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
    }

    private static void WriteJson<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    private static T? ReadJson<T>(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件删不掉不影响导出结果，下次导出会覆盖。
        }
    }

    /// <summary>manifest.json 的结构。</summary>
    private sealed record ManifestDto(
        string Format,
        int Version,
        DateTimeOffset? ExportedAt,
        int PhraseCount,
        int LearnedCount,
        int LearnedBytes);

    /// <summary>phrases.json 里的一行。</summary>
    private sealed record PhraseDto(string? Pinyin, int Position, string? Text);
}

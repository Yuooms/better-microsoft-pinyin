using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterPinyin.Core.Statistics;

/// <summary>
/// 输入统计的本地存储。
/// </summary>
/// <remarks>
/// <para>
/// 路径 <c>%APPDATA%\BetterPinyin\statistics.json</c>，与设置、词库平级。
/// 写入方式对齐 <see cref="Wordbook.WordbookStore"/>：脏标记 + 1.5 秒防抖 + 临时文件替换，
/// 避免每敲一个字都落盘，也避免断电留下半个文件。
/// </para>
/// <para>
/// 与词库不同的一点：这里**不保留备份**。<see cref="DeleteAll"/> 是隐私删除，
/// 留一份副本等于没删干净；其它数据（词库、自定义短语）不受影响。
/// </para>
/// </remarks>
public sealed class StatisticsStore : IDisposable
{
    private const int DebounceMilliseconds = 1500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 不缩进：这份文件里 24 个小时桶全是数字，缩进能让体积翻好几倍。
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly Timer _timer;
    private IReadOnlyList<DailyStatistics>? _pending;
    private int _retentionDays;
    private bool _disposed;

    /// <summary>用默认路径创建。</summary>
    public StatisticsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterPinyin",
            "statistics.json"))
    {
    }

    /// <summary>用指定路径创建，便于测试。</summary>
    /// <param name="filePath">统计文件完整路径。</param>
    public StatisticsStore(string filePath)
    {
        FilePath = filePath;
        _timer = new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>统计文件路径。</summary>
    public string FilePath { get; }

    /// <summary>最近一次读写的错误信息。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 读取历史统计；文件不存在或损坏时返回空列表并记录 <see cref="LastError"/>。
    /// </summary>
    /// <returns>按日期升序的每日数据。</returns>
    public IReadOnlyList<DailyStatistics> Load()
    {
        if (!File.Exists(FilePath))
        {
            LastError = null;
            return Array.Empty<DailyStatistics>();
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            var file = JsonSerializer.Deserialize<StatisticsFile>(stream, JsonOptions);
            if (file?.Days is null)
            {
                LastError = "统计文件为空";
                return Array.Empty<DailyStatistics>();
            }

            foreach (var day in file.Days)
            {
                day.Normalize();
            }

            LastError = null;
            return file.Days;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 损坏不阻塞启动：退回空统计，用户照样能用输入法与云联想。
            LastError = $"{ex.GetType().Name}：{ex.Message}";
            return Array.Empty<DailyStatistics>();
        }
    }

    /// <summary>请求在防抖窗口之后落盘。</summary>
    /// <param name="days">要保存的快照。</param>
    /// <param name="retentionDays">保留天数；0 表示全部保留。</param>
    /// <remarks>
    /// 保留期必须在这里一起记下来：防抖落盘时拿不到调用方的设置，
    /// 不记就会变成「只有手动点保存才裁剪」，用户不开设置页时数据会一直长。
    /// </remarks>
    public void ScheduleSave(IReadOnlyList<DailyStatistics> days, int retentionDays = 0)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _pending = days;
            _retentionDays = retentionDays;
        }

        try
        {
            _timer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 退出过程中定时器已释放，忽略。
        }
    }

    /// <summary>
    /// 立刻落盘。
    /// </summary>
    /// <param name="days">要保存的快照。</param>
    /// <param name="retentionDays">保留天数；0 表示全部保留；-1 表示沿用上次的保留期。</param>
    /// <param name="timeoutMilliseconds">等待写锁的超时。</param>
    /// <returns>成功返回 true。</returns>
    public bool Save(
        IReadOnlyList<DailyStatistics> days,
        int retentionDays = -1,
        int timeoutMilliseconds = 2000)
    {
        if (!Monitor.TryEnter(_gate, timeoutMilliseconds))
        {
            LastError = "等待写入超时";
            return false;
        }

        try
        {
            var effectiveRetention = retentionDays < 0 ? _retentionDays : retentionDays;
            var payload = new StatisticsFile
            {
                Version = 1,
                Days = TrimByRetention(days, effectiveRetention).ToList(),
            };

            // 记住保留期，后续的防抖落盘与退出刷盘都按它来。
            _retentionDays = effectiveRetention;

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);

            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>把待写的快照立刻刷盘。</summary>
    /// <param name="retentionDays">保留天数；0 表示全部保留；-1 表示沿用上次的保留期。</param>
    /// <param name="timeoutMilliseconds">等待写锁的超时。</param>
    /// <returns>成功返回 true。</returns>
    /// <remarks>退出路径必须调用：否则最后 1.5 秒里累计的数字会丢。</remarks>
    public bool Flush(int retentionDays = -1, int timeoutMilliseconds = 500)
    {
        IReadOnlyList<DailyStatistics>? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        return pending is null || Save(pending, retentionDays, timeoutMilliseconds);
    }

    /// <summary>
    /// 删除全部历史统计。
    /// </summary>
    /// <param name="timeoutMilliseconds">等待写锁的超时。</param>
    /// <returns>成功返回 true。</returns>
    /// <remarks>
    /// 直接删文件，不写空文件也不留备份：这是隐私删除，留副本等于没删干净。
    /// 下次落盘时会自然重建。
    /// </remarks>
    public bool DeleteAll(int timeoutMilliseconds = 2000)
    {
        if (!Monitor.TryEnter(_gate, timeoutMilliseconds))
        {
            LastError = "等待写入超时";
            return false;
        }

        try
        {
            _pending = null;

            try
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // 已经释放，忽略。
            }

            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }

            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    /// <summary>
    /// 按保留天数裁剪。
    /// </summary>
    /// <param name="days">原始数据。</param>
    /// <param name="retentionDays">保留天数；小于等于 0 表示不裁剪。</param>
    /// <returns>按日期升序的裁剪结果。</returns>
    /// <remarks>
    /// 保留的是「最近 N 个有记录的日子」，而不是「今天往前 N 天」。
    /// 后者一旦用户出差两个月回来，整段历史会在下次落盘时被静默清空，
    /// 对一份纯本地统计来说代价太大。
    /// </remarks>
    public static IReadOnlyList<DailyStatistics> TrimByRetention(
        IReadOnlyList<DailyStatistics> days,
        int retentionDays)
    {
        if (retentionDays <= 0 || days.Count <= retentionDays)
        {
            return days;
        }

        // 按日期字符排序即可：yyyy-MM-dd 的字典序与时间序一致。
        var sorted = days.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
        return sorted.Skip(sorted.Count - retentionDays).ToList();
    }

    private void FlushPending()
    {
        IReadOnlyList<DailyStatistics>? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        if (pending is not null)
        {
            // 用 ScheduleSave 时记下的保留期，而不是 0（= 永不裁剪）。
            Save(pending);
        }
    }

    private sealed class StatisticsFile
    {
        public int Version { get; set; } = 1;

        public List<DailyStatistics>? Days { get; set; }
    }
}

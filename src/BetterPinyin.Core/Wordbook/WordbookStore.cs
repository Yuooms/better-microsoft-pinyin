using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterPinyin.Core.Wordbook;

/// <summary>
/// 用户词库的磁盘存储：<c>%APPDATA%\BetterPinyin\words.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>settings.json</c> 分开存：设置是低频小文件，词库会被每次采纳触碰；混在一起会让
/// 一次采纳引发整份配置重写，也把「配置损坏」和「词库损坏」两种故障绑死。
/// </para>
/// <para>
/// 写入策略：变更只置脏标记 + 重置 1.5 秒防抖定时器，真正落盘在后台线程完成。
/// 采纳路径在线程池上跑，且提交窗口内全机按键被吞，绝不能在这里做磁盘 I/O。
/// </para>
/// </remarks>
public sealed class WordbookStore : IDisposable
{
    private const int DebounceMilliseconds = 1500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly Timer _timer;
    private UserWordbook? _pending;
    private bool _disposed;

    /// <summary>用默认路径创建。</summary>
    public WordbookStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterPinyin",
            "words.json"))
    {
    }

    /// <summary>用指定路径创建，便于测试。</summary>
    /// <param name="filePath">词库文件完整路径。</param>
    public WordbookStore(string filePath)
    {
        FilePath = filePath;
        _timer = new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>词库文件路径。</summary>
    public string FilePath { get; }

    /// <summary>最近一次读写的错误信息。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 读取词库；文件不存在或损坏时返回空词库并记录 <see cref="LastError"/>。
    /// </summary>
    /// <returns>装载好的词库实例。</returns>
    public UserWordbook Load()
    {
        var book = new UserWordbook();
        if (!File.Exists(FilePath))
        {
            return book;
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            var file = JsonSerializer.Deserialize<WordbookFile>(stream, JsonOptions);
            if (file is null)
            {
                LastError = "词库文件为空";
                return book;
            }

            book.Reset(file.Words ?? new List<UserWord>(), file.Overrides ?? new List<WordOverride>());
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 损坏不阻塞启动：退回空词库，用户还能继续用内置词库与云联想。
            LastError = $"{ex.GetType().Name}：{ex.Message}";
            book.Reset(Array.Empty<UserWord>(), Array.Empty<WordOverride>());
        }

        return book;
    }

    /// <summary>请求在防抖窗口之后落盘。</summary>
    /// <param name="book">要保存的词库。</param>
    public void ScheduleSave(UserWordbook book)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _pending = book;
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
    /// <param name="book">要保存的词库。</param>
    /// <param name="timeoutMilliseconds">等待写锁的超时。</param>
    /// <returns>成功返回 true。</returns>
    public bool Save(UserWordbook book, int timeoutMilliseconds = 2000)
    {
        if (!Monitor.TryEnter(_gate, timeoutMilliseconds))
        {
            LastError = "等待写入超时";
            return false;
        }

        try
        {
            var payload = new WordbookFile
            {
                Version = 1,
                Words = book.Words.ToList(),
                Overrides = book.Overrides.ToList(),
            };

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LastError = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>把挂起的改动立刻写盘，用于退出。</summary>
    /// <param name="book">要保存的词库。</param>
    /// <param name="timeoutMilliseconds">等待写锁的超时。</param>
    /// <returns>成功返回 true。</returns>
    public bool Flush(UserWordbook book, int timeoutMilliseconds = 300)
    {
        try
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 已释放，直接写。
        }

        return Save(book, timeoutMilliseconds);
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

    private void FlushPending()
    {
        UserWordbook? book;
        lock (_gate)
        {
            book = _pending;
            _pending = null;
        }

        if (book is not null)
        {
            Save(book, 2000);
        }
    }

    /// <summary>磁盘上的数据结构。</summary>
    private sealed class WordbookFile
    {
        public int Version { get; set; } = 1;

        public List<UserWord>? Words { get; set; }

        public List<WordOverride>? Overrides { get; set; }
    }
}

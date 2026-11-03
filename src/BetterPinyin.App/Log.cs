using System.Collections.Concurrent;
using System.IO;

namespace BetterPinyin.App;

/// <summary>
/// 极简文件日志。
/// </summary>
/// <remarks>
/// <para>
/// 常驻程序"启动后什么都没发生"是最难排查的一类问题：没有控制台、异常又被吞掉。
/// 因此把所有启动阶段与关键路径的异常写进文件，用户反馈问题时可以直接提供日志。
/// 日志只记录事件与异常，不记录用户输入内容。
/// </para>
/// <para>
/// 写入是异步的：调用方只把整行放进有界队列，落盘由单独的后台线程完成。
/// 这条路径曾被上屏流程调用，而那时它同步做"建目录 + 查文件大小 + 打开/关闭文件"，
/// 单次几毫秒，落在按键回调上就是可感知的顿挫。队列满时直接丢弃新日志——
/// 日志永远不能反过来影响输入流畅性。
/// </para>
/// </remarks>
internal static class Log
{
    /// <summary>队列上限。正常使用远达不到，只有异常刷屏时才会丢。</summary>
    private const int QueueCapacity = 4096;

    /// <summary>单个文件的大小上限，超过就滚动。</summary>
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly BlockingCollection<string> Queue =
        new(new ConcurrentQueue<string>(), QueueCapacity);

    private static readonly object FileGate = new();

    private static readonly Lazy<Thread> Writer = new(StartWriter, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterPinyin",
        "logs");

    private static int _shutdown;

    /// <summary>日志文件完整路径。</summary>
    public static string FilePath => Path.Combine(DirectoryPath, "app.log");

    /// <summary>写入一条日志。任意线程可调用，不阻塞。</summary>
    /// <param name="message">内容。</param>
    public static void Write(string message)
    {
        // 时间戳在入队时格式化，保证日志顺序与事件发生顺序一致。
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        if (Volatile.Read(ref _shutdown) != 0)
        {
            return;
        }

        try
        {
            // 第一次写入时把消费线程拉起来；懒加载只在 Shutdown 里触发是不够的。
            _ = Writer.Value;
            Queue.TryAdd(line);
        }
        catch (InvalidOperationException)
        {
            // 队列已停止接受新项（正在退出），忽略。
        }
    }

    /// <summary>写入异常。</summary>
    /// <param name="context">发生位置。</param>
    /// <param name="exception">异常。</param>
    public static void Error(string context, Exception exception) =>
        Write($"[错误] {context}：{exception.GetType().Name} {exception.Message}{Environment.NewLine}{exception.StackTrace}");

    /// <summary>
    /// 停止接受新日志并等待落盘。进程退出前必须调用；幂等。
    /// </summary>
    /// <param name="timeoutMilliseconds">最长等待时间。</param>
    public static void Shutdown(int timeoutMilliseconds = 1500)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return;
        }

        try
        {
            Queue.CompleteAdding();
            if (Writer.IsValueCreated)
            {
                Writer.Value.Join(timeoutMilliseconds);
            }
        }
        catch (Exception)
        {
            // 退出流程里任何异常都不能阻止进程结束。
        }
    }

    private static Thread StartWriter()
    {
        var thread = new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                AppendLine(line);
            }
        })
        {
            IsBackground = true,
            Name = "BetterPinyin.Log",
        };

        thread.Start();
        return thread;
    }

    private static void AppendLine(string line)
    {
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(DirectoryPath);

                // 超过 1MB 就滚动，避免长期运行把磁盘写满。
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // 日志失败绝不能影响主流程。
        }
    }
}

using System.IO;

namespace BetterPinyin.App;

/// <summary>
/// 极简文件日志。
/// </summary>
/// <remarks>
/// 常驻程序"启动后什么都没发生"是最难排查的一类问题：没有控制台、异常又被吞掉。
/// 因此把所有启动阶段与关键路径的异常写进文件，用户反馈问题时可以直接提供日志。
/// 日志只记录事件与异常，不记录用户输入内容。
/// </remarks>
internal static class Log
{
    private static readonly object SyncRoot = new();

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterPinyin",
        "logs");

    /// <summary>日志文件完整路径。</summary>
    public static string FilePath => Path.Combine(DirectoryPath, "app.log");

    /// <summary>写入一条日志。</summary>
    /// <param name="message">内容。</param>
    public static void Write(string message)
    {
        try
        {
            lock (SyncRoot)
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);

                // 超过 1MB 就滚动，避免长期运行把磁盘写满。
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1024 * 1024)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 日志失败绝不能影响主流程。
        }
    }

    /// <summary>写入异常。</summary>
    /// <param name="context">发生位置。</param>
    /// <param name="exception">异常。</param>
    public static void Error(string context, Exception exception) =>
        Write($"[错误] {context}：{exception.GetType().Name} {exception.Message}{Environment.NewLine}{exception.StackTrace}");
}

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using BetterPinyin.Core.Updates;

namespace BetterPinyin.App.Updates;

/// <summary>一次版本检查的结果。</summary>
/// <param name="Succeeded">网络与解析是否成功。</param>
/// <param name="Latest">拿到的发布信息；失败时为 null。</param>
/// <param name="HasUpdate">是否有比当前更新的稳定版。</param>
/// <param name="Message">可以直接显示给用户的一句话。</param>
internal sealed record UpdateCheckOutcome(bool Succeeded, ReleaseInfo? Latest, bool HasUpdate, string Message)
{
    /// <summary>造一个失败结果。</summary>
    /// <param name="message">原因。</param>
    public static UpdateCheckOutcome Failed(string message) => new(false, null, false, message);
}

/// <summary>
/// 通过 GitHub Releases 检查与安装更新。
/// </summary>
/// <remarks>
/// <para>
/// 只访问仓库的公开接口，不带任何身份信息：检查更新拿的是 <c>releases/latest</c>，
/// 下载的是发布里的 zip 直链。没有私钥、没有 token、不回报安装量。
/// </para>
/// <para>
/// 更新方式是「解压到临时目录 + 起一个 cmd 脚本替换 + 重新启动」，
/// 见 <see cref="UpdateScript"/>；安装目录不可写时（例如放在 Program Files）不硬来，
/// 退回「打开发布页手动下载」。
/// </para>
/// </remarks>
internal sealed class UpdateService
{
    /// <summary>仓库标识，同时也是更新检查的唯一数据来源。</summary>
    public const string Repository = "Yuooms/better-microsoft-pinyin";

    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;

    /// <summary>
    /// 更新包的缓存目录。
    /// </summary>
    /// <remarks>
    /// 放在 LocalApplicationData 而不是 %TEMP%：用户要能在「关于」页看到路径、
    /// 打开文件夹、手动删掉；临时目录会被清理工具整片扫走，也不适合当作可复用的位置。
    /// </remarks>
    public static string CacheRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterPinyin",
        "updates");

    /// <summary>创建服务。</summary>
    /// <param name="httpClient">共享的 HTTP 客户端。</param>
    public UpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>当前程序版本。</summary>
    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    /// <summary>
    /// 最近一次检查结果。
    /// </summary>
    /// <remarks>
    /// 启动时的后台检查与「关于」页共用一份，避免页面一打开就再发一次请求。
    /// </remarks>
    public UpdateCheckOutcome? LastOutcome { get; private set; }

    /// <summary>最近一次真正发出请求的时间（UTC）。</summary>
    public DateTimeOffset? LastCheckedUtc { get; private set; }

    /// <summary>
    /// 查询最新正式版。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>检查结果；网络或解析失败时 <see cref="UpdateCheckOutcome.Succeeded"/> 为 false。</returns>
    public async Task<UpdateCheckOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        UpdateCheckOutcome outcome;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);

            // GitHub 的 API 强制要求 User-Agent，缺了会直接 403。
            request.Headers.TryAddWithoutValidation("User-Agent", $"BetterMicrosoftPinyin/{CurrentVersion.ToString(3)}");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                outcome = UpdateCheckOutcome.Failed("仓库里还没有正式发布");
            }
            else if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                outcome = UpdateCheckOutcome.Failed("请求被 GitHub 限流，稍后再试");
            }
            else if (!response.IsSuccessStatusCode)
            {
                outcome = UpdateCheckOutcome.Failed($"GitHub 返回 {(int)response.StatusCode}");
            }
            else
            {
                var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                var release = ReleaseParser.Parse(json);

                outcome = release is null
                    ? UpdateCheckOutcome.Failed("发布信息解析失败")
                    : new UpdateCheckOutcome(
                        true,
                        release,
                        release.IsNewerThan(CurrentVersion),
                        release.IsNewerThan(CurrentVersion)
                            ? $"发现新版本 {release.Tag}"
                            : $"已是最新版本（{CurrentVersion.ToString(3)}）");
            }
        }
        catch (OperationCanceledException)
        {
            outcome = UpdateCheckOutcome.Failed(
                cancellationToken.IsCancellationRequested ? "检查已取消" : "连接 GitHub 超时");
        }
        catch (HttpRequestException)
        {
            outcome = UpdateCheckOutcome.Failed("连不上 GitHub，检查网络或代理");
        }

        LastOutcome = outcome;
        LastCheckedUtc = DateTimeOffset.UtcNow;
        return outcome;
    }

    /// <summary>
    /// 下载并解压发布包。
    /// </summary>
    /// <param name="release">要安装的发布。</param>
    /// <param name="progress">下载进度（0~1），可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解压后的目录。</returns>
    public async Task<string> DownloadAsync(
        ReleaseInfo release,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var asset = release.FindWindowsPackage()
            ?? throw new InvalidOperationException("这次发布里没有 Windows 压缩包");

        var root = GetReleaseDirectory(release.Tag);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "package.zip");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        using (var response = await _httpClient
                   .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var target = File.Create(zipPath);

            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                copied += read;
                if (total > 0)
                {
                    progress?.Report((double)copied / total);
                }
            }
        }

        progress?.Report(1);

        var extractDirectory = Path.Combine(root, "app");
        ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true);
        File.Delete(zipPath);
        return extractDirectory;
    }

    /// <summary>某个版本在本地缓存里的目录。</summary>
    /// <param name="tag">发布标签。</param>
    /// <returns>目录绝对路径（不一定存在）。</returns>
    public static string GetReleaseDirectory(string tag) => Path.Combine(CacheRoot, SafeName(tag));

    /// <summary>
    /// 该版本的安装包是否已经下载并解压好。
    /// </summary>
    /// <param name="release">发布信息。</param>
    /// <returns>已缓存返回 true。</returns>
    /// <remarks>
    /// 只认「解压目录里至少有一个 exe」：下载中断会留下空目录或半个 zip，
    /// 那种情况必须重新下，不能当成已就绪。
    /// </remarks>
    public static bool IsDownloaded(ReleaseInfo release)
    {
        var directory = Path.Combine(GetReleaseDirectory(release.Tag), "app");
        try
        {
            return Directory.Exists(directory) &&
                Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).Any();
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>缓存目录当前占用的字节数。</summary>
    /// <returns>总大小；目录不存在时为 0。</returns>
    public static long GetCacheSize() => GetDirectorySize(CacheRoot);

    /// <summary>
    /// 删除整个更新缓存。
    /// </summary>
    /// <returns>清理掉的字节数。</returns>
    /// <remarks>
    /// 只删自己的缓存目录，不碰安装目录。删不掉（文件被占用）时返回已删除的部分，
    /// 调用方据此给提示，不抛异常——清理缓存失败不值得打断用户。
    /// </remarks>
    public static long ClearCache()
    {
        var size = GetCacheSize();
        try
        {
            if (Directory.Exists(CacheRoot))
            {
                Directory.Delete(CacheRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"清理更新缓存失败：{ex.GetType().Name} {ex.Message}");
        }

        return size;
    }

    private static long GetDirectorySize(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            return new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 启动替换脚本，让新版本在本进程退出后接管安装目录。
    /// </summary>
    /// <param name="release">要安装的发布，只用于提示文案。</param>
    /// <param name="extractedDirectory"><see cref="DownloadAsync"/> 返回的目录。</param>
    /// <param name="message">成功时为提示语，失败时为原因。</param>
    /// <returns>脚本已启动返回 true；调用方此时应当退出程序。</returns>
    /// <remarks>
    /// 先把新版本解压好再退出，而不是让脚本自己下载：
    /// 脚本里没有网络与解压逻辑，出错面更小，失败了也只是「没换成」，不会毁掉安装目录。
    /// </remarks>
    public bool TryApplyAndRestart(ReleaseInfo release, string extractedDirectory, out string message)
    {
        message = string.Empty;

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            message = "拿不到当前程序路径";
            return false;
        }

        var executableName = Path.GetFileName(executable);
        var installDirectory = Path.GetDirectoryName(executable);

        // 用 dotnet 跑 dll 调试时进程是 dotnet.exe，替换它毫无意义。
        if (string.IsNullOrEmpty(installDirectory) ||
            string.Equals(executableName, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            message = "当前是开发模式启动，请手动解压覆盖";
            return false;
        }

        if (!File.Exists(Path.Combine(extractedDirectory, executableName)))
        {
            message = "下载的包里没有主程序";
            return false;
        }

        if (!IsWritable(installDirectory))
        {
            message = "安装目录不可写，请用管理员权限手动解压覆盖";
            return false;
        }

        var scriptDirectory = Path.Combine(Path.GetTempPath(), "BetterPinyin");
        Directory.CreateDirectory(scriptDirectory);
        var scriptPath = Path.Combine(scriptDirectory, UpdateScript.FileName);
        File.WriteAllText(scriptPath, UpdateScript.Build(), Encoding.ASCII);

        var logPath = Path.Combine(Path.GetDirectoryName(extractedDirectory) ?? scriptDirectory, "update.log");

        // 参数全部走命令行（CreateProcess 是 UTF-16），脚本正文保持纯 ASCII，中文路径才不会乱码。
        var arguments =
            $"/c \"\"{scriptPath}\" \"{extractedDirectory}\" \"{installDirectory}\" " +
            $"\"{executableName}\" {Environment.ProcessId} \"{logPath}\"\"";

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            message = $"正在更新到 {release.Tag}，程序会自动重启";
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"启动更新脚本失败：{ex.GetType().Name} {ex.Message}");
            message = "启动更新脚本失败，请手动解压覆盖";
            return false;
        }
    }

    /// <summary>
    /// 安装目录是否可写。
    /// </summary>
    /// <remarks>
    /// 放在 Program Files 时没有写权限，与其让脚本静默失败，不如先探一下再给用户明确的提示。
    /// </remarks>
    private static bool IsWritable(string directory)
    {
        var probe = Path.Combine(directory, $".update-probe-{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string SafeName(string tag)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(tag.Length);
        foreach (var character in tag)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.Length == 0 ? "latest" : builder.ToString();
    }
}

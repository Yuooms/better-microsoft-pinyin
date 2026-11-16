using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Interop.Startup;

/// <summary>从计划任务文件里读出来的关键字段。</summary>
/// <param name="Command">要启动的程序路径。</param>
/// <param name="Arguments">附带的参数。</param>
/// <param name="RunLevel">运行级别，<c>HighestAvailable</c> 表示提权运行。</param>
public readonly record struct StartupTaskDefinition(string? Command, string? Arguments, string? RunLevel)
{
    /// <summary>该任务是否以最高权限运行。</summary>
    public bool RunsAsAdmin =>
        string.Equals(RunLevel, "HighestAvailable", StringComparison.OrdinalIgnoreCase);
}

/// <summary>开机启动任务的当前状态。</summary>
/// <param name="Registered">任务文件是否存在。</param>
/// <param name="MatchesCurrent">命令是否正好指向当前程序（路径与参数都一致）。</param>
/// <param name="RunsAsAdmin">任务是否登记为以最高权限运行。</param>
/// <param name="Command">任务里的命令行；未登记或解析失败时为 <c>null</c>。</param>
public readonly record struct StartupTaskState(
    bool Registered,
    bool MatchesCurrent,
    bool RunsAsAdmin,
    string? Command);

/// <summary>
/// 开机启动的「以管理员身份运行」变体，由计划任务实现。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要用计划任务：注册表 Run 键启动的进程永远拿的是当前用户的普通令牌，
/// 想让它跑到高完整性级别只能靠 UAC，而开机时不可能有人去点「是」。
/// 计划任务带 <c>RunLevel=HighestAvailable</c> 时由任务计划服务直接拉起，
/// 不经过 UAC，是 Windows 上唯一的静默提权开机启动方式。
/// </para>
/// <para>
/// 有代价：登记与删除任务本身需要管理员权限，所以开启/关闭这个开关会各弹一次 UAC。
/// 这是系统限制，不是实现偷懒——普通权限根本写不了根目录下的任务。
/// </para>
/// <para>
/// 状态判定不调用 <c>schtasks /Query</c>：任务计划服务把每个任务都存成
/// <c>%SystemRoot%\System32\Tasks\&lt;名字&gt;</c> 下的 UTF-16 XML，任何用户都能读。
/// 直接读文件既不用起进程、也不用解析本地化的控制台输出，还能顺便核对
/// <c>RunLevel</c> 到底是不是提权——只查「存在」是不够的。
/// </para>
/// </remarks>
public sealed class ElevatedStartupTask
{
    /// <summary>本程序使用的任务名。</summary>
    public const string DefaultTaskName = "BetterMicrosoftPinyin";

    /// <summary>读取常见异常：一律记日志返回失败，不让设置页崩掉。</summary>
    private static bool IsExpected(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or InvalidOperationException
            or System.Security.SecurityException or Win32Exception;

    /// <summary>创建一个登记器。</summary>
    /// <param name="executablePath">要启动的 exe 路径。</param>
    /// <param name="arguments">附在 exe 后面的参数，可为空。</param>
    /// <param name="taskName">任务名，默认 <see cref="DefaultTaskName"/>。</param>
    public ElevatedStartupTask(
        string executablePath,
        string? arguments = null,
        string taskName = DefaultTaskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);

        ExecutablePath = Path.GetFullPath(executablePath);
        Arguments = arguments?.Trim() ?? string.Empty;
        TaskName = taskName;
    }

    /// <summary>要启动的 exe 完整路径。</summary>
    public string ExecutablePath { get; }

    /// <summary>附在 exe 后面的参数（已去除首尾空白）。</summary>
    public string Arguments { get; }

    /// <summary>任务名。</summary>
    public string TaskName { get; }

    /// <summary>
    /// 任务文件的路径。
    /// </summary>
    /// <remarks>
    /// 32 位进程在 64 位系统上会被重定向到 SysWOW64，那里没有 Tasks 目录，
    /// 所以目录不存在时退回 <c>%WINDIR%\System32\Tasks</c>。
    /// </remarks>
    public string TaskFilePath
    {
        get
        {
            var tasks = Path.Combine(Environment.SystemDirectory, "Tasks");
            if (!Directory.Exists(tasks))
            {
                tasks = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
            }

            return Path.Combine(tasks, TaskName);
        }
    }

    /// <summary>
    /// 拼出计划任务的 XML。
    /// </summary>
    /// <param name="executablePath">要启动的程序路径（不写引号，XML 元素本身已分隔好）。</param>
    /// <param name="arguments">参数。</param>
    /// <param name="userId">任务归属的账户，形如 <c>MACHINE\User</c>。</param>
    /// <returns>UTF-16 的完整任务定义。</returns>
    /// <remarks>
    /// 几处不能省的设置：
    /// <list type="bullet">
    /// <item><c>RunLevel=HighestAvailable</c>：本功能存在的理由。</item>
    /// <item><c>LogonType=InteractiveToken</c>：要有桌面和托盘；用密码登录型会跑进会话 0。</item>
    /// <item><c>ExecutionTimeLimit=PT0S</c>：不限时长。默认是 72 小时，到点任务会被硬停——
    /// 一个常驻托盘程序三天后自己消失，很难排查。</item>
    /// <item><c>DisallowStartIfOnBatteries=false</c> 与 <c>StopIfGoingOnBatteries=false</c>：
    /// 这两项默认是 true，笔记本一拔电源就不启动或直接被停掉。</item>
    /// <item><c>Priority=4</c>：普通优先级。计划任务默认是 7（低于普通），而键盘钩子的回调
    /// 跑在本进程的线程上，系统繁忙时低优先级会拖慢候选浮窗的反应。</item>
    /// </list>
    /// </remarks>
    public static string BuildTaskXml(string executablePath, string arguments, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var command = Escape(executablePath.Trim());
        var argumentXml = string.IsNullOrWhiteSpace(arguments)
            ? string.Empty
            : $"      <Arguments>{Escape(arguments.Trim())}</Arguments>\n";

        return
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo>\n" +
            "    <Description>Better 微软拼音：登录后以管理员权限在后台运行，为管理员权限的程序提供云联想。</Description>\n" +
            "  </RegistrationInfo>\n" +
            "  <Triggers>\n" +
            "    <LogonTrigger>\n" +
            "      <Enabled>true</Enabled>\n" +
            $"      <UserId>{Escape(userId)}</UserId>\n" +
            "    </LogonTrigger>\n" +
            "  </Triggers>\n" +
            "  <Principals>\n" +
            "    <Principal id=\"Author\">\n" +
            $"      <UserId>{Escape(userId)}</UserId>\n" +
            "      <LogonType>InteractiveToken</LogonType>\n" +
            "      <RunLevel>HighestAvailable</RunLevel>\n" +
            "    </Principal>\n" +
            "  </Principals>\n" +
            "  <Settings>\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
            "    <AllowHardTerminate>true</AllowHardTerminate>\n" +
            "    <StartWhenAvailable>false</StartWhenAvailable>\n" +
            "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n" +
            "    <IdleSettings>\n" +
            "      <StopOnIdleEnd>false</StopOnIdleEnd>\n" +
            "      <RestartOnIdle>false</RestartOnIdle>\n" +
            "    </IdleSettings>\n" +
            "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
            "    <Enabled>true</Enabled>\n" +
            "    <Hidden>false</Hidden>\n" +
            "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n" +
            "    <WakeToRun>false</WakeToRun>\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
            "    <Priority>4</Priority>\n" +
            "  </Settings>\n" +
            "  <Actions Context=\"Author\">\n" +
            "    <Exec>\n" +
            $"      <Command>{command}</Command>\n" +
            argumentXml +
            "    </Exec>\n" +
            "  </Actions>\n" +
            "</Task>\n";
    }

    /// <summary>拼出 <c>schtasks /Create</c> 的参数串。</summary>
    /// <param name="taskName">任务名。</param>
    /// <param name="xmlPath">任务 XML 文件路径。</param>
    /// <returns>可直接交给 schtasks.exe 的参数。</returns>
    /// <remarks>
    /// 用 <c>/XML</c> 而不是 <c>/TR</c>：<c>/TR</c> 要把路径、参数、引号挤进一个字符串，
    /// 路径里带空格或引号时几乎必然拼错，而且没法设置 <c>ExecutionTimeLimit</c> 这些项。
    /// </remarks>
    public static string BuildCreateArguments(string taskName, string xmlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlPath);
        return $"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /F";
    }

    /// <summary>拼出 <c>schtasks /Delete</c> 的参数串。</summary>
    /// <param name="taskName">任务名。</param>
    public static string BuildDeleteArguments(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        return $"/Delete /TN \"{taskName}\" /F";
    }

    /// <summary>
    /// 解析任务文件内容。
    /// </summary>
    /// <param name="xml">任务 XML 文本。</param>
    /// <param name="definition">解析结果。</param>
    /// <returns>能解析出结构返回 true。</returns>
    /// <remarks>
    /// 按 <c>LocalName</c> 取值，不绑定命名空间：任务计划服务写文件时会带上
    /// <c>2004/02/mit/task</c> 命名空间，而不同 Windows 版本的任务版本号也不一样，
    /// 硬绑命名空间只会在换系统后莫名其妙地解析失败。
    /// </remarks>
    public static bool TryParseDefinition(string? xml, out StartupTaskDefinition definition)
    {
        definition = default;

        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            var root = document.Root;
            if (root is null ||
                !string.Equals(root.Name.LocalName, "Task", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var command = Find(root, "Command");
            var arguments = Find(root, "Arguments");
            var runLevel = Find(root, "RunLevel");

            // 连 Command 都没有的任务没法启动任何东西，当作无效。
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            definition = new StartupTaskDefinition(command, arguments, runLevel);
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读取当前任务状态。
    /// </summary>
    /// <param name="state">任务状态；文件不存在时为「未登记」。</param>
    /// <param name="error">读取失败的原因；成功时为 <c>null</c>。</param>
    /// <returns>读取成功（不代表已登记）返回 true。</returns>
    public bool TryProbe(out StartupTaskState state, out string? error)
    {
        state = default;
        error = null;

        try
        {
            if (!File.Exists(TaskFilePath))
            {
                return true;
            }

            var xml = File.ReadAllText(TaskFilePath);
            if (!TryParseDefinition(xml, out var definition))
            {
                // 文件在但读不懂：报为「已登记、指向不明」，不要当成没登记去覆盖。
                state = new StartupTaskState(true, MatchesCurrent: false, RunsAsAdmin: false, Command: null);
                error = "任务文件存在，但内容不是可识别的任务定义";
                return false;
            }

            var command = definition.Command?.Trim();
            var arguments = (definition.Arguments ?? string.Empty).Trim();

            state = new StartupTaskState(
                Registered: true,
                MatchesCurrent:
                    string.Equals(command, ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(arguments, Arguments, StringComparison.OrdinalIgnoreCase),
                RunsAsAdmin: definition.RunsAsAdmin,
                Command: string.IsNullOrEmpty(arguments) ? command : $"{command} {arguments}");

            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 登记（或覆盖）开机启动任务。
    /// </summary>
    /// <param name="error">失败原因；成功时为 <c>null</c>。</param>
    /// <returns>登记成功返回 true。</returns>
    /// <remarks>
    /// 每次都用当前进程路径重写一遍，所以程序被移动后再开关一次就能修好。
    /// 当前不是管理员时通过 <c>runas</c> 拉起提权的 schtasks，用户会看到一次 UAC。
    /// </remarks>
    public bool TryEnable(out string? error)
    {
        error = null;

        if (!File.Exists(ExecutablePath))
        {
            error = $"程序路径不存在：{ExecutablePath}";
            return false;
        }

        var xmlPath = Path.Combine(
            Path.GetTempPath(),
            $"BetterPinyin-task-{Guid.NewGuid():N}.xml");

        try
        {
            var userId = WindowsIdentity.GetCurrent().Name;
            var xml = BuildTaskXml(ExecutablePath, Arguments, userId);

            // schtasks 只接受 Unicode（带 BOM）的任务文件，写成 UTF-8 会被判为格式错误。
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);

            if (!TryRunSchtasks(
                    BuildCreateArguments(TaskName, xmlPath),
                    out var exitCode,
                    out var output,
                    out var runError))
            {
                error = runError ?? $"登记任务失败（schtasks 退出码 {exitCode}）{FormatOutput(output)}";
                return false;
            }

            // schtasks 返回 0 也可能什么都没写进去（权限不足时它有时只打印警告），
            // 所以以「文件真的出现了」为准。
            if (!TryProbe(out var state, out _) || !state.Registered)
            {
                error = "任务已提交，但没有在系统里找到它。请检查任务计划程序中的权限设置。";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时文件删不掉不影响功能，系统迟早会清理。
            }
        }
    }

    /// <summary>
    /// 删除开机启动任务。
    /// </summary>
    /// <param name="error">失败原因；成功时为 <c>null</c>。</param>
    /// <returns>删除成功或本来就没有返回 true。</returns>
    /// <remarks>
    /// 任务不存在时直接返回成功：此时没必要为了删一个不存在的东西弹 UAC。
    /// </remarks>
    public bool TryDisable(out string? error)
    {
        error = null;

        if (!File.Exists(TaskFilePath))
        {
            return true;
        }

        try
        {
            if (!TryRunSchtasks(
                    BuildDeleteArguments(TaskName),
                    out var exitCode,
                    out var output,
                    out var runError))
            {
                error = runError ?? $"删除任务失败（schtasks 退出码 {exitCode}）{FormatOutput(output)}";
                return false;
            }

            if (File.Exists(TaskFilePath))
            {
                error = "任务计划程序仍报告该任务存在，可能被组策略锁定。";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
    }

    private static string? Find(XElement root, string localName) =>
        root.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim();

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>把 schtasks 的输出压成一行，方便塞进状态栏。</summary>
    private static string FormatOutput(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var single = string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"：{single}";
    }

    /// <summary>
    /// 调用 schtasks.exe；当前不是管理员时通过 UAC 提权调用。
    /// </summary>
    /// <returns>退出码为 0 返回 true。</returns>
    private static bool TryRunSchtasks(
        string arguments,
        out int exitCode,
        out string output,
        out string? error)
    {
        exitCode = -1;
        output = string.Empty;
        error = null;

        var elevated = ProcessIntegrity.IsSelfElevated;
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),

            // 需要提权时必须经过 Shell 才能用 runas 动词；此时拿不到输出，只认退出码。
            UseShellExecute = !elevated,
            CreateNoWindow = elevated,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = arguments,
            WorkingDirectory = Path.GetTempPath(),
        };

        if (elevated)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            // schtasks 的控制台输出是本机代码页（中文系统是 GBK），.NET 默认按 UTF-8 解码会乱码。
            // Latin1 是逐字节映射，至少不会把字节吞掉；真正有用的退出码不受影响。
            startInfo.StandardOutputEncoding = Encoding.Latin1;
            startInfo.StandardErrorEncoding = Encoding.Latin1;
        }
        else
        {
            startInfo.Verb = "runas";
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动 schtasks.exe";
                return false;
            }

            if (elevated)
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                output = $"{stdout} {stderr}".Trim();
            }

            process.WaitForExit();
            exitCode = process.ExitCode;
            return exitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 上点了「否」。
            error = "已取消管理员授权";
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            error = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }
}

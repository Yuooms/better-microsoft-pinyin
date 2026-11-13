namespace BetterPinyin.Core.Updates;

/// <summary>
/// 自替换脚本的正文。
/// </summary>
/// <remarks>
/// <para>
/// 程序自己在运行，exe 被锁住，没法直接覆盖自己。做法是：先把新版本解压到临时目录，
/// 再起一个 <c>cmd.exe</c> 脚本等本进程退出，然后用 <c>robocopy</c> 覆盖安装目录并重新启动。
/// </para>
/// <para>
/// 脚本内容保持纯 ASCII，路径全部通过命令行参数传入：
/// <c>cmd.exe</c> 按系统代码页读取 .bat 文件，脚本里写中文路径会在非中文系统或
/// 非 936 代码页下变成乱码；而命令行参数是 CreateProcess 的 UTF-16 字符串，不存在这个问题。
/// </para>
/// </remarks>
public static class UpdateScript
{
    /// <summary>脚本文件名。</summary>
    public const string FileName = "apply-update.cmd";

    /// <summary>等待旧进程退出的最长秒数。超时也继续复制，避免永远卡住。</summary>
    public const int WaitSeconds = 90;

    /// <summary>
    /// 生成脚本正文。
    /// </summary>
    /// <returns>可直接写入磁盘的脚本内容。</returns>
    public static string Build()
    {
        var lines = new[]
        {
            "@echo off",
            "setlocal",
            "rem args: 1=extracted dir 2=install dir 3=exe name 4=pid to wait 5=log file",
            "set \"SRC=%~1\"",
            "set \"DST=%~2\"",
            "set \"EXE=%~3\"",
            "set \"WAITPID=%~4\"",
            "set \"LOG=%~5\"",
            "set /a TRIES=0",
            "rem wait until the running instance is gone; matching the exe name avoids localized tasklist output",
            ":wait",
            "tasklist /FI \"PID eq %WAITPID%\" /NH 2>nul | findstr /I /C:\"%EXE%\" >nul",
            "if errorlevel 1 goto copy",
            "set /a TRIES+=1",
            $"if %TRIES% GEQ {WaitSeconds} goto copy",
            "timeout /t 1 /nobreak >nul",
            "goto wait",
            "rem /E copies subdirectories, /R:3 retries locked files a few times",
            ":copy",
            "robocopy \"%SRC%\" \"%DST%\" /E /NFL /NDL /NJH /NJS /NP /R:3 /W:1 >\"%LOG%\" 2>&1",
            "echo robocopy exit=%ERRORLEVEL% >>\"%LOG%\"",
            "start \"\" \"%DST%\\%EXE%\"",
            "rd /s /q \"%SRC%\" 2>nul",
            "rem self-delete: (goto) ends the batch context, then the rest of the line still runs",
            "(goto) 2>nul & del \"%~f0\"",
        };

        // 行尾必须是 CRLF：cmd.exe 解析 goto 标签时对只有 LF 的文件并不可靠。
        return string.Join("\r\n", lines) + "\r\n";
    }
}

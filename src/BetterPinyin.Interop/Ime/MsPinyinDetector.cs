using System.Runtime.InteropServices;
using BetterPinyin.Interop.Windowing;
using Microsoft.Win32;

namespace BetterPinyin.Interop.Ime;

/// <summary>
/// 微软拼音环境探测。
/// </summary>
/// <remarks>
/// 这里刻意只做「够用就好」的判定，不追求精确识别输入法实例：
/// 真正判断「现在是否在中文组字」依靠候选窗是否出现，而不是靠输入法身份，
/// 因为 TSF 输入法的激活状态无法跨进程可靠查询。
/// </remarks>
public static class MsPinyinDetector
{
    /// <summary>简体中文的语言 Id（0x0804）。</summary>
    public const ushort SimplifiedChineseLanguageId = 0x0804;

    /// <summary>微软拼音（ChsIME）的 TSF 类标识。</summary>
    public const string MsPinyinClsid = "{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}";

    private const string SettingsKeyPath = @"Software\Microsoft\InputMethod\Settings\CHS";
    private const string TipKeyPath = @"SOFTWARE\Microsoft\CTF\TIP\{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}";

    /// <summary>取前台窗口所属线程的键盘布局。</summary>
    /// <returns>键盘布局句柄，失败返回 <see cref="IntPtr.Zero"/>。</returns>
    public static IntPtr GetForegroundKeyboardLayout()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        return threadId == 0 ? IntPtr.Zero : NativeMethods.GetKeyboardLayout(threadId);
    }

    /// <summary>取前台键盘布局对应的语言 Id。</summary>
    /// <returns>语言 Id，失败返回 0。</returns>
    public static ushort GetForegroundLanguageId()
    {
        var layout = GetForegroundKeyboardLayout();
        return layout == IntPtr.Zero ? (ushort)0 : (ushort)(layout.ToInt64() & 0xFFFF);
    }

    /// <summary>前台是否处于简体中文键盘布局（0x0804）。</summary>
    public static bool IsChineseLayout()
    {
        return GetForegroundLanguageId() == SimplifiedChineseLanguageId;
    }

    /// <summary>本机是否安装了微软拼音。</summary>
    public static bool IsMsPinyinInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TipKeyPath);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 读取微软拼音的用户设置快照（只读，用于诊断页展示与能力判断）。
    /// </summary>
    /// <returns>设置项名称到值的映射，读不到时返回空字典。</returns>
    public static IReadOnlyDictionary<string, string> ReadSettingsSnapshot()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            if (key is null)
            {
                return result;
            }

            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name);
                result[name] = value?.ToString() ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // 注册表被策略锁定或权限不足时静默降级。
        }

        return result;
    }

    /// <summary>
    /// 从设置快照中判断模糊音是否开启。
    /// </summary>
    /// <param name="snapshot">设置快照。</param>
    /// <returns>开启返回 true；无法判断时返回 true（模糊音是更宽松的匹配策略，误判代价更低）。</returns>
    public static bool IsFuzzyInputEnabled(IReadOnlyDictionary<string, string> snapshot)
    {
        foreach (var (name, value) in snapshot)
        {
            if (!name.Contains("Fuzzy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Contains("Pair", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (normalized.Length > 0 && normalized[0] == '0')
            {
                return false;
            }

            return true;
        }

        return true;
    }

    /// <summary>
    /// 从设置快照中判断是否启用了双拼。启用双拼时按键串不再是全拼，云联想必须停用。
    /// </summary>
    /// <param name="snapshot">设置快照。</param>
    /// <returns>启用返回 true。</returns>
    public static bool IsDoublePinyinEnabled(IReadOnlyDictionary<string, string> snapshot)
    {
        foreach (var (name, value) in snapshot)
        {
            if (!name.Contains("Pinyin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!name.Contains("Double", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Shuang", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (normalized.Length > 0 && normalized[0] != '0')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把语言 Id 转成便于日志阅读的字符串。</summary>
    /// <param name="languageId">语言 Id。</param>
    /// <returns>形如 <c>0x0804</c> 的字符串。</returns>
    public static string DescribeLanguage(ushort languageId) => $"0x{languageId:X4}";
}

/// <summary>
/// 用于读取窗口所属进程名的辅助方法。
/// </summary>
public static class WindowProcess
{
    /// <summary>获取窗口所属进程 Id。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>进程 Id，失败返回 0。</returns>
    public static uint GetProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>获取窗口所属进程名。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>进程名，失败返回空串。</returns>
    public static string GetProcessName(IntPtr hwnd)
    {
        var pid = GetProcessId(hwnd);
        if (pid == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// 用于把 COM 错误码格式化的辅助方法。
/// </summary>
public static class ComError
{
    /// <summary>把 HRESULT 格式化成十六进制字符串。</summary>
    /// <param name="hr">返回值。</param>
    /// <returns>形如 <c>0x80004005</c> 的字符串。</returns>
    public static string Describe(int hr) => $"0x{hr:X8}";
}

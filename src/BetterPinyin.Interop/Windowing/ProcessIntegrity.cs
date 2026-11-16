using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterPinyin.Interop.Windowing;

/// <summary>
/// Windows 完整性级别。
/// </summary>
/// <remarks>
/// 数值直接取 SID 的 RID，因此可以按大小比较：级别越高，能做的事越多。
/// </remarks>
public enum IntegrityLevel
{
    /// <summary>读不到（进程已退出，或没有查询权限）。</summary>
    Unknown = 0,

    /// <summary>低完整性（沙箱、受保护浏览模式）。</summary>
    Low = 0x1000,

    /// <summary>普通用户。绝大多数程序都在这一级。</summary>
    Medium = 0x2000,

    /// <summary>管理员（"以管理员身份运行"）。</summary>
    High = 0x3000,

    /// <summary>系统级（服务）。</summary>
    System = 0x4000,
}

/// <summary>
/// 读取进程的完整性级别。
/// </summary>
/// <remarks>
/// <para>
/// 用途只有一个：判断"当前前台程序是不是比我们权限高"。
/// 这不是好奇心，而是解释一种静默失效：Windows 的 UIPI 规定，
/// <b>低完整性进程的低级键盘钩子收不到发往高完整性窗口的按键</b>——
/// 钩子不会报错，只是永远不被调用。于是程序在管理员权限的程序里
/// 表现成"完全没反应、日志一条没有"，看起来像 bug，其实是被系统拦掉了。
/// </para>
/// <para>
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> 是专门为这种情况提供的权限：
/// 它允许低权限进程查询高权限进程的基本信息。因此大部分情况下能拿到真实级别；
/// 拿不到时返回 <see cref="IntegrityLevel.Unknown"/>，调用方不应据此报警。
/// </para>
/// </remarks>
public static class ProcessIntegrity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    /// <summary>TokenIntegrityLevel。</summary>
    private const int TokenIntegrityLevel = 25;

    private const int ErrorInsufficientBuffer = 122;

    /// <summary>ERROR_ACCESS_DENIED。</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>进程级缓存。进程号会被复用，但同一次运行里足够用。</summary>
    private static readonly ConcurrentDictionary<uint, (IntegrityLevel Level, bool Denied)> Cache = new();

    private static readonly Lazy<IntegrityLevel> Self = new(
        () => ForProcess((uint)Environment.ProcessId),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>本进程的完整性级别。</summary>
    public static IntegrityLevel Current => Self.Value;

    /// <summary>本进程是否以管理员（或更高）权限运行。</summary>
    public static bool IsSelfElevated => Current >= IntegrityLevel.High;

    /// <summary>把 SID 的 RID 映射成级别。</summary>
    /// <param name="rid">RID，例如 <c>0x2000</c>。</param>
    /// <returns>级别；未知取值返回 <see cref="IntegrityLevel.Unknown"/>。</returns>
    /// <remarks>
    /// 单列成纯函数是为了可测：这里一旦写错，判定方向就会反过来，
    /// 提示语会出现在完全正常的机器上。
    /// </remarks>
    public static IntegrityLevel FromRid(int rid) => rid switch
    {
        >= 0x4000 => IntegrityLevel.System,
        >= 0x3000 => IntegrityLevel.High,
        >= 0x2000 => IntegrityLevel.Medium,
        >= 0x1000 => IntegrityLevel.Low,
        _ => IntegrityLevel.Unknown,
    };

    /// <summary>级别是否高于本进程。</summary>
    /// <param name="level">要比较的级别。</param>
    /// <returns>需要更高权限才能读到它的输入时返回 true。</returns>
    public static bool IsAboveSelf(IntegrityLevel level) =>
        level != IntegrityLevel.Unknown && level > Current;

    /// <summary>取指定进程的完整性级别。</summary>
    /// <param name="processId">进程号。</param>
    /// <returns>级别；读不到时返回 <see cref="IntegrityLevel.Unknown"/>。</returns>
    public static IntegrityLevel ForProcess(uint processId) => Read(processId).Level;

    /// <summary>
    /// 该进程是否"本程序辅助不了"：级别更高，或者根本读不到。
    /// </summary>
    /// <param name="processId">进程号。</param>
    /// <returns>无法辅助返回 true。</returns>
    /// <remarks>
    /// 单独开这个方法，是因为"读不到"和"级别低"必须区分开：
    /// 普通权限进程去 OpenProcessToken 读管理员进程的令牌会被直接拒绝（错误 5），
    /// 此时级别是 Unknown，但它恰恰是最需要提示的一种情况。
    /// 反过来，进程已退出也会读不到，所以要按错误码区分——已被拒绝才算数。
    /// </remarks>
    public static bool IsAboveSelfOrDenied(uint processId)
    {
        var (level, denied) = Read(processId);
        return denied || (level != IntegrityLevel.Unknown && level > Current);
    }

    /// <summary>窗口所属进程是否辅助不了。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static bool IsWindowAboveSelfOrDenied(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return threadId != 0 && IsAboveSelfOrDenied(processId);
    }

    /// <summary>取窗口所属进程的完整性级别。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>级别；窗口无效或读不到时返回 <see cref="IntegrityLevel.Unknown"/>。</returns>
    public static IntegrityLevel ForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntegrityLevel.Unknown;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return threadId == 0 ? IntegrityLevel.Unknown : ForProcess(processId);
    }

    /// <summary>取前台窗口所属进程的完整性级别。</summary>
    public static IntegrityLevel Foreground => ForWindow(NativeMethods.GetForegroundWindow());

    /// <summary>级别的中文说明，用于日志与界面。</summary>
    /// <param name="level">级别。</param>
    /// <returns>例如"管理员"。</returns>
    public static string Describe(IntegrityLevel level) => level switch
    {
        IntegrityLevel.Low => "低",
        IntegrityLevel.Medium => "普通",
        IntegrityLevel.High => "管理员",
        IntegrityLevel.System => "系统",
        _ => "未知",
    };

    private static (IntegrityLevel Level, bool Denied) Read(uint processId)
    {
        if (processId == 0)
        {
            return (IntegrityLevel.Unknown, false);
        }

        if (Cache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        var result = ReadLevel(processId);

        // 「已退出」不缓存，下次可能就有新进程复用这个号了；「被拒绝」是稳定结论，可以缓存。
        if (result.Level != IntegrityLevel.Unknown || result.Denied)
        {
            if (Cache.Count > 256)
            {
                Cache.Clear();
            }

            Cache[processId] = result;
        }

        return result;
    }

    private static (IntegrityLevel Level, bool Denied) ReadLevel(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return (IntegrityLevel.Unknown, Marshal.GetLastWin32Error() == ErrorAccessDenied);
        }

        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token) || token == IntPtr.Zero)
            {
                // 这一步被拒绝正是 UIPI 在起作用：低权限进程读不到高权限进程的令牌。
                return (IntegrityLevel.Unknown, Marshal.GetLastWin32Error() == ErrorAccessDenied);
            }

            // 先问长度，再按长度取。两步之间令牌理论上可能变化，重试一次就够。
            if (!GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size) &&
                Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                return (IntegrityLevel.Unknown, false);
            }

            if (size <= 0 || size > 4096)
            {
                return (IntegrityLevel.Unknown, false);
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                {
                    return (IntegrityLevel.Unknown, false);
                }

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
                var sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero)
                {
                    return (IntegrityLevel.Unknown, false);
                }

                var countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == IntPtr.Zero)
                {
                    return (IntegrityLevel.Unknown, false);
                }

                var count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return (IntegrityLevel.Unknown, false);
                }

                var ridPointer = GetSidSubAuthority(sid, (uint)(count - 1));
                if (ridPointer == IntPtr.Zero)
                {
                    return (IntegrityLevel.Unknown, false);
                }

                return (FromRid(Marshal.ReadInt32(ridPointer)), false);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (IntegrityLevel.Unknown, false);
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }

            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
}

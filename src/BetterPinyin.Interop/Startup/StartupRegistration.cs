using System.IO;
using System.Security;
using Microsoft.Win32;

namespace BetterPinyin.Interop.Startup;

/// <summary>开机启动项的当前状态。</summary>
/// <param name="Registered">注册表里是否存在这一项。</param>
/// <param name="Command">注册表里的命令；未登记时为 <c>null</c>。</param>
/// <param name="MatchesCurrent">命令是否正好指向当前程序（路径与参数都一致）。</param>
public readonly record struct StartupRegistrationState(bool Registered, string? Command, bool MatchesCurrent);

/// <summary>
/// 当前用户的开机启动登记（<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 只写当前用户的 Run 键：不需要管理员权限，不改系统级设置，卸载时删掉这个值即可。
/// 不用「启动」文件夹里的快捷方式，是因为快捷方式的目标路径写死在 .lnk 里；
/// 程序被移动或重新发布后，旧快捷方式会静默失效。注册表值可以在每次启动时按
/// 当前进程路径重写一遍，自己修好自己。
/// </para>
/// <para>
/// 所有方法都不抛异常，失败时通过 <c>error</c> 返回原因：开机启动只是便利功能，
/// 不能因为注册表被策略锁住就让设置页崩掉。
/// </para>
/// </remarks>
public sealed class StartupRegistration
{
    /// <summary>当前用户的开机启动键。</summary>
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>本程序使用的值名。</summary>
    public const string DefaultValueName = "BetterMicrosoftPinyin";

    /// <summary>创建一个登记器。</summary>
    /// <param name="executablePath">要启动的 exe 路径。</param>
    /// <param name="arguments">附在 exe 后面的参数，可为空。</param>
    /// <param name="keyPath">注册表键路径，默认当前用户的 Run 键；测试可换成临时键。</param>
    /// <param name="valueName">值名，默认为 <see cref="DefaultValueName"/>。</param>
    public StartupRegistration(
        string executablePath,
        string? arguments = null,
        string keyPath = DefaultKeyPath,
        string valueName = DefaultValueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        ExecutablePath = Path.GetFullPath(executablePath);
        Arguments = arguments?.Trim() ?? string.Empty;
        KeyPath = keyPath;
        ValueName = valueName;
    }

    /// <summary>要启动的 exe 完整路径。</summary>
    public string ExecutablePath { get; }

    /// <summary>附在 exe 后面的参数（已去除首尾空白）。</summary>
    public string Arguments { get; }

    /// <summary>注册表键路径。</summary>
    public string KeyPath { get; }

    /// <summary>值名。</summary>
    public string ValueName { get; }

    /// <summary>写入注册表的完整命令行。</summary>
    public string Command => FormatCommand(ExecutablePath, Arguments);

    /// <summary>
    /// 拼出写入 Run 键的命令行。
    /// </summary>
    /// <param name="executablePath">exe 路径。</param>
    /// <param name="arguments">参数，可为空。</param>
    /// <returns>路径始终带引号，避免 <c>Program Files</c> 这类空格路径被拆成两段。</returns>
    public static string FormatCommand(string executablePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var command = $"\"{executablePath.Trim()}\"";
        var extra = arguments?.Trim();
        return string.IsNullOrEmpty(extra) ? command : $"{command} {extra}";
    }

    /// <summary>
    /// 读取当前登记状态。
    /// </summary>
    /// <param name="state">登记状态；读取失败时是默认值。</param>
    /// <param name="error">失败原因；成功时为 <c>null</c>。</param>
    /// <returns>读取成功（不代表已登记）返回 true。</returns>
    public bool TryProbe(out StartupRegistrationState state, out string? error)
    {
        state = default;
        error = null;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.OpenSubKey(KeyPath, writable: false);
            var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

            if (value is null)
            {
                return true;
            }

            if (value is not string command || string.IsNullOrWhiteSpace(command))
            {
                error = "启动项存在，但不是字符串";
                return false;
            }

            state = new StartupRegistrationState(
                Registered: true,
                Command: command,
                MatchesCurrent: string.Equals(command.Trim(), Command, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 写入或覆盖开机启动项。
    /// </summary>
    /// <param name="error">失败原因；成功时为 <c>null</c>。</param>
    /// <returns>写入成功返回 true。</returns>
    /// <remarks>
    /// 每次都按当前路径重写，所以程序被移动后再打开一次设置页就能自动修复。
    /// </remarks>
    public bool TryEnable(out string? error)
    {
        error = null;

        if (!File.Exists(ExecutablePath))
        {
            error = $"程序路径不存在：{ExecutablePath}";
            return false;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.CreateSubKey(KeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开当前用户的启动项注册表键";
                return false;
            }

            key.SetValue(ValueName, Command, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 删除开机启动项；本来就不存在也算成功。
    /// </summary>
    /// <param name="error">失败原因；成功时为 <c>null</c>。</param>
    /// <returns>删除成功或本来就没有返回 true。</returns>
    public bool TryDisable(out string? error)
    {
        error = null;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException;
}

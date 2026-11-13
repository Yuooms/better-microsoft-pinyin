using System.Windows.Input;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>
/// 采纳键的标识、键码与显示文字。
/// </summary>
/// <remarks>
/// <para>
/// 标识直接用 WPF <see cref="Key"/> 枚举名（<c>Oem3</c>、<c>D6</c>、<c>F8</c>），
/// 这样任何一个能按到的键都能存下来，不需要维护一张固定表。
/// </para>
/// <para>
/// 旧版本把手动扩展键存成数字串（<c>6789</c>），<see cref="ParseExtendedKeys"/> 仍然认这种写法；
/// 新写入统一用逗号分隔的标识（<c>D6,D7,D8,D9</c>）。
/// </para>
/// </remarks>
internal static class KeyNames
{
    /// <summary><c>·</c> 所在的键（数字 1 左侧），中文标点下打出 <c>·</c>。</summary>
    public const int Oem3 = 0xC0;

    /// <summary>扩展键最多几个（与「显示条数」的上限一致）。</summary>
    public const int MaxExtendedKeys = 4;

    /// <summary>按标识取虚拟键码。</summary>
    /// <param name="id">标识，例如 <c>Oem3</c>、<c>D6</c>。</param>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <returns>认得返回 true。</returns>
    public static bool TryResolve(string? id, out int virtualKey)
    {
        virtualKey = 0;
        var trimmed = id?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            // Enum.TryParse 会把 "6" 当成数字值 6（一个未定义的 Key），必须先挡住。
            char.IsDigit(trimmed[0]) ||
            !Enum.TryParse<Key>(trimmed, ignoreCase: true, out var key) ||
            key == Key.None)
        {
            return false;
        }

        virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    /// <summary>按标识取虚拟键码，未知标识退回 <c>·</c>。</summary>
    /// <param name="id">标识。</param>
    /// <returns>虚拟键码。</returns>
    public static int Resolve(string? id) => TryResolve(id, out var virtualKey) ? virtualKey : Oem3;

    /// <summary>把虚拟键码转成可保存的标识。</summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <returns>标识；无法识别时返回空串。</returns>
    public static string IdOf(int virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        return key == Key.None ? string.Empty : key.ToString();
    }

    /// <summary>把虚拟键码描述成界面文字。</summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <returns>键帽文字。</returns>
    public static string Describe(int virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        var code = (int)key;

        if (code >= (int)Key.D0 && code <= (int)Key.D9)
        {
            return ((char)('0' + code - (int)Key.D0)).ToString();
        }

        if (code >= (int)Key.NumPad0 && code <= (int)Key.NumPad9)
        {
            return $"小键盘 {code - (int)Key.NumPad0}";
        }

        if (code >= (int)Key.A && code <= (int)Key.Z)
        {
            return ((char)('A' + code - (int)Key.A)).ToString();
        }

        if (code >= (int)Key.F1 && code <= (int)Key.F24)
        {
            return key.ToString();
        }

        return key switch
        {
            Key.None => "?",
            Key.Oem3 => "·",
            Key.Oem1 => ";",
            Key.Oem2 => "/",
            Key.Oem4 => "[",
            Key.Oem5 => "\\",
            Key.Oem6 => "]",
            Key.Oem7 => "'",
            Key.Oem8 => "`",
            Key.Oem102 => "\\",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Tab => "Tab",
            Key.Space => "空格",
            Key.Back => "退格",
            _ => key.ToString(),
        };
    }

    /// <summary>是否主键盘数字键 0~9。</summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <returns>是数字键返回 true。</returns>
    /// <remarks>
    /// 小键盘不算：输入法不把小键盘数字当作候选序号，阈值复核对它们没有意义。
    /// </remarks>
    public static bool IsDigit(int virtualKey) => virtualKey is >= '0' and <= '9';

    /// <summary>
    /// 判断一个键能不能做采纳键。
    /// </summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <param name="reason">不能用的原因，可以显示给用户。</param>
    /// <returns>能用返回 true。</returns>
    /// <remarks>
    /// 拒绝的都是会打断输入本身的键：字母会被吞掉、空格/回车负责上屏、退格负责改拼音、
    /// 方向键与系统键另有用途。数字允许，但会盖过输入法同号候选，界面上单独提示。
    /// </remarks>
    public static bool IsAllowed(int virtualKey, out string? reason)
    {
        reason = null;
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        if (key == Key.None)
        {
            reason = "没认出这个键，换一个试试";
            return false;
        }

        if (IsModifier(key))
        {
            reason = "修饰键不能单独做采纳键";
            return false;
        }

        var code = (int)key;
        if (code >= (int)Key.A && code <= (int)Key.Z)
        {
            reason = "字母键会和拼音输入冲突";
            return false;
        }

        switch (key)
        {
            case Key.Space:
                reason = "空格用来选词，不能做采纳键";
                return false;
            case Key.Enter:
                reason = "回车用来上屏，不能做采纳键";
                return false;
            case Key.Back:
                reason = "退格用来修改拼音，不能做采纳键";
                return false;
            case Key.Escape:
                reason = "Esc 用来取消组字，不能做采纳键";
                return false;
            case Key.Delete:
            case Key.Insert:
            case Key.Home:
            case Key.End:
            case Key.PageUp:
            case Key.PageDown:
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                reason = "方向与编辑键另有用途，不能做采纳键";
                return false;
            case Key.CapsLock:
            case Key.NumLock:
            case Key.Scroll:
            case Key.PrintScreen:
            case Key.Pause:
            case Key.Apps:
                reason = "系统键不能做采纳键";
                return false;
            case Key.ImeProcessed:
            case Key.DeadCharProcessed:
            case Key.System:
                reason = "输入法或组合键状态不能做采纳键";
                return false;
        }

        return true;
    }

    /// <summary>
    /// 解析扩展键列表。
    /// </summary>
    /// <param name="text">设置里保存的字符串。</param>
    /// <returns>去重后的键码，最多 <see cref="MaxExtendedKeys"/> 个；保持原有顺序。</returns>
    /// <remarks>
    /// 兼容两种写法：新格式 <c>D6,D7,D8</c>，旧格式 <c>678</c>。
    /// 顺序就是扩展候选的显示顺序，不能排序。
    /// </remarks>
    public static IReadOnlyList<int> ParseExtendedKeys(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<int>();
        }

        IEnumerable<string> parts;
        var trimmed = text.Trim();
        if (trimmed.Contains(','))
        {
            parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else if (trimmed.All(ch => ch is >= '1' and <= '9'))
        {
            // 旧格式：每个字符就是一个数字键。
            parts = trimmed.Select(ch => "D" + ch);
        }
        else
        {
            parts = new[] { trimmed };
        }

        var seen = new HashSet<int>();
        var keys = new List<int>(MaxExtendedKeys);
        foreach (var part in parts)
        {
            if (keys.Count >= MaxExtendedKeys)
            {
                break;
            }

            if (TryResolve(part, out var virtualKey) && seen.Add(virtualKey))
            {
                keys.Add(virtualKey);
            }
        }

        return keys;
    }

    /// <summary>把键码列表序列化成设置里保存的字符串。</summary>
    /// <param name="keys">键码列表。</param>
    /// <returns>逗号分隔的标识。</returns>
    public static string SerializeExtendedKeys(IEnumerable<int> keys)
    {
        var ids = new List<string>(MaxExtendedKeys);
        foreach (var key in keys)
        {
            var id = IdOf(key);
            if (!string.IsNullOrEmpty(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }

            if (ids.Count >= MaxExtendedKeys)
            {
                break;
            }
        }

        return string.Join(",", ids);
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftShift or Key.RightShift or
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;
}

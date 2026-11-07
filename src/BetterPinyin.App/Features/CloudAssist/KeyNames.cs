namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>一个可选的采纳键。</summary>
/// <param name="Id">设置里保存的标识。</param>
/// <param name="Label">界面上显示的键帽文字。</param>
/// <param name="VirtualKey">虚拟键码。</param>
/// <param name="Hint">使用说明，界面上作为提示。</param>
internal readonly record struct AcceptKeyOption(string Id, string Label, int VirtualKey, string Hint);

/// <summary>采纳键的名字与键码映射。</summary>
/// <remarks>
/// 键位刻意避开微软拼音的翻页键（<c>-</c> <c>=</c> <c>[</c> <c>]</c> <c>,</c> <c>.</c>）。
/// 数字键 1~9 也开放：用户可以把云联想固定到「候选 2」，与写入微软拼音自定义短语的位置对齐。
/// 代价是会盖过输入法同号候选，所以扩展槽分配数字时要跳过已被采纳键占用的那个。
/// </remarks>
internal static class KeyNames
{
    /// <summary><c>·</c> 所在的键（数字 1 左侧），中文标点下打出 <c>·</c>。</summary>
    public const int Oem3 = 0xC0;

    /// <summary>可选键列表。</summary>
    public static IReadOnlyList<AcceptKeyOption> Options { get; } = BuildOptions();

    private static IReadOnlyList<AcceptKeyOption> BuildOptions()
    {
        var options = new List<AcceptKeyOption>
        {
            new("Oem3", "·", Oem3, "数字 1 左侧的键，中文标点下输出「·」"),
            new("Tab", "Tab", 0x09, "微软拼音组字时按 Tab 会调出 Bing 搜索，可能冲突"),
            new("Semicolon", ";", 0xBA, "分号"),
            new("Backslash", "\\", 0xDC, "反斜杠"),
            new("Quote", "'", 0xDE, "单引号"),
            new("Slash", "/", 0xBF, "斜杠"),
        };

        for (var digit = 1; digit <= 9; digit++)
        {
            options.Add(new AcceptKeyOption(
                $"D{digit}",
                digit.ToString(),
                (char)('0' + digit),
                "数字键会盖过输入法同号候选，建议与「写入位置」设成同一个数字"));
        }

        return options;
    }

    /// <summary>按标识取虚拟键码，未知标识退回 <c>·</c>。</summary>
    /// <param name="id">标识。</param>
    /// <returns>虚拟键码。</returns>
    public static int Resolve(string? id)
    {
        foreach (var option in Options)
        {
            if (string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return option.VirtualKey;
            }
        }

        return Oem3;
    }

    /// <summary>把虚拟键码描述成界面文字。</summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <returns>键帽文字。</returns>
    public static string Describe(int virtualKey) => virtualKey switch
    {
        Oem3 => "·",
        0x09 => "Tab",
        0xBA => ";",
        0xDC => "\\",
        0xDE => "'",
        0xBF => "/",
        >= '1' and <= '9' => ((char)virtualKey).ToString(),
        _ => "?",
    };

    /// <summary>是否数字键 1~9。</summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <returns>是数字键返回 true。</returns>
    public static bool IsDigit(int virtualKey) => virtualKey is >= '1' and <= '9';

    /// <summary>
    /// 解析手动指定的扩展键，例如 <c>6789</c>。
    /// </summary>
    /// <param name="text">用户输入。</param>
    /// <returns>去重后的数字键列表。</returns>
    public static IReadOnlyList<int> ParseDigits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<int>();
        }

        var seen = new HashSet<int>();
        var keys = new List<int>(4);
        foreach (var ch in text)
        {
            if (ch is >= '1' and <= '9' && seen.Add(ch) && keys.Count < 4)
            {
                keys.Add(ch);
            }
        }

        return keys;
    }
}

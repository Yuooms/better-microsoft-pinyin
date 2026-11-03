using BetterPinyin.Core.Cloud;
using BetterPinyin.Core.Ranking;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>云候选所在的槽位。</summary>
internal enum SuggestionSlot
{
    /// <summary>云联想（严格）。</summary>
    Strict = 0,

    /// <summary>扩展云联想。</summary>
    Extended = 1,
}

/// <summary>一次采纳动作的目标。</summary>
/// <param name="Slot">槽位。</param>
/// <param name="Index">槽位内序号。</param>
/// <param name="Suggestion">候选。</param>
/// <param name="VirtualKey">触发这次采纳的按键。</param>
/// <param name="DigitThreshold">
/// 数字键的「冲突阈值」。大于 0 表示这是接在本地候选之后的数字键：
/// 提交前若发现本地候选数已经达到或超过该数字，说明这个编号刚刚变成了合法的本地选词，
/// 必须放弃提交并把按键还给输入法。
/// </param>
internal readonly record struct ActivationTarget(
    SuggestionSlot Slot,
    int Index,
    CloudSuggestion Suggestion,
    int VirtualKey,
    int DigitThreshold);

/// <summary>浮窗要渲染的一行。</summary>
/// <param name="Slot">槽位。</param>
/// <param name="Index">槽位内序号。</param>
/// <param name="Text">候选词。</param>
/// <param name="KeyLabel">采纳键文字；为空表示不可采纳（只展示）。</param>
internal readonly record struct SlotDescriptor(
    SuggestionSlot Slot,
    int Index,
    string Text,
    string KeyLabel);

/// <summary>
/// 「按键 → 采纳目标」的映射。
/// </summary>
/// <remarks>
/// 钩子回调运行在输入线程上，不能做 UIA、不能分配、不能加锁，所以这里在查询完成时
/// 一次性构建不可变映射，钩子侧只做一次数组线性查找（最多 5 项）。
/// 引用赋值是原子的，用 <see cref="System.Threading.Volatile"/> 读写即可，无需锁。
/// </remarks>
internal sealed class ActivationMap
{
    private readonly int[] _keys;
    private readonly ActivationTarget[] _targets;

    private ActivationMap(int[] keys, ActivationTarget[] targets)
    {
        _keys = keys;
        _targets = targets;
    }

    /// <summary>空映射。</summary>
    public static ActivationMap Empty { get; } = new(Array.Empty<int>(), Array.Empty<ActivationTarget>());

    /// <summary>已绑定的按键数量。</summary>
    public int Count => _keys.Length;

    /// <summary>已绑定的全部目标，按「严格 → 扩展」顺序排列。</summary>
    public IReadOnlyList<ActivationTarget> Targets => _targets;

    /// <summary>
    /// 构建映射。
    /// </summary>
    /// <param name="set">分区结果。</param>
    /// <param name="strictKey">云联想的采纳键。</param>
    /// <param name="extendedKeys">扩展槽的采纳键，数量决定扩展最多显示几条。</param>
    /// <returns>映射与渲染行的组合结果。</returns>
    public static (ActivationMap Map, IReadOnlyList<SlotDescriptor> Slots) Build(
        SuggestionSet set,
        int strictKey,
        IReadOnlyList<int> extendedKeys)
    {
        var keys = new List<int>(5);
        var targets = new List<ActivationTarget>(5);
        var slots = new List<SlotDescriptor>(5);

        if (set.Strict is not null && strictKey != 0)
        {
            keys.Add(strictKey);
            targets.Add(new ActivationTarget(SuggestionSlot.Strict, 0, set.Strict, strictKey, 0));
            slots.Add(new SlotDescriptor(SuggestionSlot.Strict, 0, set.Strict.Text, KeyNames.Describe(strictKey)));
        }

        for (var i = 0; i < set.Extended.Count && i < extendedKeys.Count; i++)
        {
            var key = extendedKeys[i];
            if (key == 0)
            {
                continue;
            }

            var threshold = KeyNames.IsDigit(key) ? key - '0' : 0;
            keys.Add(key);
            targets.Add(new ActivationTarget(SuggestionSlot.Extended, i, set.Extended[i], key, threshold));
            slots.Add(new SlotDescriptor(SuggestionSlot.Extended, i, set.Extended[i].Text, KeyNames.Describe(key)));
        }

        if (keys.Count == 0)
        {
            return (Empty, Array.Empty<SlotDescriptor>());
        }

        return (new ActivationMap(keys.ToArray(), targets.ToArray()), slots);
    }

    /// <summary>查找按键对应的目标。</summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <param name="target">目标。</param>
    /// <returns>命中返回 true。</returns>
    public bool TryGet(int virtualKey, out ActivationTarget target)
    {
        var keys = _keys;
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] == virtualKey)
            {
                target = _targets[i];
                return true;
            }
        }

        target = default;
        return false;
    }
}

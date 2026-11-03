namespace BetterPinyin.Contracts;

/// <summary>
/// 一次组字（composition）的快照。
/// </summary>
/// <param name="RawInput">用户当前敲入的原始字母串，例如 <c>yisitalu</c>。</param>
/// <param name="Version">单调递增的代次号。异步结果回填时必须比对，代次不符即丢弃。</param>
/// <param name="IsComposing">输入法是否仍处于组字状态。</param>
public readonly record struct CompositionSnapshot(string RawInput, long Version, bool IsComposing);

/// <summary>
/// 输入法候选列表中的一项。
/// </summary>
/// <param name="Text">候选词文本。</param>
/// <param name="Left">候选条目在屏幕坐标系中的左边界（物理像素）。</param>
/// <param name="Top">候选条目在屏幕坐标系中的上边界（物理像素）。</param>
/// <param name="Width">候选条目宽度（物理像素）。</param>
/// <param name="Height">候选条目高度（物理像素）。</param>
public readonly record struct CandidateItem(string Text, int Left, int Top, int Width, int Height);

/// <summary>
/// 候选窗读取的质量等级。上层据此决定去重策略，而不是依赖具体实现。
/// </summary>
public enum CandidateReadQuality
{
    /// <summary>没有读到任何信息。</summary>
    None = 0,

    /// <summary>只拿到候选窗矩形，没读到候选词文本。</summary>
    GeometryOnly = 1,

    /// <summary>候选词文本与矩形都拿到了，可做精确去重。</summary>
    Exact = 2,
}

/// <summary>
/// 输入法候选窗的一次读取结果。读取失败时使用 <see cref="Empty"/>。
/// </summary>
/// <param name="IsVisible">候选窗当前是否可见。</param>
/// <param name="Left">候选窗左边界（物理像素）。</param>
/// <param name="Top">候选窗上边界（物理像素）。</param>
/// <param name="Width">候选窗宽度（物理像素）。</param>
/// <param name="Height">候选窗高度（物理像素）。</param>
/// <param name="Items">按屏幕显示顺序排列的候选条目。</param>
public sealed record CandidateWindowSnapshot(
    bool IsVisible,
    int Left,
    int Top,
    int Width,
    int Height,
    IReadOnlyList<CandidateItem> Items)
{
    /// <summary>候选窗不可见，且没有任何候选。</summary>
    public static CandidateWindowSnapshot Empty { get; } =
        new(false, 0, 0, 0, 0, Array.Empty<CandidateItem>())
        {
            Quality = CandidateReadQuality.None,
        };

    /// <summary>本次读取的质量等级。</summary>
    public CandidateReadQuality Quality { get; init; } = CandidateReadQuality.None;

    /// <summary>按屏幕竖直方向排序后的首个候选，读不到时返回 null。</summary>
    public CandidateItem? FirstCandidate => Items.Count > 0 ? Items[0] : null;

    /// <summary>候选窗底边坐标，浮窗据此贴靠。</summary>
    public int Bottom => Top + Height;
}

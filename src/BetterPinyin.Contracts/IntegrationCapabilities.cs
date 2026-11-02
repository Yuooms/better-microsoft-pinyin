namespace BetterPinyin.Contracts;

/// <summary>
/// 描述一种输入法接入方式「能做什么」。
/// <para>
/// 业务层（CloudAssistController）只按能力分支，绝不按实现名（如 "native-coc"）分支。
/// 这样将来接入原生注入实现时，只要把 <see cref="CanInjectNativeCandidate"/> 改成 true，
/// 上层就会自动从「浮窗 + 专属键」切换到「写入输入法自身候选槽位」，无需改动任何调用方代码。
/// </para>
/// </summary>
/// <param name="CanReadComposition">能否读到用户当前正在输入的组字串。</param>
/// <param name="CanReadLocalCandidates">能否读到输入法自身的候选列表。</param>
/// <param name="CanInjectNativeCandidate">能否把词写进输入法自身的候选列表（仅注入路线为 true）。</param>
/// <param name="CanCommitText">能否主动取消组字并直接上屏一段文本。</param>
public sealed record IntegrationCapabilities(
    bool CanReadComposition,
    bool CanReadLocalCandidates,
    bool CanInjectNativeCandidate,
    bool CanCommitText)
{
    /// <summary>完全不具备任何能力，用于未就绪或不受支持的环境。</summary>
    public static IntegrationCapabilities None { get; } = new(false, false, false, false);
}

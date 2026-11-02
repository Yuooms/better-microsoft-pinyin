namespace BetterPinyin.Contracts;

/// <summary>
/// 输入法接入方式。两条路线（外挂浮窗 / 原生注入）各自实现本接口，
/// 并通过 <see cref="IntegrationCapabilities"/> 声明自己能做什么、不能做什么。
/// </summary>
/// <remarks>
/// 设计约束：
/// <list type="number">
/// <item>实现方不得假设调用发生在 UI 线程；调用方也不得在键盘钩子回调里直接调用本接口。</item>
/// <item>不支持的操作返回 <see cref="OperationStatus.NotSupported"/>，不要抛异常，也不要用假成功糊弄调用方。</item>
/// <item>所有事件可能来自后台线程，订阅方自行切回 UI 线程。</item>
/// </list>
/// </remarks>
public interface IInputIntegration
{
    /// <summary>接入方式标识，例如 <c>uia-overlay</c>、<c>native-coc</c>。</summary>
    string Id { get; }

    /// <summary>当前接入方式的能力声明。</summary>
    IntegrationCapabilities Capabilities { get; }

    /// <summary>启动接入层。返回 false 表示当前环境不可用，调用方应降级或提示用户。</summary>
    Task<bool> TryStartAsync(CancellationToken cancellationToken);

    /// <summary>停止接入层并释放所有系统资源（钩子、UIA 订阅等）。</summary>
    Task StopAsync();

    /// <summary>组字串发生变化。</summary>
    event EventHandler<CompositionChangedEventArgs>? CompositionChanged;

    /// <summary>组字结束。</summary>
    event EventHandler<CompositionEndedEventArgs>? CompositionEnded;

    /// <summary>输入法本地候选发生变化。</summary>
    event EventHandler<LocalCandidatesChangedEventArgs>? LocalCandidatesChanged;

    /// <summary>
    /// 把云词交给输入法自身的候选槽位。
    /// 外挂路线固定返回 <see cref="OperationStatus.NotSupported"/>。
    /// </summary>
    Task<InjectResult> TryInjectNativeAsync(SuggestionPayload suggestion, CancellationToken cancellationToken);

    /// <summary>
    /// 取消当前组字并上屏一段文本。
    /// 注入路线固定返回 <see cref="OperationStatus.NotSupported"/>（上屏由输入法原生流程完成）。
    /// </summary>
    Task<CommitResult> TryCommitAsync(string text, CancellationToken cancellationToken);

    /// <summary>读取当前候选窗快照；不支持读取时返回 <see cref="CandidateWindowSnapshot.Empty"/>。</summary>
    Task<CandidateWindowSnapshot> GetLocalCandidatesAsync(CancellationToken cancellationToken);
}

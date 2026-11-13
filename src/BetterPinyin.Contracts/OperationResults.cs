namespace BetterPinyin.Contracts;

/// <summary>操作结果状态。<see cref="NotSupported"/> 用于「这条路线本来就不负责这件事」的情形。</summary>
public enum OperationStatus
{
    /// <summary>成功。</summary>
    Success = 0,

    /// <summary>当前接入方式不支持该操作，调用方应改用另一条路径。</summary>
    NotSupported = 1,

    /// <summary>前置条件不满足（例如当前不在组字状态）。</summary>
    NotReady = 2,

    /// <summary>执行过程中失败（例如目标窗口焦点已变化）。</summary>
    Failed = 3,
}

/// <summary>注入原生候选的结果。</summary>
/// <param name="Status">执行状态。</param>
/// <param name="Message">失败原因，便于写入日志与诊断。</param>
public readonly record struct InjectResult(OperationStatus Status, string? Message = null)
{
    /// <summary>成功。</summary>
    public static InjectResult Success { get; } = new(OperationStatus.Success);

    /// <summary>本接入方式不支持注入。</summary>
    public static InjectResult NotSupported { get; } = new(OperationStatus.NotSupported, "当前接入方式不支持注入原生候选");
}

/// <summary>上屏结果。</summary>
/// <param name="Status">执行状态。</param>
/// <param name="Message">失败原因。</param>
public readonly record struct CommitResult(OperationStatus Status, string? Message = null)
{
    /// <summary>成功。</summary>
    public static CommitResult Success { get; } = new(OperationStatus.Success);

    /// <summary>本接入方式不支持主动上屏。</summary>
    public static CommitResult NotSupported { get; } = new(OperationStatus.NotSupported, "当前接入方式不支持主动上屏");
}

/// <summary>组字串发生变化。</summary>
public sealed class CompositionChangedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    public CompositionChangedEventArgs(string rawInput, long version)
    {
        RawInput = rawInput;
        Version = version;
    }

    /// <summary>当前原始输入串。</summary>
    public string RawInput { get; }

    /// <summary>代次号。</summary>
    public long Version { get; }
}

/// <summary>组字结束（上屏、取消或失焦）。</summary>
public sealed class CompositionEndedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    public CompositionEndedEventArgs(
        long version,
        CompositionEndReason reason,
        string rawInput = "",
        int length = 0)
    {
        Version = version;
        Reason = reason;
        RawInput = rawInput;
        Length = length;
    }

    /// <summary>代次号。</summary>
    public long Version { get; }

    /// <summary>结束原因。</summary>
    public CompositionEndReason Reason { get; }

    /// <summary>
    /// 结束前那一瞬间的拼音串。
    /// </summary>
    /// <remarks>
    /// 状态机会在抛事件之前清零缓冲，所以这个值只能在 <c>End()</c> 里快照，
    /// 事后读 <c>CompositionTracker.RawInput</c> 永远是空串。
    /// 退格退空时这里也是空串——那一次输入确实什么都没留下。
    /// </remarks>
    public string RawInput { get; }

    /// <summary>结束前那一瞬间的拼音串长度。</summary>
    public int Length { get; }
}

/// <summary>组字结束原因。</summary>
public enum CompositionEndReason
{
    /// <summary>用户选定候选并上屏。</summary>
    Committed = 0,

    /// <summary>用户取消（Esc）。</summary>
    Cancelled = 1,

    /// <summary>焦点窗口变化。</summary>
    FocusLost = 2,

    /// <summary>候选窗消失，具体原因未知。</summary>
    Unknown = 3,
}

/// <summary>输入法本地候选发生变化。</summary>
public sealed class LocalCandidatesChangedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    public LocalCandidatesChangedEventArgs(CandidateWindowSnapshot snapshot, long version)
    {
        Snapshot = snapshot;
        Version = version;
    }

    /// <summary>候选窗快照。</summary>
    public CandidateWindowSnapshot Snapshot { get; }

    /// <summary>代次号。</summary>
    public long Version { get; }
}

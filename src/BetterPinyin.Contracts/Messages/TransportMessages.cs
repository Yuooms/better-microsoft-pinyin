namespace BetterPinyin.Contracts.Messages;

/// <summary>
/// 所有跨进程消息的公共契约。
/// </summary>
/// <remarks>
/// 协议版本号用于将来「注入 DLL（旧）」与「核心服务（新）」混跑时的兼容判断：
/// 收到不认识的版本必须忽略并记录，不得抛异常打断输入。
/// </remarks>
public interface ITransportMessage
{
    /// <summary>发送方使用的协议版本。</summary>
    int ProtocolVersion { get; }
}

/// <summary>当前协议版本。修改任何消息结构时递增。</summary>
public static class TransportProtocol
{
    /// <summary>协议版本号。</summary>
    public const int CurrentVersion = 1;
}

/// <summary>组字串变化通知（宿主 → 核心）。</summary>
/// <param name="RawInput">原始输入串。</param>
/// <param name="Version">代次号。</param>
public sealed record CompositionMessage(string RawInput, long Version) : ITransportMessage
{
    /// <inheritdoc />
    public int ProtocolVersion => TransportProtocol.CurrentVersion;
}

/// <summary>请求把云词写入输入法原生候选槽位（核心 → 宿主）。</summary>
/// <param name="Text">候选词文本。</param>
/// <param name="Version">发起该请求时的组字代次号，宿主必须校验是否仍然一致。</param>
public sealed record InjectCandidateRequest(string Text, long Version) : ITransportMessage
{
    /// <inheritdoc />
    public int ProtocolVersion => TransportProtocol.CurrentVersion;
}

/// <summary>请求取消组字并上屏（核心 → 宿主）。</summary>
/// <param name="Text">要上屏的文本。</param>
/// <param name="Version">代次号。</param>
public sealed record CommitRequest(string Text, long Version) : ITransportMessage
{
    /// <inheritdoc />
    public int ProtocolVersion => TransportProtocol.CurrentVersion;
}

using System.Threading.Channels;
using BetterPinyin.Contracts.Messages;

namespace BetterPinyin.Contracts;

/// <summary>
/// 宿主与核心之间的消息通道。
/// </summary>
/// <remarks>
/// V1 单进程运行，使用 <see cref="InProcessTransport"/>；
/// 将来接入原生注入时需要拆成「注入 DLL + 核心服务」两个进程，
/// 只需新增一个基于命名管道/共享内存的实现，消息结构与上层代码都不变。
/// </remarks>
public interface ITransport : IAsyncDisposable
{
    /// <summary>通道标识，便于日志区分。</summary>
    string Name { get; }

    /// <summary>发送一条消息。</summary>
    ValueTask SendAsync(ITransportMessage message, CancellationToken cancellationToken);

    /// <summary>收到一条消息。可能来自后台线程。</summary>
    event EventHandler<ITransportMessage>? Received;
}

/// <summary>
/// 单进程内的消息通道实现，基于无界 Channel，保证发送永不阻塞键盘路径。
/// </summary>
public sealed class InProcessTransport : ITransport
{
    private readonly Channel<ITransportMessage> _channel =
        Channel.CreateUnbounded<ITransportMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    /// <summary>创建通道并启动分发循环。</summary>
    /// <param name="name">通道名称。</param>
    public InProcessTransport(string name = "in-process")
    {
        Name = name;
        _pump = Task.Run(PumpAsync);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public event EventHandler<ITransportMessage>? Received;

    /// <inheritdoc />
    public ValueTask SendAsync(ITransportMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        _channel.Writer.TryWrite(message);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径，忽略。
        }

        _cts.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (message.ProtocolVersion != TransportProtocol.CurrentVersion)
                {
                    // 版本不认识就丢弃，绝不因为一条消息把输入流程打断。
                    continue;
                }

                Received?.Invoke(this, message);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径，忽略。
        }
    }
}

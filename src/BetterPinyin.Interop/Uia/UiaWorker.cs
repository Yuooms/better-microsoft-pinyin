using System.Collections.Concurrent;

namespace BetterPinyin.Interop.Uia;

/// <summary>
/// 把 UI Automation 调用串行化到一条常驻 STA 线程上执行。
/// </summary>
/// <remarks>
/// 为什么必须这样做：
/// <list type="bullet">
/// <item>UIA 跨进程调用可能因为目标进程无响应而长时间阻塞，绝不能发生在 UI 线程或键盘钩子回调里。</item>
/// <item>COM 的 STA 要求同一条线程上创建与使用对象，否则会抛 <c>RPC_E_WRONG_THREAD</c>。</item>
/// <item>带超时的调用方可以在等待超时后直接放弃结果，STA 线程本身继续跑，不会拖垮整个程序。</item>
/// </list>
/// </remarks>
public sealed class UiaWorker : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private volatile bool _disposed;

    /// <summary>创建并启动后台 STA 线程。</summary>
    /// <param name="name">线程名，便于调试。</param>
    public UiaWorker(string name = "BetterPinyin.Uia")
    {
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// 在 STA 线程上执行一个操作并等待结果。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="operation">要执行的操作。</param>
    /// <param name="timeoutMilliseconds">等待上限；超时后返回 <paramref name="fallback"/>，操作本身会被放弃。</param>
    /// <param name="fallback">超时或异常时的返回值。</param>
    /// <returns>操作结果或兜底值。</returns>
    public T Invoke<T>(Func<T> operation, int timeoutMilliseconds, T fallback)
    {
        if (_disposed)
        {
            return fallback;
        }

        var item = new WorkItem(() => operation(), timeoutMilliseconds);
        try
        {
            _queue.Add(item);
        }
        catch (InvalidOperationException)
        {
            // 队列已关闭（正在 Dispose），直接返回兜底值。
            return fallback;
        }

        return item.Wait(timeoutMilliseconds, fallback);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(500);
        _queue.Dispose();
    }

    private void Pump()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (item.IsAbandoned)
            {
                continue;
            }

            try
            {
                item.Execute();
            }
            catch (Exception ex)
            {
                item.Fail(ex);
            }
        }
    }

    private sealed class WorkItem
    {
        private readonly Delegate _operation;
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile bool _abandoned;

        public WorkItem(Delegate operation, int timeoutMilliseconds)
        {
            _operation = operation;

            // 超时兜底：即使 STA 线程被卡死，调用方也能按时返回。
            _ = Task.Delay(timeoutMilliseconds).ContinueWith(
                _ => _abandoned = true,
                TaskScheduler.Default);
        }

        public bool IsAbandoned => _abandoned;

        public void Execute()
        {
            object? result;
            try
            {
                result = _operation.DynamicInvoke();
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                return;
            }

            _completion.TrySetResult(result);
        }

        public void Fail(Exception ex) => _completion.TrySetException(ex);

        public T Wait<T>(int timeoutMilliseconds, T fallback)
        {
            if (!_completion.Task.Wait(timeoutMilliseconds))
            {
                _abandoned = true;
                return fallback;
            }

            if (_completion.Task.IsFaulted)
            {
                return fallback;
            }

            return _completion.Task.Result is T typed ? typed : fallback;
        }
    }
}

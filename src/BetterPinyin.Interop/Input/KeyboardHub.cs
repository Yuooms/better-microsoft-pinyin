namespace BetterPinyin.Interop.Input;

/// <summary>
/// 全局键盘钩子与拼音重建状态机的唯一持有者。
/// </summary>
/// <remarks>
/// <para>
/// 提出这个类的理由：钩子是进程级独占资源，同一个进程里装两个 <c>WH_KEYBOARD_LL</c>
/// 会按安装逆序串联，而前一个还可能吞掉按键（返回 1 不调 <c>CallNextHookEx</c>），
/// 后一个就再也看不到那些键。所以只能有一个持有者，其余功能订阅它。
/// </para>
/// <para>
/// 分发顺序是固定的，且不对外调换：
/// 过滤器（云联想，可以吞键）→ 组字状态机（没被吞才喂）→ 观察者（统计，只读）。
/// 观察者看到的是 <see cref="KeyboardHookEventArgs.Handled"/> 的最终值，
/// 因此不会把被云联想吞掉的采纳键再算成一次普通按键。
/// </para>
/// <para>
/// 线程：回调运行在安装线程（UI 线程）上，所以 <see cref="Dispatch"/> 里只做纯内存操作。
/// 任何订阅方都不允许在这里做 I/O 或跨进程调用，否则整机输入都会卡。
/// </para>
/// </remarks>
public sealed class KeyboardHub : IDisposable
{
    private readonly object _gate = new();
    private readonly LowLevelKeyboardHook _hook = new();
    private readonly HashSet<string> _owners = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>创建中枢（尚未安装钩子，等第一个 <see cref="Acquire"/>）。</summary>
    public KeyboardHub()
    {
        _hook.KeyEvent += OnHookKeyEvent;
    }

    /// <summary>全进程唯一的拼音重建状态机。</summary>
    public CompositionTracker Tracker { get; } = new();

    /// <summary>
    /// 过滤器：可以设置 <see cref="KeyboardHookEventArgs.Handled"/> 吞掉按键。
    /// </summary>
    /// <remarks>目前只有云联想使用；它需要抢在状态机之前决定这个键归谁。</remarks>
    public event EventHandler<KeyboardHookEventArgs>? Filter;

    /// <summary>观察者：只读，拿到的 <c>Handled</c> 已是最终值。</summary>
    public event EventHandler<KeyboardHookEventArgs>? Observed;

    /// <summary>钩子是否已安装。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _hook.IsRunning;
            }
        }
    }

    /// <summary>当前持有者数量，诊断用。</summary>
    public int OwnerCount
    {
        get
        {
            lock (_gate)
            {
                return _owners.Count;
            }
        }
    }

    /// <summary>最近一次失败原因；成功时会清空。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 申请使用钩子。第一个持有者才真正安装，其余只是计数。
    /// </summary>
    /// <param name="owner">持有者标识，同一个标识重复调用是幂等的。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>可用返回 true。</returns>
    public bool Acquire(string owner, out string? error)
    {
        error = null;

        lock (_gate)
        {
            if (_disposed)
            {
                error = "键盘中枢已释放";
                return false;
            }

            if (_owners.Contains(owner))
            {
                return true;
            }

            if (_owners.Count == 0)
            {
                try
                {
                    _hook.Start();
                }
                catch (Exception ex)
                {
                    // 安装失败要说清楚原因（通常是安全软件拦截或线程没有消息泵）。
                    error = ex.Message;
                    LastError = error;
                    return false;
                }
            }

            _owners.Add(owner);
            LastError = null;
            return true;
        }
    }

    /// <summary>释放使用权。最后一个持有者离开时才真正卸载钩子。</summary>
    /// <param name="owner">持有者标识。</param>
    public void Release(string owner)
    {
        lock (_gate)
        {
            if (!_owners.Remove(owner) || _owners.Count > 0)
            {
                return;
            }

            // 用 Stop 而不是 Dispose：钩子实例还要留给下一次开启复用，
            // 否则「关掉再打开」就得重启程序。
            _hook.Stop();
            Tracker.Reset();
        }
    }

    /// <summary>
    /// 把一次按键走完整条分发链。
    /// </summary>
    /// <param name="args">按键事件参数。</param>
    /// <remarks>
    /// 公开出来是为了让测试能在不装钩子的情况下验证分发顺序与吞键语义。
    /// </remarks>
    public void Dispatch(KeyboardHookEventArgs args)
    {
        if (args.IsOwnInjection)
        {
            return;
        }

        var filter = Filter;
        if (filter is not null)
        {
            try
            {
                filter(this, args);
            }
            catch (Exception ex)
            {
                // 订阅方异常绝不能冒泡回系统钩子链，否则整个键盘都会失灵。
                LastError = $"{nameof(Filter)}: {ex.GetType().Name} {ex.Message}";
            }
        }

        if (!args.Handled)
        {
            // 第三方注入（自动化工具）照旧当作真实输入，与本类引入前的行为一致；
            // 本程序自己的注入已在上面被挡掉了。
            Tracker.Feed(args.VirtualKey, args.IsKeyDown, args.Modifiers, isInjected: false);
        }

        var observed = Observed;
        if (observed is not null)
        {
            try
            {
                observed(this, args);
            }
            catch (Exception ex)
            {
                LastError = $"{nameof(Observed)}: {ex.GetType().Name} {ex.Message}";
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owners.Clear();
            _hook.KeyEvent -= OnHookKeyEvent;
            _hook.Dispose();
        }
    }

    private void OnHookKeyEvent(object? sender, KeyboardHookEventArgs e)
    {
        // 钩子回调里再包一层：Dispatch 自己已经吞掉订阅方异常，
        // 这里兜住的是分发链之外的意外（例如状态机抛错）。
        try
        {
            Dispatch(e);
        }
        catch (Exception ex)
        {
            LastError = $"{nameof(Dispatch)}: {ex.GetType().Name} {ex.Message}";
        }
    }
}

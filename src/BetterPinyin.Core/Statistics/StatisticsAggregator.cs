using System.Globalization;

namespace BetterPinyin.Core.Statistics;

/// <summary>
/// 把输入事件累加成按天、按小时、按程序的统计。
/// </summary>
/// <remarks>
/// <para>
/// 纯内存、纯整数运算，不碰文件也不碰 UI，因此可以脱离桌面单独测试。
/// 跨天规则：每个计数记在**它发生那一刻**的本地日期上。
/// 23:59 开始、00:01 提交的那一次组字，组字次数落在前一天、提交次数落在后一天，
/// 所以某一天的 <c>Committed + Cancelled</c> 允许比 <c>Compositions</c> 少一。
/// 选择这条规则是为了不在钩子线程里做跨天缓冲——崩溃时最多丢最后一条，不会整体错位。
/// </para>
/// <para>
/// 线程：累加来自键盘钩子线程，读取来自 UI 与落盘线程，所有入口走同一把锁。
/// </para>
/// </remarks>
public sealed class StatisticsAggregator
{
    /// <summary>长尾程序归并后的名字。</summary>
    public const string OtherProcess = "其它";

    /// <summary>拿不到前台进程时的占位名。</summary>
    public const string UnknownProcess = "未知";

    private readonly object _gate = new();
    private readonly Dictionary<string, DailyStatistics> _days = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;
    private readonly int _maxAppsPerDay;

    private bool _composing;
    private string? _currentApp;
    private int _lastLength;

    /// <summary>创建聚合器。</summary>
    /// <param name="initial">已有的历史数据，可为空。</param>
    /// <param name="clock">取当前时间的方法；测试用它模拟跨天。</param>
    /// <param name="maxAppsPerDay">每天保留的程序条数上限，超出的并进「其它」。</param>
    public StatisticsAggregator(
        IEnumerable<DailyStatistics>? initial = null,
        Func<DateTime>? clock = null,
        int maxAppsPerDay = 12)
    {
        _clock = clock ?? (() => DateTime.Now);
        _maxAppsPerDay = Math.Max(2, maxAppsPerDay);

        if (initial is not null)
        {
            Replace(initial);
        }
    }

    /// <summary>当前正在组字的程序名，诊断用。</summary>
    public string? CurrentProcess
    {
        get
        {
            lock (_gate)
            {
                return _currentApp;
            }
        }
    }

    /// <summary>用给定数据整体替换（同一天的多条会合并求和）。</summary>
    /// <param name="days">历史数据。</param>
    public void Replace(IEnumerable<DailyStatistics> days)
    {
        lock (_gate)
        {
            _days.Clear();
            foreach (var day in days)
            {
                if (string.IsNullOrWhiteSpace(day.Date))
                {
                    continue;
                }

                day.Normalize();
                if (_days.TryGetValue(day.Date, out var existing))
                {
                    Merge(existing, day);
                }
                else
                {
                    _days[day.Date] = day;
                }
            }

            _composing = false;
            _currentApp = null;
            _lastLength = 0;
        }
    }

    /// <summary>清空全部数据（含今天已经累计的部分）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _days.Clear();
            _composing = false;
            _currentApp = null;
            _lastLength = 0;
        }
    }

    /// <summary>
    /// 取一份按日期升序的深拷贝。
    /// </summary>
    /// <returns>与内部状态完全脱钩的快照。</returns>
    /// <remarks>
    /// 必须深拷贝：落盘在线程池、累加在钩子线程，共享可变对象会出现
    /// 「序列化到一半数字变了」的撕裂读。
    /// </remarks>
    public IReadOnlyList<DailyStatistics> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<DailyStatistics>(_days.Count);
            foreach (var day in _days.Values)
            {
                var copy = Clone(day);
                copy.TrimApps(_maxAppsPerDay);
                list.Add(copy);
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
            return list;
        }
    }

    /// <summary>组字期间按了一个键。</summary>
    public void AddKeystroke()
    {
        lock (_gate)
        {
            if (!_composing)
            {
                return;
            }

            TodayLocked().Totals.Keystrokes++;
        }
    }

    /// <summary>
    /// 一段新的组字开始（拼音串长度从 0 变成 1）。
    /// </summary>
    /// <param name="process">输入发生的程序名，可为空。</param>
    /// <remarks>
    /// 上一段如果没收到结束事件（典型：被云词采纳打断，采纳流程走的是 Reset 不抛事件），
    /// 这里直接开新的一段，不补记提交或取消——那一次的去向由采纳事件负责。
    /// </remarks>
    public void BeginComposition(string? process)
    {
        lock (_gate)
        {
            var day = TodayLocked();
            var hour = _clock().Hour;

            _composing = true;
            _currentApp = string.IsNullOrWhiteSpace(process) ? UnknownProcess : process;
            _lastLength = 0;

            day.Totals.Compositions++;
            day.AddHour(hour, 1, 0);

            var app = FindAppLocked(day, _currentApp);
            app.Compositions++;
        }
    }

    /// <summary>拼音串又长了几个字母。</summary>
    /// <param name="count">新增的字母数。</param>
    public void AddLetters(int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_composing)
            {
                return;
            }

            var day = TodayLocked();
            var hour = _clock().Hour;

            day.Totals.Letters += count;
            day.AddHour(hour, 0, count);

            if (_currentApp is not null)
            {
                FindAppLocked(day, _currentApp).Letters += count;
            }

            _lastLength += count;
        }
    }

    /// <summary>组字结束。</summary>
    /// <param name="committed">true 表示定稿上屏，false 表示取消。</param>
    public void EndComposition(bool committed)
    {
        lock (_gate)
        {
            if (!_composing)
            {
                return;
            }

            var day = TodayLocked();
            if (committed)
            {
                day.Totals.Committed++;
            }
            else
            {
                day.Totals.Cancelled++;
            }

            _composing = false;
            _lastLength = 0;
        }
    }

    /// <summary>云候选上屏成功。</summary>
    /// <param name="text">上屏的文本；只用于取长度，不会被保存。</param>
    public void AddCloudAccept(string? text)
    {
        var characters = text?.Length ?? 0;

        lock (_gate)
        {
            var day = TodayLocked();
            day.Totals.CloudAccepts++;
            day.Totals.CloudCharacters += characters;

            if (_currentApp is not null)
            {
                FindAppLocked(day, _currentApp).CloudAccepts++;
            }

            // 采纳走的是 Esc + 注入，状态机是被 Reset 掉的，不会有结束事件。
            // 统计这边把这一段视为已收尾，否则后面不在组字的按键会被算进 Keystrokes。
            _composing = false;
            _lastLength = 0;
        }
    }

    /// <summary>当前这一段组字已经累积的长度，供调用方判断是否需要补记字母。</summary>
    public int CurrentLength
    {
        get
        {
            lock (_gate)
            {
                return _lastLength;
            }
        }
    }

    private DailyStatistics TodayLocked()
    {
        var now = _clock();
        var key = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!_days.TryGetValue(key, out var day))
        {
            day = new DailyStatistics { Date = key };
            _days[key] = day;
        }

        return day;
    }

    private static AppUsage FindAppLocked(DailyStatistics day, string process)
    {
        foreach (var item in day.Apps)
        {
            if (string.Equals(item.Process, process, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        var created = new AppUsage { Process = process };
        day.Apps.Add(created);
        return created;
    }

    private static void Merge(DailyStatistics target, DailyStatistics source)
    {
        target.Totals.Keystrokes += source.Totals.Keystrokes;
        target.Totals.Letters += source.Totals.Letters;
        target.Totals.Compositions += source.Totals.Compositions;
        target.Totals.Committed += source.Totals.Committed;
        target.Totals.Cancelled += source.Totals.Cancelled;
        target.Totals.CloudAccepts += source.Totals.CloudAccepts;
        target.Totals.CloudCharacters += source.Totals.CloudCharacters;

        for (var hour = 0; hour < DailyStatistics.HourCount; hour++)
        {
            var (compositions, letters) = source.GetHour(hour);
            target.AddHour(hour, compositions, letters);
        }

        foreach (var app in source.Apps)
        {
            var existing = FindAppLocked(target, app.Process);
            existing.Compositions += app.Compositions;
            existing.Letters += app.Letters;
            existing.CloudAccepts += app.CloudAccepts;
        }
    }

    private static DailyStatistics Clone(DailyStatistics source)
    {
        var copy = new DailyStatistics
        {
            Date = source.Date,
            Totals = new StatisticsTotals
            {
                Keystrokes = source.Totals.Keystrokes,
                Letters = source.Totals.Letters,
                Compositions = source.Totals.Compositions,
                Committed = source.Totals.Committed,
                Cancelled = source.Totals.Cancelled,
                CloudAccepts = source.Totals.CloudAccepts,
                CloudCharacters = source.Totals.CloudCharacters,
            },
            HourlyCompositions = (int[])source.HourlyCompositions.Clone(),
            HourlyLetters = (int[])source.HourlyLetters.Clone(),
            Apps = new List<AppUsage>(source.Apps.Count),
        };

        foreach (var app in source.Apps)
        {
            copy.Apps.Add(new AppUsage
            {
                Process = app.Process,
                Compositions = app.Compositions,
                Letters = app.Letters,
                CloudAccepts = app.CloudAccepts,
            });
        }

        return copy;
    }
}

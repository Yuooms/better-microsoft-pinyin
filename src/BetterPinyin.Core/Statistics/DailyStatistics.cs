namespace BetterPinyin.Core.Statistics;

/// <summary>一天的总量。</summary>
/// <remarks>
/// 全部是整数，不含任何输入内容。这个类是「能落盘的最敏感的东西」的边界：
/// 想加字段之前先问一遍，它会不会让文件泄露用户打了什么。
/// </remarks>
public sealed class StatisticsTotals
{
    /// <summary>组字期间的按键数。</summary>
    public int Keystrokes { get; set; }

    /// <summary>落进组字串的拼音字母数。</summary>
    public int Letters { get; set; }

    /// <summary>组字次数：一段连续拼音算一次。</summary>
    public int Compositions { get; set; }

    /// <summary>以提交结束的组字次数。</summary>
    public int Committed { get; set; }

    /// <summary>以取消结束的组字次数。</summary>
    public int Cancelled { get; set; }

    /// <summary>云候选采纳次数。</summary>
    public int CloudAccepts { get; set; }

    /// <summary>云候选上屏的字数（只有这一项是真实的汉字字数）。</summary>
    public int CloudCharacters { get; set; }
}

/// <summary>某个程序在一天里的输入量。</summary>
/// <remarks>
/// 只记进程名，不记窗口标题、不记文件路径——标题里常常含有文档名。
/// </remarks>
public sealed class AppUsage
{
    /// <summary>进程名，例如 <c>Code</c>；无法识别时是 <see cref="UnknownProcess"/>。</summary>
    public string Process { get; set; } = string.Empty;

    /// <summary>组字次数。</summary>
    public int Compositions { get; set; }

    /// <summary>拼音字母数。</summary>
    public int Letters { get; set; }

    /// <summary>云候选采纳次数。</summary>
    public int CloudAccepts { get; set; }
}

/// <summary>一天的统计。</summary>
/// <remarks>
/// <para>
/// 按小时分两份数组（组字次数、字母数），而不是 24 个对象：这两个数只用来画时段分布，
/// 用对象序列化会让每天多出几百字节，一年下来是几百 KB 的纯括号。
/// </para>
/// <para>
/// 数组长度固定 <see cref="HourCount"/>，缺省补 0；读取旧文件时如果长度不对，
/// <see cref="Normalize"/> 会补齐或截断，调用方不必自己判断。
/// </para>
/// </remarks>
public sealed class DailyStatistics
{
    /// <summary>一天的小时数。</summary>
    public const int HourCount = 24;

    /// <summary>本地日期，格式 <c>yyyy-MM-dd</c>。</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>当天总量。</summary>
    public StatisticsTotals Totals { get; set; } = new();

    /// <summary>每小时发生的组字次数，长度 24。</summary>
    public int[] HourlyCompositions { get; set; } = new int[HourCount];

    /// <summary>每小时输入的拼音字母数，长度 24。</summary>
    public int[] HourlyLetters { get; set; } = new int[HourCount];

    /// <summary>按程序拆分的输入量。</summary>
    public List<AppUsage> Apps { get; set; } = new();

    /// <summary>把两个小时数组修正成固定长度。</summary>
    public void Normalize()
    {
        Totals ??= new StatisticsTotals();
        Apps ??= new List<AppUsage>();
        HourlyCompositions = NormalizeHours(HourlyCompositions);
        HourlyLetters = NormalizeHours(HourlyLetters);
    }

    /// <summary>给某个小时累加。</summary>
    /// <param name="hour">0–23。</param>
    /// <param name="compositions">组字次数增量。</param>
    /// <param name="letters">字母数增量。</param>
    public void AddHour(int hour, int compositions, int letters)
    {
        if (hour < 0 || hour >= HourCount)
        {
            return;
        }

        // 属性不能按 ref 传给方法，只能先取出来补齐再放回去。
        HourlyCompositions = NormalizeHours(HourlyCompositions);
        HourlyLetters = NormalizeHours(HourlyLetters);
        HourlyCompositions[hour] += compositions;
        HourlyLetters[hour] += letters;
    }

    /// <summary>
    /// 把程序列表裁剪到上限，超出的并进「其它」。
    /// </summary>
    /// <param name="max">保留的条数上限（含「其它」）。</param>
    /// <remarks>
    /// 一天的窗口数可能几十个，全记下来会让文件膨胀，而长尾对用户也没有信息量。
    /// </remarks>
    public void TrimApps(int max)
    {
        if (max <= 0 || Apps.Count <= max)
        {
            return;
        }

        Apps.Sort((a, b) => b.Compositions.CompareTo(a.Compositions));

        var kept = Apps.Take(Math.Max(1, max - 1)).ToList();
        var rest = Apps.Skip(Math.Max(1, max - 1)).ToList();

        var other = kept.FirstOrDefault(a => a.Process == StatisticsAggregator.OtherProcess);
        if (other is null)
        {
            other = new AppUsage { Process = StatisticsAggregator.OtherProcess };
            kept.Add(other);
        }

        foreach (var item in rest)
        {
            other.Compositions += item.Compositions;
            other.Letters += item.Letters;
            other.CloudAccepts += item.CloudAccepts;
        }

        Apps = kept;
    }

    /// <summary>取某个小时的数值。</summary>
    /// <param name="hour">0–23。</param>
    /// <returns>组字次数与字母数。</returns>
    public (int Compositions, int Letters) GetHour(int hour)
    {
        if (hour < 0 || hour >= HourCount ||
            HourlyCompositions.Length <= hour ||
            HourlyLetters.Length <= hour)
        {
            return (0, 0);
        }

        return (HourlyCompositions[hour], HourlyLetters[hour]);
    }

    private static int[] NormalizeHours(int[]? hours)
    {
        if (hours is null)
        {
            return new int[HourCount];
        }

        if (hours.Length == HourCount)
        {
            return hours;
        }

        var fixedLength = new int[HourCount];
        Array.Copy(hours, fixedLength, Math.Min(hours.Length, HourCount));
        return fixedLength;
    }
}

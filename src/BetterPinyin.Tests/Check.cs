using System.Text;

namespace BetterPinyin.Tests;

/// <summary>
/// 极简断言器。
/// </summary>
/// <remarks>
/// 不用 xUnit：本项目要求零第三方依赖，以便在受限网络下也能直接构建与验证。
/// 需要的能力只有"记录失败数 + 打印"，一个静态类足够。
/// </remarks>
internal static class Check
{
    private static readonly List<string> Failures = new();

    /// <summary>已执行的断言数。</summary>
    public static int Total { get; private set; }

    /// <summary>失败列表。</summary>
    public static IReadOnlyList<string> ErrorMessages => Failures;

    /// <summary>断言相等。</summary>
    public static void Equal<T>(T expected, T actual, string what)
    {
        Total++;
        if (!Equals(expected, actual))
        {
            Failures.Add($"{what}：期望 [{expected}]，实际 [{actual}]");
        }
    }

    /// <summary>断言为真。</summary>
    public static void True(bool condition, string what)
    {
        Total++;
        if (!condition)
        {
            Failures.Add($"{what}：期望为真，实际为假");
        }
    }

    /// <summary>断言为假。</summary>
    public static void False(bool condition, string what)
    {
        Total++;
        if (condition)
        {
            Failures.Add($"{what}：期望为假，实际为真");
        }
    }

    /// <summary>断言集合包含某元素。</summary>
    public static void Contains<T>(IEnumerable<T> items, T expected, string what)
    {
        Total++;
        if (!items.Contains(expected))
        {
            Failures.Add($"{what}：未包含 [{expected}]，实际为 [{string.Join(", ", items)}]");
        }
    }

    /// <summary>断言集合不包含某元素。</summary>
    public static void NotContains<T>(IEnumerable<T> items, T unexpected, string what)
    {
        Total++;
        if (items.Contains(unexpected))
        {
            Failures.Add($"{what}：不应包含 [{unexpected}]，实际为 [{string.Join(", ", items)}]");
        }
    }

    /// <summary>打印汇总并返回退出码。</summary>
    public static int Report()
    {
        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine($"✓ 全部通过（{Total} 项断言）");
            return 0;
        }

        Console.WriteLine($"✗ 失败 {Failures.Count} 项 / 共 {Total} 项断言：");
        foreach (var failure in Failures)
        {
            Console.WriteLine($"    - {failure}");
        }

        return 1;
    }
}

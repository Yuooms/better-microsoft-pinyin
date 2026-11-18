using System.Diagnostics;

namespace BetterPinyin.App;

/// <summary>
/// 按启动阶段记录内存占用，供 <c>--mem</c> 诊断使用。
/// </summary>
/// <remarks>
/// <para>
/// 「这个程序为什么占这么多内存」很难从现象回答：任务管理器只给一个总数，
/// 而那里面既有托管堆、也有 WPF 的渲染目标、还有 GPU 驱动的共享页。
/// 光看总数无法判断该优化哪里，所以这里把每个阶段结束时的几项指标直接写进日志，
/// 相邻两行相减就是这个阶段的开销。
/// </para>
/// <para>
/// 只统计进程自身的数据，不需要外部工具；默认关闭，加 <c>--mem</c> 才输出，
/// 避免正常使用的日志被这些行占满。
/// </para>
/// </remarks>
internal static class MemoryReport
{
    private static bool _enabled;

    /// <summary>是否启用（由命令行 <c>--mem</c> 打开）。</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;

            // 打开时先落一条基线：进程起来之前的公共部分（运行时、WPF 自身）算在这里。
            if (value)
            {
                Mark("基线");
            }
        }
    }

    /// <summary>记录一个阶段的内存快照。</summary>
    /// <param name="stage">阶段名，写进日志便于前后相减。</param>
    /// <remarks>
    /// 不在这里强制 GC：<c>GC.Collect</c> 会让测量本身干扰被测对象（本来能复用的对象被回收，
    /// 下一次分配又要重新提交内存），得到的数字反而失真。这里只读当前值。
    /// </remarks>
    public static void Mark(string stage)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();

            var managed = GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d;
            var privateMb = process.PrivateMemorySize64 / 1024d / 1024d;
            var workingSetMb = process.WorkingSet64 / 1024d / 1024d;

            Log.Write(
                $"[内存] {stage}：私有 {privateMb:0.0} MB / 工作集 {workingSetMb:0.0} MB / " +
                $"托管 {managed:0.0} MB / 线程 {process.Threads.Count} / 句柄 {process.HandleCount}");
        }
        catch (Exception ex)
        {
            Log.Write($"[内存] 统计失败：{ex.GetType().Name} {ex.Message}");
        }
    }
}

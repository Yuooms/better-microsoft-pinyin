using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Interop.Ime;

/// <summary>
/// 让微软拼音重新读一次自学习词库（<c>ChsPinyinUDL.dat</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 输入法把整份自学习词库读进内存，之后只往文件里写、不再读：改文件当场不生效，
/// 而且它下次写盘时会用内存里的副本把文件整个覆盖回去，改动等于白改。
/// 实测（Windows 10 19045 / 微软拼音）：把**当前线程**的键盘布局切到非中文再切回中文，
/// 输入法会重新加载一次词库，改动立刻生效，ChsIME.exe 的进程 Id 不变，
/// 因此既不需要管理员权限，也不需要注销。
/// </para>
/// <para>
/// 这一步只影响调用线程自己的输入法状态，不会碰到用户正在使用的其它窗口，
/// 所以必须在设置窗口处于前台、用户刚点完按钮时调用。
/// </para>
/// </remarks>
public static class ImeReloader
{
    /// <summary>简体中文语言 Id。</summary>
    private const ushort SimplifiedChineseLanguageId = 0x0804;

    /// <summary>两次切换之间的等待时间：太短输入法来不及处理 WM_INPUTLANGCHANGE。</summary>
    private const int SwitchDelayMilliseconds = 220;

    /// <summary>
    /// 触发一次重新加载。
    /// </summary>
    /// <param name="window">
    /// 要借用其输入法上下文的窗口（设置窗口自己）。传 <see cref="IntPtr.Zero"/> 时退化为
    /// 直接切换调用线程的布局，只有在该线程确实持有前台焦点时才有效。
    /// </param>
    /// <param name="message">给用户看的说明；成功时是「已重新加载」，失败时是原因。</param>
    /// <returns>成功发出切换返回 true（不代表输入法一定读到了新数据）。</returns>
    public static bool TryReload(IntPtr window, out string message)
    {
        var layouts = GetLayouts();
        if (layouts.Count == 0)
        {
            message = "读不到本机的键盘布局，无法让输入法重新加载。";
            return false;
        }

        var original = window == IntPtr.Zero
            ? NativeMethods.GetKeyboardLayout(0)
            : NativeMethods.GetKeyboardLayout(NativeMethods.GetWindowThreadProcessId(window, out _));
        if (original == IntPtr.Zero)
        {
            message = "读不到当前键盘布局，无法让输入法重新加载。";
            return false;
        }

        // 只有窗口所在线程真的持有焦点时，切布局才会走到输入法；否则只是白切一次。
        if (window != IntPtr.Zero)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero ||
                NativeMethods.GetWindowThreadProcessId(foreground, out _) !=
                NativeMethods.GetWindowThreadProcessId(window, out _))
            {
                message = "设置窗口不在前台，输入法不会重读词库；请让设置窗口保持在前台再试。";
                return false;
            }
        }

        var chinese = FindChinese(layouts);
        if (chinese == IntPtr.Zero)
        {
            message = "本机没有安装中文键盘布局，无法让输入法重新加载。";
            return false;
        }

        var other = FindNonChinese(layouts);
        if (other == IntPtr.Zero)
        {
            message = "本机只装了微软拼音一种键盘布局，缺少可临时切换的布局；"
                    + "请在「语言」里加一个英文键盘后重试，或者注销后重新登录。";
            return false;
        }

        // 一定要经过「中文 → 非中文 → 中文」这一轮，输入法才会重新读文件；
        // 本来就在英文布局时多绕一步中文，保证最后一个动作是「切回中文」。
        var path = IsChinese(original)
            ? new[] { other, chinese }
            : new[] { chinese, other, chinese };

        foreach (var layout in path)
        {
            SwitchTo(window, layout);
            Thread.Sleep(SwitchDelayMilliseconds);
        }

        // 切完把用户原来的布局还回去，避免设置窗口里突然变成英文输入。
        if (original != path[^1])
        {
            SwitchTo(window, original);
            Thread.Sleep(SwitchDelayMilliseconds);
        }

        message = "已让输入法重新加载词库。";
        return true;
    }

    private static void SwitchTo(IntPtr window, IntPtr layout)
    {
        if (window != IntPtr.Zero)
        {
            NativeMethods.PostMessage(window, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layout);
            return;
        }

        NativeMethods.ActivateKeyboardLayout(layout, 0);
    }

    private static List<IntPtr> GetLayouts()
    {
        var count = NativeMethods.GetKeyboardLayoutList(0, null);
        if (count <= 0)
        {
            return new List<IntPtr>(0);
        }

        var buffer = new IntPtr[count];
        var written = NativeMethods.GetKeyboardLayoutList(buffer.Length, buffer);
        var layouts = new List<IntPtr>(written);
        for (var i = 0; i < written; i++)
        {
            if (buffer[i] != IntPtr.Zero)
            {
                layouts.Add(buffer[i]);
            }
        }

        return layouts;
    }

    private static bool IsChinese(IntPtr layout) =>
        (ushort)(layout.ToInt64() & 0xFFFF) == SimplifiedChineseLanguageId;

    private static IntPtr FindChinese(List<IntPtr> layouts) =>
        layouts.FirstOrDefault(IsChinese);

    private static IntPtr FindNonChinese(List<IntPtr> layouts) =>
        layouts.FirstOrDefault(layout => !IsChinese(layout));
}

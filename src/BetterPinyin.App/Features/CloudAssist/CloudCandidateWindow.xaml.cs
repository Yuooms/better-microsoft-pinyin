using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BetterPinyin.App.Shell;
using BetterPinyin.Contracts;
using BetterPinyin.Core.Settings;
using BetterPinyin.Interop.Ime;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>浮窗里的一行。</summary>
public sealed class SuggestionRow
{
    /// <summary>候选词。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>采纳键文字。</summary>
    public string KeyLabel { get; init; } = string.Empty;

    /// <summary>字号。</summary>
    public double TextSize { get; init; } = 13;

    /// <summary>字重。</summary>
    public string TextWeight { get; init; } = "Normal";

    /// <summary>云图标不透明度，扩展行淡一些。</summary>
    public double IconOpacity { get; init; } = 1;
}

/// <summary>
/// 云候选浮窗。
/// </summary>
/// <remarks>
/// 两条硬约束，任何一条被破坏都会直接毁掉输入体验：
/// <list type="number">
/// <item>绝不抢焦点：窗口样式带 <c>WS_EX_NOACTIVATE</c>，且 <c>ShowActivated=false</c>。
/// 一旦抢走焦点，输入法会立刻提交当前组字。</item>
/// <item>不挡鼠标：窗口样式带 <c>WS_EX_TRANSPARENT</c>，且 <c>IsHitTestVisible=false</c>。</item>
/// </list>
/// <para>
/// 两种样式（<c>Card</c> 纯白卡片、<c>Classic</c> 经典）都是分层窗口 + WPF 自绘：
/// 圆角、描边、阴影全部由 WPF 处理，不申请 DWM 材质。圆角之外就是真透明，
/// 不会出现「圆角但圆角外不透明」的硬边；代价是背景必须实色，不能透出桌面。
/// </para>
/// </remarks>
public partial class CloudCandidateWindow : Window
{
    private const int GapPixels = 6;

    /// <summary>创建浮窗。</summary>
    /// <param name="style">样式：<c>Card</c> 纯白卡片，<c>Classic</c> 经典（Win10 输入法风格）。</param>
    public CloudCandidateWindow(string style)
    {
        PopupStyle = PopupStyleCatalog.Normalize(style);

        InitializeComponent();

        if (PopupStyleCatalog.IsClassic(PopupStyle))
        {
            ApplyClassicStyle();
        }

        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>当前样式标识（已归一化）。</summary>
    public string PopupStyle { get; }

    /// <summary>
    /// 展示若干条云候选。
    /// </summary>
    /// <param name="slots">按「严格 → 扩展」排列的行。</param>
    /// <param name="snapshot">候选窗快照；不可用时退回插入符定位。</param>
    /// <param name="englishMode">是否来自英文模式。</param>
    /// <param name="settings">设置，用于读取位置。</param>
    internal void ShowSuggestions(
        IReadOnlyList<SlotDescriptor> slots,
        CandidateWindowSnapshot snapshot,
        bool englishMode,
        AppSettings settings)
    {
        var rows = new SuggestionRow[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var isStrict = slot.Slot == SuggestionSlot.Strict;
            rows[i] = new SuggestionRow
            {
                Text = slot.Text,
                KeyLabel = slot.KeyLabel,
                TextSize = isStrict ? 15 : 13,
                TextWeight = isStrict ? "SemiBold" : "Normal",
                IconOpacity = isStrict ? 1.0 : 0.5,
            };
        }

        Rows.ItemsSource = rows;

        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            // ShowActivated=False + WS_EX_NOACTIVATE，Show 不会夺走输入焦点。
            Show();
        }

        // 让布局立即完成，否则 ActualWidth/Height 还是 0，位置会算错。
        UpdateLayout();
        Position(snapshot, englishMode ? "Below" : settings.OverlayPlacement);

        var handle = new WindowInteropHelper(this).Handle;

        // 顶层窗口之间也要排 Z 序：别的程序也可能是置顶的（截图工具、录屏条），
        // 只靠创建时的 Topmost 会被压在下面。每次显示显式提一次，带 NOACTIVATE 不会抢焦点。
        // 注意这管不了开始菜单 / 搜索面板这类壳层窗口，它们根本不参与普通窗口的 Z 序。
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        // 「浮窗没出现」最难从现象区分：可能没显示、在屏幕外、或者被别的窗口盖住。
        // 每段组字只记一次（从隐藏变可见），不然每敲一个字母都写一行，日志很快就滚没了。
        if (!wasVisible || TraceZOrderEnabled)
        {
            var rect = NativeMethods.GetWindowRect(handle, out var bounds)
                ? $"{bounds.Left},{bounds.Top} {bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top}"
                : "(取不到矩形)";
            var dpi = VisualTreeHelper.GetDpi(this);
            var hit = TraceZOrderEnabled ? $"，该点命中={DescribeWindowsAt(handle)}" : string.Empty;

            Log.Write(
                $"[浮窗] 显示：候选窗 {(snapshot.IsVisible ? $"{snapshot.Left},{snapshot.Top} {snapshot.Width}x{snapshot.Height}" : "无")}，" +
                $"浮窗 {rect}，置顶={Topmost}，布局 {ActualWidth}x{ActualHeight}@{dpi.DpiScaleX:0.##}x{hit}");
        }
    }

    /// <summary>是否打印 Z 序诊断（环境变量 <c>BETTERPINYIN_TRACE_Z=1</c>）。</summary>
    private static bool TraceZOrderEnabled { get; } =
        Environment.GetEnvironmentVariable("BETTERPINYIN_TRACE_Z") == "1";

    /// <summary>按 Z 序列出覆盖某个屏幕点的顶层窗口（最上面的在前）。</summary>
    /// <param name="handle">浮窗句柄，用于在结果里标出自己。</param>
    /// <returns>形如 <c>进程:类名[宽x高]</c> 的描述。</returns>
    /// <remarks>
    /// 只列「句柄在谁上面」不够：真正决定看不看得见的是这些窗口的矩形有没有盖住浮窗。
    /// 取浮窗右上角稍内侧的点来查，命中不了自己的话说明被别的窗口压住了。
    /// 注意这个方法看不到壳层窗口（开始菜单、搜索面板），它们不参与普通窗口树的枚举。
    /// </remarks>
    private static string DescribeWindowsAt(IntPtr handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out var self))
        {
            return "(取不到矩形)";
        }

        var x = Math.Min(self.Right - 4, self.Left + 8);
        var y = Math.Min(self.Bottom - 4, self.Top + 8);
        var found = new List<string>();

        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd) ||
                    !NativeMethods.GetWindowRect(hwnd, out var rect) ||
                    x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom)
                {
                    return true;
                }

                var mark = hwnd == handle ? "*" : string.Empty;
                found.Add(
                    $"{mark}{WindowProcess.GetProcessName(hwnd)}:{NativeMethods.GetWindowClassName(hwnd)}" +
                    $"[{rect.Right - rect.Left}x{rect.Bottom - rect.Top}]");
                return found.Count < 5;
            },
            IntPtr.Zero);

        return found.Count == 0 ? "(无)" : string.Join(" → ", found);
    }

    /// <summary>隐藏浮窗。</summary>
    public void HideSuggestion()
    {
        if (IsVisible)
        {
            // 和「显示」成对记录，才能看出「显示了但马上又被清掉」这种时序问题。
            Log.Write("[浮窗] 隐藏");
            Hide();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 浮窗不能激活：抢走焦点会让输入法立刻提交组字。
        NativeMethods.ApplyNoActivateStyle(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// 切换为经典样式：Win10 输入法的候选条是直角、实色、阴影很浅。
    /// </summary>
    /// <remarks>
    /// 圆角与阴影只在这一处改，XAML 里的默认值就是纯白卡片。
    /// 样式切换仍由外部重建窗口（<see cref="CloudAssistFeature.ApplySettings"/>），
    /// 但两种样式都是分层窗口，重建只是为了换一套参数，没有窗口类型限制。
    /// </remarks>
    private void ApplyClassicStyle()
    {
        Card.CornerRadius = new CornerRadius(0);
        Card.Margin = new Thickness(5);
        Card.Padding = new Thickness(10, 4, 10, 4);
        Card.Effect = (Effect)FindResource("ClassicShadow");

        // 微软拼音候选条不是纯白：浅色实测 #F5F5F5、底边 #DCDCDC，
        // 用纯白会显得浮窗比输入法本身更亮。两个引用都保持动态，切主题时跟着字典走。
        Card.SetResourceReference(Border.BackgroundProperty, "PopupClassicBackgroundBrush");
        Card.SetResourceReference(Border.BorderBrushProperty, "PopupClassicStrokeBrush");
    }

    private void Position(CandidateWindowSnapshot snapshot, string placement)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // 全程使用物理像素：候选窗矩形来自 UIA，本身就是物理像素。
        var scale = GetDpiScale(handle);
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);

        int anchorLeft;
        int anchorTop;
        int anchorBottom;
        var anchorHeight = 22;

        if (snapshot.IsVisible)
        {
            anchorLeft = snapshot.Left;
            anchorTop = snapshot.Top;
            anchorBottom = snapshot.Bottom;
            anchorHeight = Math.Max(snapshot.Height, 22);
        }
        else
        {
            var caret = CaretLocator.GetAnchor();
            anchorLeft = caret.X;
            anchorTop = caret.Y;
            anchorBottom = caret.Y;
        }

        var work = NativeMethods.GetWorkArea(new NativePoint { X = anchorLeft, Y = anchorBottom });

        int x;
        int y;

        if (string.Equals(placement, "Left", StringComparison.OrdinalIgnoreCase))
        {
            // 贴候选窗左侧、纵向居中；左边放不下就翻到下方。
            x = anchorLeft - width - GapPixels;
            y = anchorTop + ((anchorHeight - height) / 2);
            if (x < work.Left)
            {
                x = anchorLeft;
                y = anchorBottom + GapPixels;
            }
        }
        else
        {
            x = anchorLeft;
            y = anchorBottom + GapPixels;
        }

        // 下方放不下就翻到上方；再放不下就贴底。
        if (y + height > work.Bottom)
        {
            var above = anchorTop - height - GapPixels;
            y = above >= work.Top ? above : Math.Max(work.Top, work.Bottom - height);
        }

        if (x + width > work.Right)
        {
            x = Math.Max(work.Left, work.Right - width);
        }

        if (x < work.Left)
        {
            x = work.Left;
        }

        if (y < work.Top)
        {
            y = work.Top;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private static double GetDpiScale(IntPtr handle)
    {
        var dpi = NativeMethods.GetDpiForWindow(handle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }
}

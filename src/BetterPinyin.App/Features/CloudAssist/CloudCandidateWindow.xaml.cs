using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BetterPinyin.App.Shell;
using BetterPinyin.Contracts;
using BetterPinyin.Core.Settings;
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
/// 三条硬约束，任何一条被破坏都会直接毁掉输入体验：
/// <list type="number">
/// <item>绝不抢焦点：窗口样式带 <c>WS_EX_NOACTIVATE</c>，且 <c>ShowActivated=false</c>。
/// 一旦抢走焦点，输入法会立刻提交当前组字。</item>
/// <item>不挡鼠标：卡片样式用 <c>WS_EX_TRANSPARENT</c>；亚克力样式是普通窗口，
/// 靠 <c>WM_NCHITTEST</c> 返回 <c>HTTRANSPARENT</c> 穿透。</item>
/// <item>不遮候选条：候选条位于沉浸式 IME 图层，永远在我们上方，只能贴着它显示。</item>
/// </list>
/// <para>
/// 亚克力样式必须是非分层窗口（<c>AllowsTransparency=false</c>），否则 DWM 的模糊不会生效。
/// WPF 不允许运行时改这个属性，所以样式切换时由外部重建窗口。
/// </para>
/// </remarks>
public partial class CloudCandidateWindow : Window
{
    private const int GapPixels = 6;
    private const int CornerRadiusDip = 8;

    private readonly bool _acrylic;

    /// <summary>创建浮窗。</summary>
    /// <param name="style">样式：<c>Card</c> 纯白卡片，<c>Acrylic</c> 亚克力。</param>
    public CloudCandidateWindow(string style)
    {
        PopupStyle = style ?? "Card";
        _acrylic = string.Equals(PopupStyle, "Acrylic", StringComparison.OrdinalIgnoreCase);

        if (_acrylic)
        {
            // 必须赶在句柄创建之前设置。
            AllowsTransparency = false;
        }

        InitializeComponent();

        if (_acrylic)
        {
            Background = Brushes.Transparent;
            Card.Background = Brushes.Transparent;
            Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            Card.BorderThickness = new Thickness(1);
            Card.Margin = new Thickness(0);
            Card.Effect = null;
        }

        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>当前样式标识。</summary>
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

        if (!IsVisible)
        {
            // ShowActivated=False + WS_EX_NOACTIVATE，Show 不会夺走输入焦点。
            Show();
        }

        // 让布局立即完成，否则 ActualWidth/Height 还是 0，位置会算错。
        UpdateLayout();
        Position(snapshot, englishMode ? "Below" : settings.OverlayPlacement);
    }

    /// <summary>隐藏浮窗。</summary>
    public void HideSuggestion()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyNoActivateStyle(handle);

        if (!_acrylic)
        {
            return;
        }

        if (!Backdrop.TryApplyAcrylic(this, 0xCCFFFFFF))
        {
            // 降级为实心卡片，避免透明窗口直接透出桌面。
            Background = Brushes.White;
            Card.Background = Brushes.White;
            Card.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xDD, 0xE5));
            Log.Write("[浮窗] 亚克力不可用，已降级为纯白卡片");
            return;
        }

        // 非分层窗口没有 WS_EX_LAYERED 的天然穿透，用命中测试返回 HTTRANSPARENT。
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(NoHitTest);
        }
    }

    private static IntPtr NoHitTest(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;

        if (message != WM_NCHITTEST)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HTTRANSPARENT);
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

        if (_acrylic)
        {
            Backdrop.ApplyRoundedRegion(handle, width, height, (int)Math.Round(CornerRadiusDip * scale));
        }
    }

    private static double GetDpiScale(IntPtr handle)
    {
        var dpi = NativeMethods.GetDpiForWindow(handle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }
}

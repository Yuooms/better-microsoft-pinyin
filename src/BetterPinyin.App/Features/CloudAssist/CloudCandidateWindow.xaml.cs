using System.Windows;
using System.Windows.Interop;
using BetterPinyin.Contracts;
using BetterPinyin.Core.Cloud;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>
/// 云候选浮窗。
/// </summary>
/// <remarks>
/// 三条硬约束，任何一条被破坏都会直接毁掉输入体验：
/// <list type="number">
/// <item>绝不抢焦点：窗口样式带 <c>WS_EX_NOACTIVATE</c>，且 <c>ShowActivated=false</c>。
/// 一旦抢走焦点，输入法会立刻提交当前组字。</item>
/// <item>不挡鼠标：带 <c>WS_EX_TRANSPARENT</c>，点击穿透到下面的窗口。</item>
/// <item>不遮候选条：候选条位于沉浸式 IME 图层，永远在我们上方，
/// 因此浮窗只能贴在它下方，空间不够就翻到上方。</item>
/// </list>
/// </remarks>
public partial class CloudCandidateWindow : Window
{
    private const int GapPixels = 6;

    /// <summary>创建浮窗。</summary>
    public CloudCandidateWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.ApplyNoActivateStyle(handle);
        };
    }

    /// <summary>
    /// 展示一条云候选。
    /// </summary>
    /// <param name="suggestion">候选。</param>
    /// <param name="snapshot">候选窗快照；不可用时退回插入符定位。</param>
    /// <param name="keyHint">采纳键提示文本。</param>
    /// <param name="showCloudIcon">是否显示云图标。</param>
    public void ShowSuggestion(
        CloudSuggestion suggestion,
        CandidateWindowSnapshot snapshot,
        string keyHint,
        bool showCloudIcon)
    {
        CandidateText.Text = suggestion.Text;
        KeyHint.Text = keyHint;
        CloudGlyph.Visibility = showCloudIcon ? Visibility.Visible : Visibility.Collapsed;

        if (!IsVisible)
        {
            // ShowActivated=False + WS_EX_NOACTIVATE，Show 不会夺走输入焦点。
            Show();
        }

        // 让布局立即完成，否则 ActualWidth/Height 还是 0，位置会算错。
        UpdateLayout();
        Position(snapshot);
    }

    /// <summary>隐藏浮窗。</summary>
    public void HideSuggestion()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void Position(CandidateWindowSnapshot snapshot)
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

        if (snapshot.IsVisible)
        {
            anchorLeft = snapshot.Left;
            anchorTop = snapshot.Top;
            anchorBottom = snapshot.Bottom;
        }
        else
        {
            var caret = CaretLocator.GetAnchor();
            anchorLeft = caret.X;
            anchorTop = caret.Y;
            anchorBottom = caret.Y;
        }

        var x = anchorLeft;
        var y = anchorBottom + GapPixels;

        var screenRight = NativeMethods.ScreenWidth;
        var screenBottom = NativeMethods.ScreenHeight;

        // 下方放不下就翻到上方；再放不下就贴底。
        if (y + height > screenBottom)
        {
            var above = anchorTop - height - GapPixels;
            y = above >= 0 ? above : Math.Max(0, screenBottom - height);
        }

        if (x + width > screenRight)
        {
            x = Math.Max(0, screenRight - width);
        }

        if (x < 0)
        {
            x = 0;
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

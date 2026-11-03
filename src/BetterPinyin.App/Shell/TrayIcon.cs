using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace BetterPinyin.App.Shell;

/// <summary>
/// 托盘图标与右键菜单。
/// </summary>
/// <remarks>
/// 用 WinForms 的 <see cref="NotifyIcon"/>：它随 .NET 桌面运行时提供，
/// 不引入任何第三方包，也比手写 Shell_NotifyIcon 的 P/Invoke 更省事。
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private bool _disposed;

    /// <summary>创建托盘图标。</summary>
    /// <param name="onOpenSettings">点击"设置"时调用。</param>
    /// <param name="onToggle">点击"启用/停用"时调用，参数为期望的启用状态。</param>
    /// <param name="onExit">点击"退出"时调用。</param>
    /// <param name="initiallyEnabled">初始是否启用。</param>
    public TrayIcon(
        Action onOpenSettings,
        Action<bool> onToggle,
        Action onExit,
        bool initiallyEnabled)
    {
        _toggleItem = new ToolStripMenuItem("启用云联想")
        {
            Checked = initiallyEnabled,
            CheckOnClick = false,
        };
        _toggleItem.Click += (_, _) => onToggle(!_toggleItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => onOpenSettings()));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => onExit()));

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Better 微软拼音 · 云联想",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => onOpenSettings();
    }

    /// <summary>更新"启用"菜单项的勾选状态。</summary>
    /// <param name="enabled">是否启用。</param>
    public void SetEnabledState(bool enabled) => _toggleItem.Checked = enabled;

    /// <summary>弹一条气泡提示。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    public void ShowBalloon(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    /// <summary>
    /// 运行时绘制一个简单图标，避免为一张 16x16 的图引入资源文件。
    /// </summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(15, 108, 189));
            graphics.FillEllipse(background, 0, 0, 31, 31);

            using var font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(Color.White);
            const string glyph = "云";
            var size = graphics.MeasureString(glyph, font);
            graphics.DrawString(glyph, font, foreground, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}

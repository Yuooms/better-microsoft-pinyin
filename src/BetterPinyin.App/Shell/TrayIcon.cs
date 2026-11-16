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
    private readonly Action _onOpenAbout;

    /// <summary>下一次气泡被点击时执行的动作用它；默认进「关于」页。</summary>
    private Action _balloonAction;

    private bool _disposed;

    /// <summary>创建托盘图标。</summary>
    /// <param name="onOpenSettings">点击"设置"时调用。</param>
    /// <param name="onOpenAbout">点击更新提示气泡时调用。</param>
    /// <param name="onToggle">点击"启用/停用"时调用，参数为期望的启用状态。</param>
    /// <param name="onExit">点击"退出"时调用。</param>
    /// <param name="initiallyEnabled">初始是否启用。</param>
    public TrayIcon(
        Action onOpenSettings,
        Action onOpenAbout,
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
            Text = "Better 微软拼音",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => onOpenSettings();

        // 气泡点击默认跳「关于」（更新提示用）；其它气泡可以临时换掉目标页。
        _onOpenAbout = onOpenAbout;
        _balloonAction = onOpenAbout;
        _notifyIcon.BalloonTipClicked += (_, _) => _balloonAction();
    }

    /// <summary>更新"启用"菜单项的勾选状态。</summary>
    /// <param name="enabled">是否启用。</param>
    public void SetEnabledState(bool enabled) => _toggleItem.Checked = enabled;

    /// <summary>弹一条气泡提示。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="onClick">点击气泡时执行的动作；为 null 时进「关于」页。</param>
    public void ShowBalloon(string title, string message, Action? onClick = null)
    {
        _balloonAction = onClick ?? _onOpenAbout;
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
    /// 托盘图标。优先复用 exe 内嵌的 ICO，保证 exe、任务栏、托盘三处一致；
    /// 资源缺失时才退回运行时绘制，托盘不能因为图标问题而不显示。
    /// </summary>
    private static Icon CreateIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/assets/brand/app.ico", UriKind.Absolute));

            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, new Size(32, 32));
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or UriFormatException)
        {
            Log.Write($"托盘图标加载失败，改用运行时绘制：{ex.GetType().Name} {ex.Message}");
        }

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

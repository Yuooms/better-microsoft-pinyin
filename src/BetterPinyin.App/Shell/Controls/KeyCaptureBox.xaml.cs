using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterPinyin.App.Features.CloudAssist;

namespace BetterPinyin.App.Shell.Controls;

/// <summary>捕获到一个可用键之后的事件参数。</summary>
public sealed class KeyCapturedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="keyId">可保存的标识。</param>
    /// <param name="virtualKey">虚拟键码。</param>
    public KeyCapturedEventArgs(string keyId, int virtualKey)
    {
        KeyId = keyId;
        VirtualKey = virtualKey;
    }

    /// <summary>可保存的标识，例如 <c>Oem3</c>、<c>D6</c>。</summary>
    public string KeyId { get; }

    /// <summary>虚拟键码。</summary>
    public int VirtualKey { get; }
}

/// <summary>
/// 按键捕获框：点一下，再按下想用的键。
/// </summary>
/// <remarks>
/// <para>
/// 用捕获而不是下拉框，是因为采纳键不限于那几个标点——功能键、小键盘、任意 OEM 键都可以。
/// 不可用的键（字母、空格、回车等）会当场拒绝并说明原因，而不是先存下来再在输入时报错。
/// </para>
/// <para>
/// 捕获期间必须把按键标记为 <c>Handled</c>，否则 Tab 会把焦点移走、空格会滚动页面。
/// </para>
/// </remarks>
public partial class KeyCaptureBox : UserControl
{
    /// <summary>当前键标识（WPF <see cref="Key"/> 枚举名）。</summary>
    public static readonly DependencyProperty KeyIdProperty = DependencyProperty.Register(
        nameof(KeyId),
        typeof(string),
        typeof(KeyCaptureBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnKeyIdChanged));

    /// <summary>没有设置按键时显示的占位文字。</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder),
        typeof(string),
        typeof(KeyCaptureBox),
        new FrameworkPropertyMetadata("点击设置按键", OnKeyIdChanged));

    /// <summary>捕获后是否清空，用于「添加按键」这种一次性输入。</summary>
    public static readonly DependencyProperty ClearAfterCaptureProperty = DependencyProperty.Register(
        nameof(ClearAfterCapture),
        typeof(bool),
        typeof(KeyCaptureBox),
        new PropertyMetadata(false));

    private bool _capturing;
    private string? _message;

    /// <summary>创建控件。</summary>
    public KeyCaptureBox()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    /// <summary>捕获成功。</summary>
    public event EventHandler<KeyCapturedEventArgs>? KeyCaptured;

    /// <summary>当前键标识。</summary>
    public string KeyId
    {
        get => (string)GetValue(KeyIdProperty);
        set => SetValue(KeyIdProperty, value);
    }

    /// <summary>占位文字。</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>捕获后是否恢复占位状态。</summary>
    public bool ClearAfterCapture
    {
        get => (bool)GetValue(ClearAfterCaptureProperty);
        set => SetValue(ClearAfterCaptureProperty, value);
    }

    /// <summary>
    /// 显示一条拒绝原因，保持在捕获状态等下一个键。
    /// </summary>
    /// <param name="message">原因。</param>
    public void Reject(string message)
    {
        _message = message;
        UpdateDisplay();
    }

    private static void OnKeyIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyCaptureBox)d).UpdateDisplay();

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled)
        {
            return;
        }

        Focus();
        _capturing = true;
        _message = null;
        UpdateDisplay();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        // Alt 组合键会以 Key.System 送到这里，真正的键在 SystemKey。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            EndCapture();
            e.Handled = true;
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!KeyNames.IsAllowed(virtualKey, out var reason))
        {
            _message = reason;
            UpdateDisplay();
            e.Handled = true;
            return;
        }

        var id = KeyNames.IdOf(virtualKey);
        if (string.IsNullOrEmpty(id))
        {
            _message = "没认出这个键，换一个试试";
            UpdateDisplay();
            e.Handled = true;
            return;
        }

        KeyId = id;
        KeyCaptured?.Invoke(this, new KeyCapturedEventArgs(id, virtualKey));

        if (ClearAfterCapture)
        {
            KeyId = string.Empty;
        }

        EndCapture();
        e.Handled = true;
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturing)
        {
            EndCapture();
        }
    }

    private void EndCapture()
    {
        _capturing = false;
        _message = null;
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (ValueText is null || HintText is null || Root is null)
        {
            return;
        }

        var hasKey = !string.IsNullOrWhiteSpace(KeyId);
        var virtualKey = hasKey ? KeyNames.Resolve(KeyId) : 0;

        if (_capturing)
        {
            ValueText.Text = _message ?? "请按键…";
            HintText.Text = "Esc 取消";
            ValueText.Foreground = _message is null
                ? (Brush?)TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray
                : (Brush?)TryFindResource("WarningBrush") ?? Brushes.OrangeRed;
            Root.BorderBrush = (Brush?)TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
            return;
        }

        ValueText.Text = hasKey ? KeyNames.Describe(virtualKey) : Placeholder;
        ValueText.Foreground = hasKey
            ? (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.Black
            : (Brush?)TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray;

        HintText.Text = hasKey
            ? KeyNames.IsDigit(virtualKey) ? "会盖过同号候选"
                : virtualKey == 0x09 ? "会调出 Bing 搜索"
                : "点击修改"
            : string.Empty;

        Root.BorderBrush = (Brush?)TryFindResource("ControlStrokeBrush") ?? Brushes.LightGray;
    }
}

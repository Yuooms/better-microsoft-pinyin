using System.Windows;

namespace BetterPinyin.App.Shell.Controls;

/// <summary>
/// 设置行的附加属性。
/// </summary>
/// <remarks>
/// <para>
/// 系统「设置」的每一行是「标题 + 说明」在左、控件在右，说明文字紧跟标题而不是缩进到控件下方。
/// 用 <see cref="System.Windows.Controls.HeaderedContentControl"/> 承载标题与控件，
/// 说明文字用附加属性补上——这样一个设置项在 XAML 里只要三行，而且不用再手写
/// <c>Margin="22,2,0,0"</c> 这类靠数字对齐的缩进。
/// </para>
/// <para>
/// 模板里读说明必须用 <c>Binding Path=(ui:SettingRow.Description)</c>：
/// <c>TemplateBinding</c> 对附加属性不可靠，会静默拿不到值。
/// </para>
/// </remarks>
public static class SettingRow
{
    /// <summary>行下方的说明文字。</summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.RegisterAttached(
            "Description",
            typeof(string),
            typeof(SettingRow),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary>是否在行底部画分隔线；卡片最后一行设为 false。</summary>
    public static readonly DependencyProperty ShowDividerProperty =
        DependencyProperty.RegisterAttached(
            "ShowDivider",
            typeof(bool),
            typeof(SettingRow),
            new FrameworkPropertyMetadata(true));

    /// <summary>读取说明文字。</summary>
    /// <param name="element">目标元素。</param>
    /// <returns>说明文字。</returns>
    public static string GetDescription(DependencyObject element) =>
        (string)element.GetValue(DescriptionProperty);

    /// <summary>设置说明文字。</summary>
    /// <param name="element">目标元素。</param>
    /// <param name="value">说明文字。</param>
    public static void SetDescription(DependencyObject element, string value) =>
        element.SetValue(DescriptionProperty, value);

    /// <summary>读取是否显示分隔线。</summary>
    /// <param name="element">目标元素。</param>
    /// <returns>是否显示。</returns>
    public static bool GetShowDivider(DependencyObject element) =>
        (bool)element.GetValue(ShowDividerProperty);

    /// <summary>设置是否显示分隔线。</summary>
    /// <param name="element">目标元素。</param>
    /// <param name="value">是否显示。</param>
    public static void SetShowDivider(DependencyObject element, bool value) =>
        element.SetValue(ShowDividerProperty, value);
}

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BetterPinyin.App.Features.CloudAssist.Views;

/// <summary>词库页里的一行：微软拼音自定义短语。</summary>
internal sealed class PhraseRow : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>构造一行。</summary>
    /// <param name="pinyin">触发拼音（全拼）。</param>
    /// <param name="text">输出文本。</param>
    /// <param name="source">来源标注：本程序学到的 / 手动添加的 / 系统里原有的。</param>
    /// <param name="position">记录里的位置值（输入法不按它排序，仅展示）。</param>
    /// <param name="initials">同时写入的简拼记录；没有时为空串。</param>
    public PhraseRow(string pinyin, string text, string source, int position, string initials = "")
    {
        Pinyin = pinyin;
        Text = text;
        Source = source;
        Position = position;
        Initials = initials;
    }

    /// <summary>触发拼音（全拼）。</summary>
    public string Pinyin { get; }

    /// <summary>输出文本。</summary>
    public string Text { get; }

    /// <summary>来源标注。</summary>
    public string Source { get; }

    /// <summary>记录里的位置值。</summary>
    public int Position { get; }

    /// <summary>同时写入的简拼记录；没有时为空串。</summary>
    public string Initials { get; }

    /// <summary>列表里显示的触发码：同时有简拼时写成「全拼 · 简拼」。</summary>
    public string Code => Initials.Length > 0 ? $"{Pinyin} · {Initials}" : Pinyin;

    /// <summary>是否被勾选（批量操作用）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>词库页「自学习」里的一行：输入法自己记下的词。</summary>
internal sealed class LearnedRow : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>构造一行。</summary>
    /// <param name="text">学到的词。</param>
    /// <param name="pinyin">按单字读音与简拼推断出的全拼；推不出时为空。</param>
    /// <param name="jianpin">文件里保存的简拼。</param>
    /// <param name="length">字数。</param>
    public LearnedRow(string text, string pinyin, string jianpin, int length)
    {
        Text = text;
        Pinyin = pinyin;
        Jianpin = jianpin;
        Length = length;
    }

    /// <summary>学到的词。</summary>
    public string Text { get; }

    /// <summary>推断出的全拼。</summary>
    public string Pinyin { get; }

    /// <summary>文件里保存的简拼。</summary>
    public string Jianpin { get; }

    /// <summary>字数。</summary>
    public int Length { get; }

    /// <summary>是否被勾选（批量删除用）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

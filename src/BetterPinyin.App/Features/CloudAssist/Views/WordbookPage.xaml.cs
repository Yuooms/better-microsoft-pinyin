using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BetterPinyin.Core.Wordbook;
using Microsoft.Win32;

namespace BetterPinyin.App.Features.CloudAssist.Views;

/// <summary>
/// 词库页：直接管理微软拼音的「用户定义的短语」。
/// </summary>
/// <remarks>
/// <para>
/// 早期版本这一页展示的是本程序自己的词库（内置 15 万词 + 用户词 + 权重），
/// 但那些调整只影响我们的浮窗排序，用户在输入法里看不到任何变化，
/// 也就无法判断功能到底有没有生效。现在改成直接读写
/// <c>ChsPinyinEUDPv1.lex</c>：改完打拼音就能在原生候选里看到。
/// </para>
/// <para>
/// 页面实例被 <c>SettingsPageProvider</c> 永久缓存，<c>Loaded</c> 会触发多次，
/// 所以只在第一次读设置，之后只刷新列表。
/// </para>
/// </remarks>
public partial class WordbookPage : Page
{
    private readonly ObservableCollection<PhraseRow> _rows = new();

    private bool _initialized;
    private EditMode _mode = EditMode.None;
    private string? _editingPinyin;

    /// <summary>编辑面板当前是新增还是修改。</summary>
    private enum EditMode
    {
        None,
        Add,
        Update,
    }

    /// <summary>构造页面。</summary>
    public WordbookPage()
    {
        InitializeComponent();
        RowsList.ItemsSource = _rows;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            Reload();
            return;
        }

        _initialized = true;
        AutoWriteToggle.IsChecked = AppServices.Settings.PhraseAutoWrite;
        LocalFirstToggle.IsChecked = AppServices.Settings.WordbookLocalFirst;
        Reload();
    }

    // ── 数据加载 ──────────────────────────────────────────────

    private void Reload()
    {
        var phrases = AppServices.Phrases.Read(out var error);
        var learned = AppServices.Wordbook.Words
            .Select(w => (w.Pinyin, w.Text))
            .ToHashSet();

        _rows.Clear();
        foreach (var phrase in phrases.OrderBy(p => p.Pinyin, StringComparer.Ordinal))
        {
            var source = learned.Contains((phrase.Pinyin, phrase.Text)) ? "学习" : "手动";
            _rows.Add(new PhraseRow(phrase.Pinyin, phrase.Text, source, phrase.Position));
        }

        ApplyFilter();
        UpdateStatus(error);
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var visible = _rows
            .Where(r => query.Length == 0 ||
                        r.Pinyin.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        r.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        RowsList.ItemsSource = visible;
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatus(string? error)
    {
        StatusText.Text = error is null
            ? $"共 {_rows.Count} 条 · {AppServices.Phrases.FilePath}"
            : $"共 {_rows.Count} 条 · 读取有问题：{error}";
    }

    // ── 交互 ──────────────────────────────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            ApplyFilter();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        AppServices.Settings.PhraseAutoWrite = AutoWriteToggle.IsChecked == true;
        AppServices.Settings.WordbookLocalFirst = LocalFirstToggle.IsChecked == true;
        AppServices.SaveSettings();
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => ShowEditor(EditMode.Add, null);

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PhraseRow row)
        {
            ShowEditor(EditMode.Update, row);
        }
    }

    private void ShowEditor(EditMode mode, PhraseRow? row)
    {
        _mode = mode;
        _editingPinyin = row?.Pinyin;
        EditPinyinBox.Text = row?.Pinyin ?? string.Empty;
        EditTextBox.Text = row?.Text ?? string.Empty;
        EditConfirmButton.Content = mode == EditMode.Update ? "保存" : "添加";
        EditPanel.Visibility = Visibility.Visible;
        EditPinyinBox.Focus();
    }

    private void OnEditCancelClick(object sender, RoutedEventArgs e)
    {
        _mode = EditMode.None;
        _editingPinyin = null;
        EditPanel.Visibility = Visibility.Collapsed;
    }

    private void OnEditConfirmClick(object sender, RoutedEventArgs e)
    {
        var pinyin = EditPinyinBox.Text.Trim().ToLowerInvariant();
        var text = EditTextBox.Text.Trim();

        if (!IsValidPinyin(pinyin))
        {
            StatusText.Text = "拼音只能由小写字母组成，例如 yisitalu。";
            return;
        }

        if (text.Length == 0)
        {
            StatusText.Text = "短语不能为空。";
            return;
        }

        var phrases = AppServices.Phrases.Read(out _).ToList();

        // 改拼音等于换一条记录：先把旧的删掉，避免留下死条目。
        if (_mode == EditMode.Update && _editingPinyin is not null &&
            !string.Equals(_editingPinyin, pinyin, StringComparison.OrdinalIgnoreCase))
        {
            phrases.RemoveAll(p => string.Equals(p.Pinyin, _editingPinyin, StringComparison.OrdinalIgnoreCase));
        }

        var existing = phrases.FindIndex(p => string.Equals(p.Pinyin, pinyin, StringComparison.OrdinalIgnoreCase));
        var position = existing >= 0
            ? phrases[existing].Position
            : Math.Clamp(AppServices.Settings.WordbookSyncPosition, 1, 9);

        var phrase = new MspyPhrase(pinyin, position, text);
        if (existing >= 0)
        {
            phrases[existing] = phrase;
        }
        else
        {
            phrases.Add(phrase);
        }

        if (!AppServices.Phrases.ReplaceAll(phrases))
        {
            StatusText.Text = $"写入失败：{AppServices.Phrases.LastError}";
            return;
        }

        OnEditCancelClick(sender, e);
        Reload();
        StatusText.Text = $"已保存「{text}」（{pinyin}），几秒后即可在原生候选里看到。";
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PhraseRow row)
        {
            return;
        }

        var phrases = AppServices.Phrases.Read(out _)
            .Where(p => !string.Equals(p.Pinyin, row.Pinyin, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!AppServices.Phrases.ReplaceAll(phrases))
        {
            StatusText.Text = $"删除失败：{AppServices.Phrases.LastError}";
            return;
        }

        Reload();
        StatusText.Text = $"已删除「{row.Text}」（{row.Pinyin}）。";
    }

    private void OnImportLearnedClick(object sender, RoutedEventArgs e)
    {
        var count = AppServices.SyncPendingPhrases();
        Reload();
        StatusText.Text = count > 0
            ? $"已从学习记录导入 {count} 条。"
            : "学习记录里没有待导入的词（可能都已写入，或与现有短语冲突）。";
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入自定义短语",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var phrases = AppServices.Phrases.Read(out _).ToList();
        var added = 0;

        foreach (var line in File.ReadAllLines(dialog.FileName))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            }

            if (parts.Length < 2)
            {
                continue;
            }

            var pinyin = parts[0].Trim().ToLowerInvariant();
            var text = parts[1].Trim();
            if (!IsValidPinyin(pinyin) || text.Length == 0)
            {
                continue;
            }

            var index = phrases.FindIndex(p => string.Equals(p.Pinyin, pinyin, StringComparison.OrdinalIgnoreCase));
            var position = index >= 0
                ? phrases[index].Position
                : Math.Clamp(AppServices.Settings.WordbookSyncPosition, 1, 9);

            var phrase = new MspyPhrase(pinyin, position, text);
            if (index >= 0)
            {
                phrases[index] = phrase;
            }
            else
            {
                phrases.Add(phrase);
            }

            added++;
        }

        if (added == 0)
        {
            StatusText.Text = "文件里没有可识别的行（每行格式：拼音 + Tab + 短语）。";
            return;
        }

        if (!AppServices.Phrases.ReplaceAll(phrases))
        {
            StatusText.Text = $"导入失败：{AppServices.Phrases.LastError}";
            return;
        }

        Reload();
        StatusText.Text = $"已导入 {added} 条。";
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出自定义短语",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = "mspy-phrases.txt",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        StatusText.Text = AppServices.Phrases.ExportText(dialog.FileName)
            ? $"已导出到 {dialog.FileName}"
            : $"导出失败：{AppServices.Phrases.LastError}";
    }

    /// <summary>拼音只允许小写字母，与输入法的触发码一致。</summary>
    private static bool IsValidPinyin(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => c is >= 'a' and <= 'z');
}

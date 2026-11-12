using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using BetterPinyin.Core.Wordbook;
using Microsoft.Win32;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

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
    /// <summary>自学习列表最多展示这么多行，避免文件异常巨大时把界面拖死。</summary>
    private const int LearningDisplayLimit = 5000;

    private const string PhraseSubtitle =
        "这里直接管理微软拼音的「用户定义的短语」。采纳过的云词会自动写进来，写入后几秒生效，打拼音就能在原生候选里看到。";

    private const string LearningSubtitle =
        "输入法在你反复打某个词之后会自己记下来，存在本机，用于调整原生候选的顺序。勾选不再需要的词可以直接删掉，删完输入法会重新加载。";

    private readonly ObservableCollection<PhraseRow> _rows = new();
    private readonly ObservableCollection<LearnedRow> _learnedRows = new();

    /// <summary>当前筛选后真正显示出来的行，全选只作用于它们。</summary>
    private List<PhraseRow> _visiblePhrases = new();

    private List<LearnedRow> _visibleLearned = new();

    private IReadOnlyList<MspyLearnedWord> _learnedWords = Array.Empty<MspyLearnedWord>();

    private bool _initialized;
    private bool _loading;
    private EditMode _mode = EditMode.None;
    private PhraseRow? _editingRow;

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

        for (var i = 1; i <= 9; i++)
        {
            EditPositionBox.Items.Add(new ComboBoxItem
            {
                Content = i.ToString(CultureInfo.InvariantCulture),
                Tag = i,
            });
        }

        RowsList.ItemsSource = _rows;
        LearningList.ItemsSource = _learnedRows;

        // 默认显示「自定义短语」；--ui-shot 做界面回归时可用环境变量直接打开「自学习」或编辑面板。
        var startupTab = Environment.GetEnvironmentVariable("BETTERPINYIN_UI_TAB");
        ApplyTab(showLearning: string.Equals(startupTab, "learning", StringComparison.OrdinalIgnoreCase));
        if (string.Equals(startupTab, "edit", StringComparison.OrdinalIgnoreCase))
        {
            ShowEditor(EditMode.Add, null);
        }

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

        // 赋值会触发 Checked/Unchecked；这里必须挡住，否则三个开关会互相把对方的
        // 默认值写回配置（新加的键第一次打开时就被改成 false）。
        _loading = true;
        AutoWriteToggle.IsChecked = AppServices.Settings.PhraseAutoWrite;
        LocalFirstToggle.IsChecked = AppServices.Settings.WordbookLocalFirst;
        InitialsToggle.IsChecked = AppServices.Settings.PhraseWriteInitials;
        _loading = false;

        Reload();
    }

    // ── 两个视图 ──────────────────────────────────────────────

    private void OnTabClick(object sender, RoutedEventArgs e) =>
        ApplyTab(showLearning: string.Equals(
            (sender as FrameworkElement)?.Tag as string,
            "Learning",
            StringComparison.Ordinal));

    private void ApplyTab(bool showLearning)
    {
        PhraseView.Visibility = showLearning ? Visibility.Collapsed : Visibility.Visible;
        LearningView.Visibility = showLearning ? Visibility.Visible : Visibility.Collapsed;

        // 当前页用 Primary，另一个用 Secondary；WPF-UI 的按钮本身就有这两种外观。
        PhraseTabButton.Appearance = showLearning ? ControlAppearance.Secondary : ControlAppearance.Primary;
        LearningTabButton.Appearance = showLearning ? ControlAppearance.Primary : ControlAppearance.Secondary;
        SubtitleText.Text = showLearning ? LearningSubtitle : PhraseSubtitle;

        if (showLearning)
        {
            LoadLearning();
        }
    }

    // ── 自学习 ────────────────────────────────────────────────

    private void LoadLearning()
    {
        var ok = AppServices.Learning.TryRead(out var words, out var error);
        _learnedWords = ok ? words : Array.Empty<MspyLearnedWord>();
        ApplyLearningFilter();

        if (!ok)
        {
            LearningStatusText.Text = $"读取失败：{error} · {AppServices.Learning.FilePath}";
            return;
        }

        var lastWrite = AppServices.Learning.LastWriteTime;
        var stamp = lastWrite is null
            ? "尚未写入"
            : lastWrite.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        LearningStatusText.Text =
            $"共 {_learnedWords.Count} 条 · 最后更新 {stamp} · {AppServices.Learning.FilePath}";
    }

    private void ApplyLearningFilter()
    {
        var query = LearningSearchBox.Text?.Trim() ?? string.Empty;
        var lexicon = AppServices.Lexicon;

        _learnedRows.Clear();
        foreach (var word in _learnedWords)
        {
            if (_learnedRows.Count >= LearningDisplayLimit)
            {
                break;
            }

            if (query.Length > 0 &&
                !word.Text.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !word.Jianpin.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _learnedRows.Add(new LearnedRow(
                word.Text,
                MspyLearningStore.ResolvePinyin(word.Text, word.Jianpin, lexicon),
                word.Jianpin,
                word.Length));
        }

        LearningEmptyText.Visibility = _learnedRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _visibleLearned = _learnedRows.ToList();
        UpdateLearningSelection();
    }

    private void OnLearningSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            foreach (var row in _learnedRows)
            {
                row.IsSelected = false;
            }

            ApplyLearningFilter();
        }
    }

    private void OnLearningRefreshClick(object sender, RoutedEventArgs e) => LoadLearning();

    private void OnLearningExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出自学习词汇",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = "mspy-learned.csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var ok = MspyLearningStore.ExportCsv(_learnedWords, dialog.FileName, AppServices.Lexicon);
        LearningStatusText.Text = ok
            ? $"已导出 {_learnedWords.Count} 条到 {dialog.FileName}"
            : "导出失败：文件被占用或没有写入权限。";
    }

    // ── 数据加载 ──────────────────────────────────────────────

    private void Reload()
    {
        var phrases = AppServices.Phrases.Read(out var error);
        var learned = AppServices.Wordbook.Words
            .Select(w => (w.Pinyin, w.Text))
            .ToHashSet();
        var variants = FindInitialVariants(phrases);
        var codes = phrases.Select(p => (p.Text, p.Pinyin)).ToHashSet();

        _rows.Clear();
        foreach (var phrase in phrases.OrderBy(p => p.Pinyin, StringComparer.Ordinal))
        {
            if (variants.Contains((phrase.Text, phrase.Pinyin)))
            {
                continue; // 简拼记录由全拼那一行代表，不单独显示
            }

            var initials = PhraseVariants.InitialsOf(phrase.Pinyin);
            var shown = initials is not null && codes.Contains((phrase.Text, initials)) ? initials : string.Empty;
            var source = learned.Contains((phrase.Pinyin, phrase.Text)) ? "学习" : "手动";
            _rows.Add(new PhraseRow(phrase.Pinyin, phrase.Text, source, phrase.Position, shown));
        }

        ApplyFilter();
        UpdateStatus(error);
    }

    /// <summary>
    /// 找出「自动补出来的简拼记录」：同一文本、拼音等于另一条记录的简拼。
    /// </summary>
    private static HashSet<(string Text, string Pinyin)> FindInitialVariants(IReadOnlyList<MspyPhrase> phrases)
    {
        var codes = phrases.Select(p => (p.Text, p.Pinyin)).ToHashSet();
        var variants = new HashSet<(string Text, string Pinyin)>();
        foreach (var phrase in phrases)
        {
            var initials = PhraseVariants.InitialsOf(phrase.Pinyin);
            if (initials is not null && codes.Contains((phrase.Text, initials)))
            {
                variants.Add((phrase.Text, initials));
            }
        }

        return variants;
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
        _visiblePhrases = visible;
        UpdatePhraseSelection();
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
            // 换了筛选条件就重新开始选，免得删到看不见的行。
            foreach (var row in _rows)
            {
                row.IsSelected = false;
            }

            ApplyFilter();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loading)
        {
            return;
        }

        AppServices.Settings.PhraseAutoWrite = AutoWriteToggle.IsChecked == true;
        AppServices.Settings.WordbookLocalFirst = LocalFirstToggle.IsChecked == true;

        var initialsWas = AppServices.Settings.PhraseWriteInitials;
        AppServices.Settings.PhraseWriteInitials = InitialsToggle.IsChecked == true;
        AppServices.SaveSettings();

        // 刚打开「同时写入简拼」时，把已有的短语补一遍，不用等下次采纳。
        if (!initialsWas && AppServices.Settings.PhraseWriteInitials)
        {
            var count = AppServices.BackfillInitialPhrases();
            Reload();
            StatusText.Text = count > 0
                ? $"已为 {count} 条短语补写简拼，现在打首字母也能出词。"
                : "现有短语都已经有简拼记录。";
        }
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
        _editingRow = row;
        EditPinyinBox.Text = row?.Pinyin ?? string.Empty;
        EditTextBox.Text = row?.Text ?? string.Empty;
        EditConfirmButton.Content = mode == EditMode.Update ? "保存" : "添加";

        // 新增时用设置里的默认位置，修改时沿用这条记录原来的位置，用户随时可改。
        var position = row?.Position ?? AppServices.Settings.WordbookSyncPosition;
        EditPositionBox.SelectedIndex = Math.Clamp(position, 1, 9) - 1;

        EditPanel.Visibility = Visibility.Visible;
        EditPinyinBox.Focus();
    }

    private void OnEditCancelClick(object sender, RoutedEventArgs e)
    {
        _mode = EditMode.None;
        _editingRow = null;
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

        // 改拼音等于换一条记录：先把旧的（连同它的简拼）删掉，避免留下死条目。
        if (_mode == EditMode.Update && _editingRow is not null)
        {
            phrases.RemoveAll(p => SameCode(p, _editingRow.Pinyin));
            if (_editingRow.Initials.Length > 0)
            {
                phrases.RemoveAll(p => SameCode(p, _editingRow.Initials));
            }
        }

        var position = EditPositionBox.SelectedItem is ComboBoxItem { Tag: int value }
            ? value
            : Math.Clamp(AppServices.Settings.WordbookSyncPosition, 1, 9);

        phrases.RemoveAll(p => SameCode(p, pinyin));
        phrases.Add(new MspyPhrase(pinyin, position, text));

        // 再补一条简拼记录，让首字母也能打出来；简拼被别的词占了就不写。
        var note = string.Empty;
        var initials = AppServices.Settings.PhraseWriteInitials
            ? PhraseVariants.InitialsOf(pinyin)
            : null;
        if (initials is not null)
        {
            var clash = phrases.FirstOrDefault(p => SameCode(p, initials) &&
                                                    !string.Equals(p.Text, text, StringComparison.Ordinal));
            if (clash.Text is not null)
            {
                note = $"（简拼 {initials} 已被「{clash.Text}」占用，没有写入简拼）";
            }
            else
            {
                phrases.RemoveAll(p => SameCode(p, initials));
                phrases.Add(new MspyPhrase(initials, position, text));
            }
        }

        if (!AppServices.Phrases.ReplaceAll(phrases))
        {
            StatusText.Text = $"写入失败：{AppServices.Phrases.LastError}";
            return;
        }

        OnEditCancelClick(sender, e);
        Reload();
        StatusText.Text = $"已保存「{text}」（{pinyin}），几秒后即可在原生候选里看到。{note}";
    }

    /// <summary>两条记录的触发码是否相同（忽略大小写）。</summary>
    private static bool SameCode(MspyPhrase phrase, string code) =>
        string.Equals(phrase.Pinyin, code, StringComparison.OrdinalIgnoreCase);

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PhraseRow row)
        {
            return;
        }

        // 读失败时绝不能接着写：那会把整个短语文件清空。
        var phrases = AppServices.Phrases.Read(out var readError).ToList();
        if (readError is not null)
        {
            StatusText.Text = $"删除中止：读取短语失败（{readError}）";
            return;
        }

        phrases.RemoveAll(p => SameCode(p, row.Pinyin) ||
                               (row.Initials.Length > 0 && SameCode(p, row.Initials)));

        if (!AppServices.Phrases.ReplaceAll(phrases))
        {
            StatusText.Text = $"删除失败：{AppServices.Phrases.LastError}";
            return;
        }

        Reload();
        StatusText.Text = $"已删除「{row.Text}」（{row.Pinyin}）。";
    }

    // ── 多选与批量删除 ────────────────────────────────────────

    /// <summary>行的复选框点击：两个列表共用，按行的类型分派。</summary>
    private void OnRowSelectionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhraseRow)
        {
            UpdatePhraseSelection();
        }
        else
        {
            UpdateLearningSelection();
        }
    }

    /// <summary>同步「删除选中」按钮和表头全选框的状态。</summary>
    private void UpdatePhraseSelection()
    {
        var count = _rows.Count(r => r.IsSelected);
        PhraseDeleteButton.Content = count > 0 ? $"删除选中 ({count})" : "删除选中";
        PhraseDeleteButton.IsEnabled = count > 0;

        PhraseSelectAll.IsChecked = _visiblePhrases.Count > 0 && _visiblePhrases.All(r => r.IsSelected)
            ? true
            : _visiblePhrases.Any(r => r.IsSelected) ? null : false;
    }

    private void UpdateLearningSelection()
    {
        var count = _learnedRows.Count(r => r.IsSelected);
        LearningDeleteButton.Content = count > 0 ? $"删除选中 ({count})" : "删除选中";
        LearningDeleteButton.IsEnabled = count > 0;

        LearningSelectAll.IsChecked = _visibleLearned.Count > 0 && _visibleLearned.All(r => r.IsSelected)
            ? true
            : _visibleLearned.Any(r => r.IsSelected) ? null : false;
    }

    private void OnPhraseSelectAllClick(object sender, RoutedEventArgs e)
    {
        var select = PhraseSelectAll.IsChecked == true;
        foreach (var row in _visiblePhrases)
        {
            row.IsSelected = select;
        }

        UpdatePhraseSelection();
    }

    private void OnLearningSelectAllClick(object sender, RoutedEventArgs e)
    {
        var select = LearningSelectAll.IsChecked == true;
        foreach (var row in _visibleLearned)
        {
            row.IsSelected = select;
        }

        UpdateLearningSelection();
    }

    private void OnDeleteSelectedPhrasesClick(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (!Confirm($"确定删除选中的 {selected.Count} 条短语？删除后打这些拼音就不会再出词了。", "删除短语"))
        {
            return;
        }

        // 读失败时绝不能接着写：那会把整个短语文件清空。
        var phrases = AppServices.Phrases.Read(out var readError).ToList();
        if (readError is not null)
        {
            StatusText.Text = $"删除中止：读取短语失败（{readError}）";
            return;
        }

        phrases.RemoveAll(p => selected.Any(r => SameCode(p, r.Pinyin) ||
                                                (r.Initials.Length > 0 && SameCode(p, r.Initials))));

        if (!AppServices.Phrases.ReplaceAll(phrases))
        {
            StatusText.Text = $"删除失败：{AppServices.Phrases.LastError}";
            return;
        }

        Reload();
        StatusText.Text = $"已删除 {selected.Count} 条短语。";
    }

    private void OnDeleteSelectedLearningClick(object sender, RoutedEventArgs e)
    {
        var selected = _learnedRows
            .Where(r => r.IsSelected)
            .Select(r => r.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (selected.Count == 0)
        {
            return;
        }

        if (!Confirm(
                $"确定从自学习词库里删除选中的 {selected.Count} 个词？\n\n"
                + "删除后输入法会重新加载词库，几秒内生效；原文件会先备份。",
                "删除自学习词"))
        {
            return;
        }

        LearningStatusText.Text = $"正在删除 {selected.Count} 条…";
        UpdateLayout();

        var result = AppServices.RemoveLearnedWords(selected, GetWindowHandle());

        LoadLearning();

        if (result.Error is not null)
        {
            LearningStatusText.Text = $"删除失败：{result.Error}";
            return;
        }

        if (result.Removed == 0)
        {
            LearningStatusText.Text = "这些词已经不在自学习词库里了（可能被输入法自己清理了）。";
            return;
        }

        LearningStatusText.Text = result.Reloaded
            ? $"已删除 {result.Removed} 条，{result.ReloadMessage}打这些拼音就不会再出这些词了。"
            : $"已删除 {result.Removed} 条并写入文件，但没能让输入法重新加载：{result.ReloadMessage}"
            + "改动可能会被输入法覆盖，建议注销后重新登录再试。";
    }

    /// <summary>取本页所在窗口的句柄，用于让输入法切换一次输入法上下文。</summary>
    private IntPtr GetWindowHandle()
    {
        var window = Window.GetWindow(this);
        return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
    }

    private bool Confirm(string message, string title)
    {
        var owner = Window.GetWindow(this);
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return result == MessageBoxResult.OK;
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

            var index = phrases.FindIndex(p => SameCode(p, pinyin));
            var position = index >= 0
                ? phrases[index].Position
                : Math.Clamp(AppServices.Settings.WordbookSyncPosition, 1, 9);

            phrases.RemoveAll(p => SameCode(p, pinyin));
            phrases.Add(new MspyPhrase(pinyin, position, text));

            var initials = AppServices.Settings.PhraseWriteInitials
                ? PhraseVariants.InitialsOf(pinyin)
                : null;
            if (initials is not null &&
                !phrases.Any(p => SameCode(p, initials) && !string.Equals(p.Text, text, StringComparison.Ordinal)))
            {
                phrases.RemoveAll(p => SameCode(p, initials));
                phrases.Add(new MspyPhrase(initials, position, text));
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

        // 自动补出来的简拼不单独导出，否则文件里同一句话会出现两次。
        var variants = FindInitialVariants(AppServices.Phrases.Read(out _));
        StatusText.Text = AppServices.Phrases.ExportText(
            dialog.FileName,
            p => !variants.Contains((p.Text, p.Pinyin)))
            ? $"已导出到 {dialog.FileName}"
            : $"导出失败：{AppServices.Phrases.LastError}";
    }

    /// <summary>拼音只允许小写字母，与输入法的触发码一致。</summary>
    private static bool IsValidPinyin(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => c is >= 'a' and <= 'z');
}

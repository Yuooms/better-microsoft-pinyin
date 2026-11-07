using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BetterPinyin.Core.Lexicon;
using BetterPinyin.Core.Wordbook;
using Microsoft.Win32;

namespace BetterPinyin.App.Features.CloudAssist.Views;

/// <summary>
/// 词库管理页：搜索、筛选、调权重、遗忘、手动添加、同步到微软拼音。
/// </summary>
/// <remarks>
/// 页面实例被 <c>SettingsPageProvider</c> 永久缓存，所以 <c>Loaded</c> 会触发多次：
/// 只有第一次读设置，之后只刷新列表。事件订阅必须在 <c>Unloaded</c> 退订。
/// </remarks>
public partial class WordbookPage : Page
{
    private const int PageSize = 200;
    private const int MaxRows = 2000;

    private readonly ObservableCollection<WordbookRow> _rows = new();
    private readonly DispatcherTimer _searchTimer;

    private bool _initialized;
    private string _query = string.Empty;
    private string _filter = "All";

    private IReadOnlyList<int> _matches = Array.Empty<int>();
    private int _matchCursor;
    private bool _loading;

    /// <summary>创建词库页。</summary>
    public WordbookPage()
    {
        InitializeComponent();

        RowsList.ItemsSource = _rows;
        RowsList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnListScrollChanged));

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Reload();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static UserWordbook Book => AppServices.Wordbook;

    private static IWordCatalog Catalog => (IWordCatalog)AppServices.Lexicon;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            _initialized = true;
            LearnToggle.IsChecked = AppServices.Settings.WordbookLearnOnAccept;
            LocalFirstToggle.IsChecked = AppServices.Settings.WordbookLocalFirst;
            AutoSyncToggle.IsChecked = AppServices.Settings.WordbookAutoSync;
            Reload();
        }

        Book.Changed -= OnBookChanged;
        Book.Changed += OnBookChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Book.Changed -= OnBookChanged;

    private void OnBookChanged(object? sender, EventArgs e)
    {
        // 采纳线程也会触发；只刷新统计，不重排列表，避免用户正在看的时候列表跳动。
        Dispatcher.BeginInvoke(new Action(RefreshCount));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _query = SearchBox.Text?.Trim() ?? string.Empty;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _filter = (FilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        Reload();
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        AppServices.Settings.WordbookLearnOnAccept = LearnToggle.IsChecked == true;
        AppServices.Settings.WordbookLocalFirst = LocalFirstToggle.IsChecked == true;
        AppServices.Settings.WordbookAutoSync = AutoSyncToggle.IsChecked == true;
        AppServices.SaveSettings();
    }

    /// <summary>重建列表：先用户词，再内置词的第一页。</summary>
    private void Reload()
    {
        _loading = true;
        try
        {
            _rows.Clear();
            _matches = Catalog.Search(_query, 5000);
            _matchCursor = 0;

            foreach (var word in Book.Words)
            {
                if (!UserMatches(word) || !PassesFilter(word.Adjustment, isUser: true, word.UdpState))
                {
                    continue;
                }

                _rows.Add(new WordbookRow(
                    word.Text,
                    word.Pinyin,
                    IsUser: true,
                    Rank: -1,
                    word.Hits,
                    word.Adjustment,
                    word.UdpState));
            }

            AppendBuiltin(PageSize);
        }
        finally
        {
            _loading = false;
        }

        RefreshCount();
    }

    private void AppendBuiltin(int count)
    {
        var added = 0;
        while (added < count && _matchCursor < _matches.Count && _rows.Count < MaxRows)
        {
            var index = _matches[_matchCursor++];
            if (!Catalog.TryGet(index, out var entry))
            {
                continue;
            }

            var adjustment = Book.AdjustmentOf(entry.Text);
            if (!PassesFilter(adjustment, isUser: false, UdpSyncState.Pending))
            {
                continue;
            }

            _rows.Add(new WordbookRow(
                entry.Text,
                entry.Pinyin,
                IsUser: false,
                entry.Index,
                Hits: 0,
                adjustment,
                UdpSyncState.Pending));
            added++;
        }
    }

    private bool PassesFilter(WordAdjustment adjustment, bool isUser, UdpSyncState sync) => _filter switch
    {
        "User" => isUser,
        "Boosted" => adjustment == WordAdjustment.Boosted,
        "Reduced" => adjustment == WordAdjustment.Reduced,
        "Hidden" => adjustment == WordAdjustment.Hidden,
        "Pending" => isUser && sync != UdpSyncState.Synced,
        _ => true,
    };

    private bool UserMatches(UserWord word)
    {
        if (_query.Length == 0)
        {
            return true;
        }

        if (word.Text.Contains(_query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsAscii(_query) &&
               (word.Pinyin.StartsWith(_query, StringComparison.OrdinalIgnoreCase) ||
                word.Initials.StartsWith(_query, StringComparison.OrdinalIgnoreCase));
    }

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_loading || e.OriginalSource is not ScrollViewer)
        {
            return;
        }

        if (e.ExtentHeight - e.VerticalOffset - e.ViewportHeight > 6)
        {
            return;
        }

        if (_rows.Count >= MaxRows)
        {
            StatusText.Text = $"结果过多，只显示前 {MaxRows} 条，请细化搜索。";
            return;
        }

        AppendBuiltin(PageSize);
        RefreshCount();
    }

    private void RefreshCount()
    {
        var total = Book.Count;
        var hidden = Book.Words.Count(w => w.IsHidden) +
                     Book.Overrides.Count(o => o.Adjustment == WordAdjustment.Hidden);
        var pending = Book.Words.Count(w => w.UdpState != UdpSyncState.Synced);
        CountText.Text = $"我的 {total} · 已遗忘 {hidden} · 待同步 {pending} · 显示 {_rows.Count}";
    }

    private void OnBoostClick(object sender, RoutedEventArgs e) => Adjust(sender, WordAdjustment.Boosted);

    private void OnReduceClick(object sender, RoutedEventArgs e) => Adjust(sender, WordAdjustment.Reduced);

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WordbookRow row })
        {
            return;
        }

        var target = row.Adjustment == WordAdjustment.Hidden
            ? WordAdjustment.Normal
            : WordAdjustment.Hidden;
        Adjust(sender, target);
    }

    private void Adjust(object sender, WordAdjustment adjustment)
    {
        if (sender is not Button { Tag: WordbookRow row })
        {
            return;
        }

        Book.SetAdjustment(row.Text, row.Pinyin, adjustment);
        ReplaceRow(row.Text);
        RefreshCount();
        StatusText.Text = $"{row.Text} → {Describe(adjustment)}";
    }

    private void ReplaceRow(string text)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!string.Equals(_rows[i].Text, text, StringComparison.Ordinal))
            {
                continue;
            }

            var row = _rows[i];
            var word = Book.Words.FirstOrDefault(w => string.Equals(w.Text, text, StringComparison.Ordinal));
            _rows[i] = row with
            {
                Adjustment = Book.AdjustmentOf(text),
                SyncState = word?.UdpState ?? row.SyncState,
                Hits = word?.Hits ?? row.Hits,
            };
            return;
        }
    }

    private static string Describe(WordAdjustment adjustment) => adjustment switch
    {
        WordAdjustment.Boosted => "已提升",
        WordAdjustment.Reduced => "已降低",
        WordAdjustment.Hidden => "已遗忘",
        _ => "已恢复默认",
    };

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        AddPanel.Visibility = Visibility.Visible;
        AddPositionBox.Text = AppServices.Settings.WordbookSyncPosition.ToString();
        AddWordBox.Focus();
    }

    private void OnAddCancelClick(object sender, RoutedEventArgs e) => AddPanel.Visibility = Visibility.Collapsed;

    private void OnAddConfirmClick(object sender, RoutedEventArgs e)
    {
        var text = AddWordBox.Text?.Trim() ?? string.Empty;
        var pinyin = AddPinyinBox.Text?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!Book.Learn(text, pinyin, pinyin, out var word))
        {
            StatusText.Text = "添加失败：词要是 2~8 个汉字，拼音要能完整切分（如 yisitalu）。";
            return;
        }

        if (int.TryParse(AddPositionBox.Text, out var position))
        {
            Book.SetUdpPosition(text, position);
        }

        AddPanel.Visibility = Visibility.Collapsed;
        AddWordBox.Text = string.Empty;
        AddPinyinBox.Text = string.Empty;
        Reload();
        StatusText.Text = $"已添加 {word.Text}（{word.Pinyin}）";
    }

    private void OnSyncClick(object sender, RoutedEventArgs e)
    {
        var pending = Book.Words.Where(w => w.UdpState != UdpSyncState.Synced).ToList();
        if (pending.Count == 0)
        {
            StatusText.Text = "没有待同步的词。";
            return;
        }

        var store = new MspyPhraseStore();
        var defaultPosition = Math.Clamp(AppServices.Settings.WordbookSyncPosition, 1, 9);
        var phrases = pending.Select(w => new MspyPhrase(
            w.Pinyin,
            w.UdpPosition == 0 ? defaultPosition : w.UdpPosition,
            w.Text));

        if (!store.Merge(phrases, overwriteOthers: false, out var conflicts))
        {
            foreach (var word in pending)
            {
                Book.SetUdpState(word.Text, UdpSyncState.Failed);
            }

            StatusText.Text = $"写入失败：{store.LastError}。可先用「导出」生成文本，再从系统设置导入。";
            Reload();
            return;
        }

        var conflictPinyin = new HashSet<string>(conflicts.Select(c => c.Pinyin), StringComparer.OrdinalIgnoreCase);
        var written = 0;
        foreach (var word in pending)
        {
            if (conflictPinyin.Contains(word.Pinyin))
            {
                Book.SetUdpState(word.Text, UdpSyncState.Conflict);
            }
            else
            {
                Book.SetUdpState(word.Text, UdpSyncState.Synced);
                written++;
            }
        }

        Reload();
        StatusText.Text = conflicts.Count == 0
            ? $"已写入 {written} 条。切换一次输入法或重启后生效。"
            : $"已写入 {written} 条，{conflicts.Count} 条与已有短语冲突未覆盖。";
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出词库",
            Filter = "文本文件|*.txt",
            FileName = "better-pinyin-words.txt",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var lines = Book.Words.Select(w => $"{w.Pinyin}\t{w.Text}");
            File.WriteAllLines(dialog.FileName, lines);
            StatusText.Text = $"已导出 {Book.Count} 条到 {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入词库",
            Filter = "文本文件|*.txt",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var added = 0;
        foreach (var line in File.ReadAllLines(dialog.FileName))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            if (Book.Learn(parts[1].Trim(), parts[0].Trim().ToLowerInvariant(), parts[0].Trim(), out _))
            {
                added++;
            }
        }

        Reload();
        StatusText.Text = $"已导入 {added} 条。";
    }

    private static bool IsAscii(string value)
    {
        foreach (var ch in value)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z')))
            {
                return false;
            }
        }

        return true;
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using BetterPinyin.Core.Wordbook;

namespace BetterPinyin.App.Features.CloudAssist.Views;

/// <summary>
/// 自学习词库的备份管理：看清单、看差异、恢复、删除。
/// </summary>
/// <remarks>
/// <para>
/// 备份由 <see cref="MspyLearningStore"/> 在每次写文件之前自动生成，
/// 这里只负责把目录里的文件读出来给人看。
/// </para>
/// <para>
/// 恢复同样是「写文件 + 让输入法重读」，所以必须在窗口处于前台时执行——
/// 和词库页的删除一样，借用本窗口自己的输入法上下文（<c>ImeReloader</c>）。
/// </para>
/// </remarks>
public partial class LearningBackupWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>差异预览最多显示多少条，避免一次塞进几万行把界面拖死。</summary>
    private const int DiffDisplayLimit = 400;

    private readonly ObservableCollection<BackupRow> _rows = new();
    private readonly ObservableCollection<DiffRow> _diffRows = new();

    private IReadOnlyList<MspyLearnedWord> _currentWords = Array.Empty<MspyLearnedWord>();

    /// <summary>构造窗口。</summary>
    public LearningBackupWindow()
    {
        InitializeComponent();
        BackupList.ItemsSource = _rows;
        DiffList.ItemsSource = _diffRows;
        Loaded += (_, _) => Reload();
    }

    /// <summary>本次打开是否真的改过词库（恢复或删备份），关闭后调用方据此刷新列表。</summary>
    public bool Changed { get; private set; }

    /// <summary>最后一次操作的结果说明，关窗后由调用方显示在词库页上。</summary>
    public string? LastStatus { get; private set; }

    // ── 加载与预览 ────────────────────────────────────────────

    private void Reload()
    {
        AppServices.Learning.TryRead(out var current, out _);
        _currentWords = current;

        var selectedPath = (BackupList.SelectedItem as BackupRow)?.Path;

        _rows.Clear();
        foreach (var backup in AppServices.Learning.ListBackups())
        {
            var words = MspyLearningStore.TryReadFile(backup.FilePath, out var parsed, out _)
                ? parsed
                : Array.Empty<MspyLearnedWord>();
            var diff = MspyLearningStore.Diff(words, _currentWords);
            _rows.Add(new BackupRow(backup, diff));
        }

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 刷新后尽量停在原来那一行；首次打开就默认选中最新的备份，省一次点击。
        BackupList.SelectedItem = _rows.FirstOrDefault(r => r.Path == selectedPath) ?? _rows.FirstOrDefault();
        if (BackupList.SelectedItem is null)
        {
            UpdatePreview(null);
        }
    }

    private void OnBackupSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdatePreview(BackupList.SelectedItem as BackupRow);

    private void UpdatePreview(BackupRow? row)
    {
        _diffRows.Clear();

        var hasSelection = row is not null;
        RestoreButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;

        if (row is null)
        {
            PreviewTitle.Text = _rows.Count == 0
                ? "还没有可用的备份"
                : "选中一份备份查看差异";
            return;
        }

        PreviewTitle.Text = $"恢复到这份备份（{row.Time} · {row.WordCount} 条）后："
                          + $"会回来 {row.Diff.Missing.Count} 条，会消失 {row.Diff.Extra.Count} 条";

        if (row.Diff.IsEmpty)
        {
            PreviewTitle.Text = $"这份备份（{row.Time} · {row.WordCount} 条）和现在完全一样。";
            return;
        }

        foreach (var word in row.Diff.Missing.Take(DiffDisplayLimit))
        {
            _diffRows.Add(new DiffRow($"＋ {word}", DiffKind.Missing));
        }

        foreach (var word in row.Diff.Extra.Take(DiffDisplayLimit))
        {
            _diffRows.Add(new DiffRow($"－ {word}", DiffKind.Extra));
        }

        var hidden = row.Diff.Missing.Count + row.Diff.Extra.Count - _diffRows.Count;
        if (hidden > 0)
        {
            _diffRows.Add(new DiffRow($"…另有 {hidden} 条未显示", DiffKind.Note));
        }
    }

    // ── 操作 ──────────────────────────────────────────────────

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupRow row)
        {
            return;
        }

        if (!Confirm(
                $"确定把自学习词库恢复到 {row.Time} 的备份（{row.WordCount} 条）？\n\n"
                + "当前状态会先自动备份一份，恢复之后还可以再回到现在的样子。",
                "恢复备份"))
        {
            return;
        }

        SetStatus("正在恢复…");
        UpdateLayout();

        var result = AppServices.RestoreLearnedBackup(row.Path, GetWindowHandle());
        Changed = true;

        if (result.Error is not null)
        {
            SetStatus($"恢复失败：{result.Error}");
            Reload();
            return;
        }

        Reload();
        SetStatus(result.Reloaded
            ? $"已恢复到 {row.Time} 的 {result.Restored} 条，{result.ReloadMessage}打拼音就能看到差别。"
            : $"已恢复到 {row.Time} 的 {result.Restored} 条，但没能让输入法重新加载：{result.ReloadMessage}"
            + "改动可能会被输入法覆盖，建议注销后重新登录再试。");
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupRow row)
        {
            return;
        }

        if (!Confirm(
                $"确定删除 {row.Time} 这份备份？\n\n只删备份文件，当前词库不受影响。",
                "删除备份"))
        {
            return;
        }

        if (!AppServices.Learning.TryDeleteBackup(row.Path, out var error))
        {
            SetStatus($"删除失败：{error}");
            return;
        }

        Changed = true;
        Reload();
        SetStatus($"已删除 {row.Time} 的备份。");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ── 辅助 ──────────────────────────────────────────────────

    private IntPtr GetWindowHandle() => new WindowInteropHelper(this).Handle;

    /// <summary>更新状态栏，同时留给调用方一份。</summary>
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        LastStatus = text;
    }

    private bool Confirm(string message, string title)
    {
        var result = MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return result == MessageBoxResult.OK;
    }

    /// <summary>清单里的一行。</summary>
    internal sealed class BackupRow
    {
        public BackupRow(LearningBackup backup, LearningDiff diff)
        {
            Path = backup.FilePath;
            Time = backup.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            WordCount = backup.WordCount;
            Diff = diff;
            Size = FormatSize(backup.SizeBytes);

            Delta = diff.IsEmpty
                ? "一致"
                : $"+{diff.Missing.Count} / −{diff.Extra.Count}";
        }

        public string Path { get; }

        public string Time { get; }

        public int WordCount { get; }

        public string Delta { get; }

        public string Size { get; }

        public LearningDiff Diff { get; }

        private static string FormatSize(long bytes) => bytes < 1024
            ? $"{bytes} B"
            : $"{bytes / 1024.0:0.#} KB";
    }

    /// <summary>差异预览里的一行。</summary>
    internal sealed class DiffRow
    {
        public DiffRow(string text, DiffKind kind)
        {
            Display = text;
            Kind = kind;
        }

        public string Display { get; }

        public DiffKind Kind { get; }
    }

    /// <summary>差异类型：恢复后会回来 / 恢复后会消失 / 提示。</summary>
    internal enum DiffKind
    {
        Missing,
        Extra,
        Note,
    }
}

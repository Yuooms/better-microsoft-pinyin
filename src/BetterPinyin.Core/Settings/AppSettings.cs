using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterPinyin.Core.Settings;

/// <summary>
/// 应用设置。
/// </summary>
/// <remarks>
/// 纯数据对象，直接读写 JSON。默认值即"开箱可用"的配置，
/// 首次运行不写文件也能正常工作。
/// </remarks>
public sealed class AppSettings
{
    /// <summary>云联想总开关。</summary>
    public bool CloudAssistEnabled { get; set; } = true;

    /// <summary>
    /// 是否在本机记录输入统计。
    /// </summary>
    /// <remarks>
    /// 默认关闭：这是唯一会持续写本地文件的开关，属于用户应当明确同意的行为。
    /// 关闭只是停止累计，已有历史仍然保留，用户可以自行查看或清空。
    /// </remarks>
    public bool StatisticsEnabled { get; set; }

    /// <summary>是否按程序分别统计。关闭后只累计总数，不记录进程名。</summary>
    public bool StatisticsPerAppEnabled { get; set; } = true;

    /// <summary>统计保留天数；0 表示永久保留。</summary>
    public int StatisticsRetentionDays { get; set; } = 365;

    /// <summary>是否启用「云联想」（严格按音节数匹配）。</summary>
    public bool StrictEnabled { get; set; } = true;

    /// <summary>云联想的采纳键标识，默认 <c>Oem3</c>（即 <c>·</c> 键）。</summary>
    public string StrictAcceptKey { get; set; } = "Oem3";

    /// <summary>云联想命中本地已有候选时是否隐藏。</summary>
    public bool StrictDedupeLocal { get; set; } = true;

    /// <summary>
    /// 是否启用「扩展云联想」（输入是前缀的更长词）。
    /// </summary>
    /// <remarks>
    /// 默认关闭：扩展词更像「猜你想搜」，命中率比整词低，首次上手先只给云联想更干净。
    /// 需要的人可以在云联想页自己打开，键位与条数都有默认值。
    /// </remarks>
    public bool ExtendedEnabled { get; set; }

    /// <summary>扩展云联想最多显示几条（1~4）。</summary>
    public int ExtendedMaxCount { get; set; } = 3;

    /// <summary>扩展键位分配方式：<c>Auto</c> 接在本地候选之后，<c>Manual</c> 用 <see cref="ExtendedKeys"/>。</summary>
    public string ExtendedKeyMode { get; set; } = "Auto";

    /// <summary>
    /// 手动指定的扩展采纳键，逗号分隔的 WPF Key 标识，例如 <c>D6,D7,D8,D9</c>。
    /// </summary>
    /// <remarks>
    /// 旧版本存的是数字串 <c>6789</c>，读取时仍兼容；新写入统一带上 <c>D</c> 前缀，
    /// 这样同一个字段也能放功能键、小键盘键与标点。
    /// </remarks>
    public string ExtendedKeys { get; set; } = "D6,D7,D8,D9";

    /// <summary>浮窗位置：<c>Below</c> 候选窗下方，<c>Left</c> 候选窗左侧。</summary>
    public string OverlayPlacement { get; set; } = "Below";

    /// <summary>浮窗样式：<c>Card</c> 纯白卡片，<c>Acrylic</c> 亚克力。</summary>
    public string PopupStyle { get; set; } = "Card";

    /// <summary>是否把用户词库的命中显示在浮窗里。</summary>
    public bool WordbookEnabled { get; set; } = true;

    /// <summary>采纳云联想后是否自动记入用户词库。</summary>
    public bool WordbookLearnOnAccept { get; set; } = true;

    /// <summary>用户词库整词命中时是否跳过联网，直接出词。</summary>
    public bool WordbookLocalFirst { get; set; } = true;

    /// <summary>采纳云词后是否立即写进微软拼音的自定义短语。</summary>
    /// <remarks>
    /// 换过名字（旧值 <c>WordbookAutoSync</c> 默认关，导致功能不可见）。
    /// 新键在旧配置里不存在，因此升级后按默认值 true 生效。
    /// </remarks>
    public bool PhraseAutoWrite { get; set; } = true;

    /// <summary>
    /// 扩展云联想的词采纳后是否也写进自定义短语。
    /// </summary>
    /// <remarks>
    /// 默认关闭。扩展候选多是「伊斯塔路是谁」这类整句，写进短语库会长期占住原生候选的第一位，
    /// 而这类词往往只用一次；严格云联想（单个词）不受影响，仍然照常写入。
    /// 关闭时连用户词库也不记：只学不写会留下「待同步」，下次启动会被补写，开关就形同虚设。
    /// </remarks>
    public bool PhraseWriteExtended { get; set; }

    /// <summary>
    /// 写入自定义短语时是否同时补一条简拼记录，使得打首字母也能出词。
    /// </summary>
    /// <remarks>
    /// 输入法只按「拼音」字段做纯字符串匹配，不会自己从 <c>yisitalu</c> 推出 <c>ystl</c>，
    /// 所以要让简拼也能出词，必须再写一条拼音为简拼、文本相同的记录。
    /// </remarks>
    public bool PhraseWriteInitials { get; set; } = true;

    /// <summary>写入微软拼音时的默认候选位置（1~9）。</summary>
    public int WordbookSyncPosition { get; set; } = 2;

    /// <summary>配色主题：<c>System</c> 跟随系统，<c>Light</c> 浅色，<c>Dark</c> 深色。</summary>
    public string ThemeMode { get; set; } = "System";

    /// <summary>
    /// 设置窗口的窗口材质：<c>Auto</c> 跟随系统能力（Windows 11 22H2 及以上用 Mica，其余纯色），
    /// <c>Solid</c> 始终纯色。
    /// </summary>
    /// <remarks>
    /// 只影响设置窗口。候选浮窗始终按自己的 <see cref="PopupStyle"/> 走亚克力或实色，
    /// 因为它贴在候选条旁边，纯色会显得比输入法本身更"重"。
    /// </remarks>
    public string BackdropMode { get; set; } = "Auto";

    /// <summary>是否启用自定义窗口背景图。</summary>
    public bool BackgroundImageEnabled { get; set; }

    /// <summary>背景图绝对路径；为空表示未设置。</summary>
    public string? BackgroundImagePath { get; set; }

    /// <summary>背景图不透明度（0.05~1.0）。越小文字越清晰。</summary>
    public double BackgroundImageOpacity { get; set; } = 0.35;

    /// <summary>英文状态下是否也联想（默认关，采纳会删除已输入的字母）。</summary>
    public bool EnglishModeAssistEnabled { get; set; }

    /// <summary>英文模式的采纳方式：<c>Replace</c> 删除字母后上屏，<c>Preview</c> 只提示不上屏。</summary>
    public string EnglishModeCommit { get; set; } = "Replace";

    /// <summary>触发云查询的最小输入长度。</summary>
    public int MinQueryLength { get; set; } = 2;

    /// <summary>停止输入后等待多久再发请求（毫秒）。太短会浪费请求，太长会让用户觉得慢。</summary>
    public int DebounceMs { get; set; } = 120;

    /// <summary>单次云端请求超时（毫秒）。</summary>
    public int RequestTimeoutMs { get; set; } = 1500;

    /// <summary>是否按模糊音规则做读音校验。</summary>
    public bool FuzzyPinyin { get; set; } = true;

    /// <summary>用户是否使用双拼。开启时云联想自动停用（按键串不是全拼）。</summary>
    public bool DoublePinyinMode { get; set; }

    /// <summary>搜索市场。</summary>
    public string Market { get; set; } = "zh-CN";

    /// <summary>端点策略：<c>auto</c> 自动选择，<c>manual</c> 使用 <see cref="ManualEndpointId"/>。</summary>
    public string EndpointMode { get; set; } = "auto";

    /// <summary>手动指定的端点标识。</summary>
    public string? ManualEndpointId { get; set; }

    /// <summary>代理策略：<c>direct</c> 直连，<c>system</c> 跟随系统，<c>custom</c> 自定义。</summary>
    public string ProxyMode { get; set; } = "system";

    /// <summary>自定义代理地址，形如 <c>127.0.0.1:7890</c>。</summary>
    public string? ProxyAddress { get; set; }

    /// <summary>命中缓存的有效期（分钟）。</summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary>浮窗是否显示云图标。</summary>
    public bool ShowCloudIcon { get; set; } = true;

    /// <summary>是否随系统启动。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// 是否在启动时检查更新。
    /// </summary>
    /// <remarks>
    /// 默认开启：只读 GitHub 的公开发布信息，每天最多一次，失败静默。
    /// 这也是唯一会主动访问第三方接口的非功能请求，所以留了开关，关掉后完全不出网。
    /// </remarks>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>上次成功检查更新的时间（ISO 8601，UTC）；为空表示还没查过。</summary>
    public string? LastUpdateCheckUtc { get; set; }

    /// <summary>不启用云联想的进程名（不含 .exe，忽略大小写）。</summary>
    public List<string> BlockedProcesses { get; set; } = new();

    /// <summary>深拷贝，用于设置页"取消"时回滚。</summary>
    /// <returns>副本。</returns>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();
}

/// <summary>
/// 设置的读写与持久化。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;

    /// <summary>创建存储。</summary>
    /// <param name="filePath">配置文件路径；传 null 使用 <c>%APPDATA%\BetterPinyin\settings.json</c>。</param>
    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterPinyin",
            "settings.json");
    }

    /// <summary>配置文件路径。</summary>
    public string FilePath => _filePath;

    /// <summary>最近一次读写失败的原因，便于诊断页展示。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 读取设置。文件不存在或损坏时返回默认值，不抛异常。
    /// </summary>
    /// <returns>设置对象。</returns>
    public AppSettings Load()
    {
        LastError = null;

        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 配置损坏不能让程序起不来，退回默认值并把原因留给诊断页。
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return new AppSettings();
        }
    }

    /// <summary>
    /// 保存设置。
    /// </summary>
    /// <param name="settings">要保存的设置。</param>
    /// <returns>保存成功返回 true。</returns>
    public bool Save(AppSettings settings)
    {
        LastError = null;

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, SerializerOptions);

            // 先写临时文件再替换，避免断电或崩溃留下半个文件。
            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}

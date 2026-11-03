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

    /// <summary>是否启用「云联想」（严格按音节数匹配）。</summary>
    public bool StrictEnabled { get; set; } = true;

    /// <summary>云联想的采纳键标识，默认 <c>Oem3</c>（即 <c>·</c> 键）。</summary>
    public string StrictAcceptKey { get; set; } = "Oem3";

    /// <summary>云联想命中本地已有候选时是否隐藏。</summary>
    public bool StrictDedupeLocal { get; set; } = true;

    /// <summary>是否启用「扩展云联想」（输入是前缀的更长词）。</summary>
    public bool ExtendedEnabled { get; set; } = true;

    /// <summary>扩展云联想最多显示几条（1~4）。</summary>
    public int ExtendedMaxCount { get; set; } = 3;

    /// <summary>扩展键位分配方式：<c>Auto</c> 接在本地候选之后，<c>Manual</c> 用 <see cref="ExtendedKeys"/>。</summary>
    public string ExtendedKeyMode { get; set; } = "Auto";

    /// <summary>手动指定的扩展采纳键，例如 <c>6789</c>。</summary>
    public string ExtendedKeys { get; set; } = "6789";

    /// <summary>浮窗位置：<c>Below</c> 候选窗下方，<c>Left</c> 候选窗左侧。</summary>
    public string OverlayPlacement { get; set; } = "Below";

    /// <summary>浮窗样式：<c>Card</c> 纯白卡片，<c>Acrylic</c> 亚克力。</summary>
    public string PopupStyle { get; set; } = "Card";

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

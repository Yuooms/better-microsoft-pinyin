using BetterPinyin.App.Features.CloudAssist;
using BetterPinyin.Core.Settings;
using BetterPinyin.Interop.Uia;

namespace BetterPinyin.App;

/// <summary>
/// 进程内的共享服务定位器。
/// </summary>
/// <remarks>
/// 这个程序规模不大，用一个显式的静态容器比引入完整的依赖注入框架更直接，
/// 也避免为了 DI 而增加第三方依赖。所有字段在 <see cref="Initialize"/> 中一次性装配。
/// </remarks>
internal static class AppServices
{
    private static SettingsStore? _store;

    /// <summary>当前设置。修改后调用 <see cref="SaveSettings"/> 落盘。</summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>设置读写器。</summary>
    public static SettingsStore Store => _store ??= new SettingsStore();

    /// <summary>UIA 专用 STA 线程。</summary>
    public static UiaWorker Uia { get; private set; } = null!;

    /// <summary>候选窗读取器。</summary>
    public static UiaCandidateWindowReader Reader { get; private set; } = null!;

    /// <summary>云联想控制器（功能启动后可用）。</summary>
    public static CloudAssistController? Controller { get; set; }

    /// <summary>UIA 是否可用，供诊断页展示。</summary>
    public static bool UiaAvailable { get; set; }

    /// <summary>装配共享服务。</summary>
    public static void Initialize()
    {
        Settings = Store.Load();

        Uia = new UiaWorker();
        Reader = new UiaCandidateWindowReader(Uia);
        UiaAvailable = Reader.Initialize();
    }

    /// <summary>保存设置到磁盘。</summary>
    /// <returns>保存成功返回 true。</returns>
    public static bool SaveSettings() => Store.Save(Settings);

    /// <summary>释放 UIA 线程。</summary>
    public static void Shutdown()
    {
        Controller = null;
        Uia?.Dispose();
    }
}

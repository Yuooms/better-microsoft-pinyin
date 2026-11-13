# 新增一个功能模块

目标：加一个功能（例如「输入统计」「快捷短语」），在设置里自动出现独立页面，不需要改动外壳代码。

## 三步

### 1. 实现 `IFeatureModule`

```csharp
using BetterPinyin.Core.Features;
using BetterPinyin.Core.Settings;

namespace BetterPinyin.App.Features.WordCount;

internal sealed class WordCountFeature : IFeatureModule
{
    private readonly AppSettings _settings;

    public WordCountFeature(AppSettings settings)
    {
        _settings = settings;
        SettingsPages = new ISettingsPage[] { new WordCountSettingsPage(this) };
    }

    public string Id => "word-count";

    public string DisplayName => "输入统计";

    public string Description => "统计每天输入的字数";

    public bool IsEnabled
    {
        get => _settings.WordCountEnabled;   // 在 AppSettings 里加这个字段
        set => _settings.WordCountEnabled = value;
    }

    // 注意：IsEnabled 只是配置代理。框架只在启动时按它决定要不要 StartAsync，
    // 运行期改它不会重新走一遍生命周期。要让开关当场生效，自己加一个 SetEnabled：
    //
    // public void SetEnabled(bool enabled)
    // {
    //     if (enabled == _started) return;
    //     if (enabled) _ = StartAsync(CancellationToken.None);
    //     else _ = StopAsync();
    // }

    public IReadOnlyList<ISettingsPage> SettingsPages { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 在这里启动你的逻辑：订阅事件、开定时器、装钩子……
        // 注意：不要阻塞，异常要自己吞掉并记日志，别让一个模块拖垮整个程序。
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // 释放资源，保证卸载后系统状态回到原样。
        return Task.CompletedTask;
    }
}
```

### 2. 声明设置页

```csharp
internal sealed class WordCountSettingsPage : ISettingsPage
{
    private readonly WordCountFeature _feature;

    public WordCountSettingsPage(WordCountFeature feature) => _feature = feature;

    public string Id => "word-count.settings";
    public string Title => "输入统计";
    public string Glyph => "\uE9D9";          // Segoe MDL2 字形，外壳会映射到 Fluent 图标
    public string? Description => "统计口径与重置";

    public Type ViewType => typeof(WordCountPage);

    public object CreateView() => new WordCountPage(_feature);
}
```

`CreateView()` 返回 `object`、`ViewType` 只声明类型，都是为了让 `Core` 不依赖 WPF：
具体类型由 App 层提供。`ViewType` 同时是导航键与页面缓存的键——
`SettingsPageProvider` 保证同一页面只创建一次，切走再切回时输入不丢。

### 3. 注册

在 `App.OnStartup` 里加一行：

```csharp
_registry.Register(new WordCountFeature(AppServices.Settings));
```

左侧导航会自动多出「输入统计」一页，不需要改 `SettingsWindow`。

## 设置页写法

参考 `src/BetterPinyin.App/Features/CloudAssist/Views/CloudAssistPage.xaml(.cs)`：

- 页面继承 `Page`，根节点加 `ScrollViewer.CanContentScroll="False"`（阻止 WPF-UI 再套一层滚动容器）。
- 页面实例会被缓存，`Loaded` 可能触发多次：只有第一次读设置，否则会覆盖用户未保存的修改。
- 「保存」时写回设置对象 → `AppServices.SaveSettings()` → 调用模块自己的 `ApplySettings()`。
- 需要实时日志时订阅模块事件，在 `Unloaded` 里退订；再 `Loaded` 时先减后加，避免重复订阅。

## 检查清单

- [ ] `Id` 唯一，且不与已有模块重复（`FeatureRegistry` 会抛异常提示）。
- [ ] `StartAsync` 不阻塞、不抛异常；失败要让功能降级而不是让程序起不来。
- [ ] `StopAsync` 能真正释放资源，卸载后系统状态与安装前一致。
- [ ] 设置项都有默认值，首次运行不写配置文件也能工作。
- [ ] 涉及网络/跨进程的操作都带超时。
- [ ] 面向用户的文案是中文，并且说明了该功能会做什么。
- [ ] README 与相关 docs 同步更新（功能清单、排障、路线图），别让文档停在设计稿阶段。

## 反面例子

- 在 `Core` 里 `using System.Windows;` —— 分层被破坏，`Core` 将无法脱离桌面测试。
- 在键盘钩子回调里做网络请求或 UIA 调用 —— 整个系统的键盘输入都会被拖慢。
- 在 `StartAsync` 里 `Thread.Sleep` 或 `.Result` —— 启动画面会卡住。
- 新功能默认开启且会修改用户数据 —— 应默认关闭或明确告知。

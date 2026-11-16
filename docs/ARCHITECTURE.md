# 架构说明

## 一句话概括

**引擎与接入方式彻底解耦**：云联想的价值在于「查什么、留哪个、怎么排」，这些与「候选最终显示在输入法的哪个槽位」无关。因此业务逻辑全部放在 `Core`，接入方式实现 `IInputIntegration` 并声明自己的能力。

## 分层

```
┌──────────────────────────────────────────────────────────┐
│ BetterPinyin.App (WPF)                                    │
│  托盘 / 设置窗口（导航自动生成） / 云候选浮窗               │
│  词库页 / 统计页 / 关于页（含更新）                         │
├──────────────────────────────────────────────────────────┤
│ Features/CloudAssist  ← 一个 IFeatureModule 实现           │
│  CloudAssistController：按键→查询→排序→展示→上屏          │
│ Features/Statistics   ← 另一个 IFeatureModule              │
├─────────────────────────────┬────────────────────────────┤
│ BetterPinyin.Core           │ BetterPinyin.Interop        │
│  Bing 客户端 / 响应解析      │  KeyboardHub（钩子 + 组字状态机）│
│  候选清洗 / 读音校验 / 排序   │  SendInput / 光标定位         │
│  端点体检 / 设置 / 模块框架   │  UIA 候选窗读取（STA 线程）   │
│  统计聚合 / 词库 / 版本解析   │  IME 词库文件读写             │
└─────────────────────────────┴────────────────────────────┘
            ↑ 双方都只依赖 ↓
┌──────────────────────────────────────────────────────────┐
│ BetterPinyin.Contracts                                    │
│  IInputIntegration / IntegrationCapabilities / 跨进程消息  │
└──────────────────────────────────────────────────────────┘
```

依赖方向单向：`App → Core → Contracts`，`App → Interop → Contracts`。`Core` 不引用任何 Win32 或 UI 类型，因此可以脱离 Windows 桌面单独测试。

## 一次输入的完整链路

```
用户敲 y
  │
  ├─ KeyboardHub（WH_KEYBOARD_LL，UI 线程，只做过滤与转交）
  │     ├→ Filter（云联想）：命中 ActivationMap → 吞键 → 异步提交
  │     ├→ 未被吞 → CompositionTracker：拼成 "yisitalu"，带自增代次号
  │     └→ Observed（输入统计）：只读，拿到的是 Handled 的最终值
  │
  ├─ 防抖 120ms（用户还在打字就取消上一次查询）
  │
  ├─ UiaCandidateWindowReader：先读候选窗
  │     ├→ 可见 → 中文模式，拿到本地候选（去重 + 数字键基准）与矩形（定位）
  │     └→ 不可见 → 英文模式（开关打开且输入能切成音节）或直接放弃
  │
  ├─ 简拼展开：无法完整切分时查词库，把 ystl 展开成 yisitalu
  │
  ├─ 云词源（严格槽用所选来源，扩展槽固定 Bing）
  │     ├→ BingSuggestionClient：qsonhs（api → cn → www）→ 旧 osjson 兜底（限 500ms）
  │     ├→ SogouSuggestionClient：单端点，二进制帧，失败即空
  │     └→ 两条请求并行，返回原始候选文本 + 本次生效的来源
  │
  ├─ ResultSanitizer：截断整句、限长、只留纯汉字、去重
  ├─ PinyinMatcher：候选驱动 DP，算出「整词 / 末音节未打完 / 简拼 / 扩展」
  ├─ SuggestionSelector：严格槽取第一条整词，扩展槽取前缀更长的词；
  │    双源模式下用 Combine 把两次分区拼成一个展示集
  │
  ├─ ActivationMap：按键 → 采纳目标（严格 = ·，扩展 = 本地候选之后的数字）
  ├─ CloudCandidateWindow：贴在候选条下沿或左侧，不抢焦点
  │
  └─ 用户按 · 或 6
        ├→ 数字键复核：本地候选数已经占到这个编号 → 把按键注入回去
        ├→ SendInput(Esc) 取消组字
        ├→ 等候选窗消失（≤140ms，未消失再补一次 Esc）
        └→ SendInput Unicode 逐字上屏
```

### 为什么用按键重建拼音，而不是直接读组字串

微软拼音是 TSF 输入法，组字串保存在**目标应用进程**的 TSF 上下文里，跨进程没有任何受支持的读取方式。而中文模式下用户敲的字母是可观测的，因此用「按键 + 候选窗是否存在」共同判定组字状态，是当前最稳的组合。

代价：双拼用户的按键串不是全拼，所以双拼模式直接停用本功能（设置里有开关）。

### 为什么浮窗要贴着候选条

候选条位于沉浸式输入法的独立 window band，层级永远高于普通置顶窗口，因此浮窗**不可能盖住它**，只能排在其下方；下方放不下时自动翻到上方。

## 关键设计决定

| 决定 | 理由 |
|---|---|
| 候选窗用 `WindowFromPoint` 发现，不用 `EnumWindows` | 候选条在独立 window band，枚举 API 拿不到；按进程名能枚举到的那个是全屏宿主窗口，且被 DWM 隐身，UIA 子树为空 |
| 读音校验用「候选驱动 DP」而不是先切分输入 | 输入的切分本来就不唯一（`xian` 既是「先」也是「西安」），先切分必然丢解 |
| 模糊音用拼写变体表，不整串 Replace | 整串归一会让音节边界错位，且切不出 `zuang` 这类手写变体 |
| 严格槽被本地去重后不再往下找 | 用户要的是「补一个我还没有的词」，换个同音字只会更困惑 |
| 键位映射在查询完成时构建，钩子只查表 | 钩子回调必须 O(1) 且无 I/O，否则整机输入会被拖慢 |
| 排序权重写死在代码里、条数开放到设置 | 权重属于产品调性，不该让用户面对一堆旋钮；条数是真实偏好，开放 |
| 设置窗口导航由模块注册表生成 | 新增功能不需要改外壳，符合"方便后续扩展"的要求 |
| 一处失败只降级、不打断 | 云端失败、UIA 失败、去重失败都不影响输入法与上屏；底线是"读不到也不影响打字" |
| 严格槽来源可切换，扩展槽固定 Bing | 搜狗对新人名/新词命中更好但只给整词；扩展词本来就要靠搜索引擎的前缀联想 |
| 双源并发而不是串行 | 两个来源互不依赖，串行会把延迟相加；只有扩展开启时才请求 Bing，关了就不发 |

## 扩展点

### 1. 新增一个功能模块

实现 `IFeatureModule` 并注册，设置窗口自动出现对应页面。详见 [FEATURE-GUIDE.md](FEATURE-GUIDE.md)。

### 2. 新增一个云词源

实现 `ICloudSuggestionProvider`：只负责"给定输入串返回原始候选文本 + 本次生效的来源"，清洗、校验、排序、缓存都在外层复用。随后在 `CloudSourceCatalog` 登记 id、在设置页的来源下拉加一项即可。

协议不是 JSON 时，照 `Cloud/Sogou/` 的拆法：组帧（`SogouFrame`）与解帧（`SogouResponseParser`）各自是纯函数，解析层任何输入都不许抛异常，客户端只负责 HTTP 与超时。

### 3. 接入更多输入法

`IInputIntegration` 已经抽象出能力模型。当前只实现了外挂浮窗路线（`CanInjectNativeCandidate = false`）。未来若要支持"把云词写进输入法自己的候选槽位"，新增一个实现即可，`CloudAssistController` 与 `Core` 无需改动。技术前置见 [ROADMAP.md](ROADMAP.md)。

## 自更新

更新不属于 `IFeatureModule`：它没有常驻功能，也不需要自己的导航页，只挂在「关于」页与启动流程上。

```
关于页 [检查更新] ──┐
启动时（每天≤1 次）─┴→ UpdateService.CheckAsync → GitHub releases/latest
                                                 │
                  有正式版更新 → 只提示（关于页显示 [下载更新] / 托盘气泡），不自动下载
                                                 │
                     用户点 [下载更新] → DownloadAsync：%LOCALAPPDATA%\BetterPinyin\updates\<tag> 下载 + 解压
                                                 │
                     用户点 [安装并重启] → TryApplyAndRestart：写 apply-update.cmd，起 cmd.exe，然后退出
                                                 │
                     cmd 等本进程退出 → robocopy 覆盖安装目录 → 重启 → 自删
```

版本解析与替换脚本的文本放在 `Core/Updates`（可单测），网络、文件与进程操作放在 `App/Updates`。为什么绕一个批处理、为什么脚本正文必须纯 ASCII，见
[TECH-NOTES 第二十一节](TECH-NOTES.md)。

## 线程模型

| 组件 | 线程 | 约束 |
|---|---|---|
| 键盘钩子（`KeyboardHub`） | 安装它的线程（UI 线程） | 回调内禁止任何 I/O 与跨进程调用，否则整机输入会卡顿；订阅方异常一律吞掉 |
| 输入统计 | 同上（钩子回调里）+ 后台 `Timer` 落盘 | 回调里只做整数加法；快照与写文件都离开钩子线程 |
| 云端查询 | 线程池 | 结果回投时必须校验组字代次，代次不符直接丢弃 |
| 采纳上屏 | 线程池（`Task.Run`） | 里面有同步 UIA 读取；留在钩子线程会把整机键盘堵住约 1 秒。提交窗口内吞掉非修饰键 |
| UIA 读取 | 专用 STA 线程（`UiaWorker`） | 串行执行 + 带超时；超时后放弃结果，不阻塞调用方 |
| 浮窗 | UI 线程 | 事件从后台线程回投时经 `Dispatcher` 切回 |
| 更新检查 | 线程池，结果回投 UI 线程 | 启动时后台跑，不能挡住第一帧；写 `LastUpdateCheckUtc` 与弹气泡都在 UI 线程 |
| 更新替换 | 独立的 `cmd.exe` | 不再依附本进程，等本进程退出后才能覆盖 exe |

## 外观与主题

```
src/BetterPinyin.App/Themes/
  Colors.Light.xaml / Colors.Dark.xaml   候选浮窗与托盘的配色，索引 0 可整体替换
  Metrics.xaml                           浮窗与旧控件的度量
  Controls.xaml                          浮窗与旧页面的控件模板
src/BetterPinyin.App/Shell/
  ThemeManager.cs                        主题状态、浮窗配色切换、启动自检
  SystemTheme.cs                         读「应用模式」并监听系统变化
  FluentThemeBridge.cs                   把同一份深浅色状态同步给 WPF-UI
  SettingsWindow.xaml                    FluentWindow + TitleBar + NavigationView
  SettingsPageProvider.cs                页面工厂与缓存（切页保留输入）
```

设置窗口的外观由 **WPF-UI 4.3.0** 负责：`ui:FluentWindow` + `ui:TitleBar` +
`ui:NavigationView`。窗口材质走 WPF-UI 的 `WindowBackdropType`：Windows 11 22H2
及以上用 Mica，其余（含 Windows 10）保持 `None` 并用纯色底。材质与可读性分开处理——
`MaterialTint` 层用 60% 的 `WindowGlassBrush` 着色，`NavigationViewContentBackground`
在内容区叠 85% 的 `ContentGlassBrush`（有背景图时让开）。为什么不用自己调
`DwmSetWindowAttribute`，见 [TECH-NOTES 第二十二节](TECH-NOTES.md)。

三条约束：

1. **浮窗的颜色一律 `DynamicResource`**。切主题是整字典替换，静态引用不会刷新；
   设置窗口则用 WPF-UI 的 `ThemeResource` / `DynamicResource`，由主题字典驱动。
2. **`Controls.xaml` 里不许出现跨字典的 `StaticResource`**，解析期找不到键会直接抛异常。
3. **不要让 WPF-UI 接管候选浮窗**。浮窗是无焦点、点击穿透的透明窗口，
   WPF-UI 的隐式 `Window` 样式会覆盖 `AllowsTransparency`；浮窗的两种样式
   （纯白卡片 / 经典）都由 WPF 自绘，不依赖系统材质。

## 配置与数据

| 内容 | 位置 |
|---|---|
| 设置 | `%APPDATA%\BetterPinyin\settings.json` |
| 用户词库 | `%APPDATA%\BetterPinyin\words.json`（采纳过哪些云词，用于浮窗排序与回写短语） |
| 输入统计 | `%APPDATA%\BetterPinyin\statistics.json`（默认关闭） |
| 日志 | `%LOCALAPPDATA%\BetterPinyin\logs\app.log`（超过 1MB 滚动） |
| 更新缓存 | `%LOCALAPPDATA%\BetterPinyin\updates\<tag>\`（下载包、解压结果与 `update.log`；安装完由脚本删除，也可以通过「关于」页手动清理） |

设置写入采用「临时文件 + 替换」，避免断电留下半个文件；读取失败退回默认值并在诊断页显示原因。

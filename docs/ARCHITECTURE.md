# 架构说明

## 一句话概括

**引擎与接入方式彻底解耦**：云联想的价值在于「查什么、留哪个、怎么排」，这些与「候选最终显示在输入法的哪个槽位」无关。因此业务逻辑全部放在 `Core`，接入方式实现 `IInputIntegration` 并声明自己的能力。

## 分层

```
┌──────────────────────────────────────────────────────────┐
│ BetterPinyin.App (WPF)                                    │
│  托盘 / 设置窗口（导航自动生成） / 云候选浮窗              │
├──────────────────────────────────────────────────────────┤
│ Features/CloudAssist  ← 一个 IFeatureModule 实现           │
│  CloudAssistController：按键→查询→排序→展示→上屏          │
├─────────────────────────────┬────────────────────────────┤
│ BetterPinyin.Core           │ BetterPinyin.Interop        │
│  Bing 客户端 / 响应解析      │  低级键盘钩子                │
│  候选清洗 / 读音校验 / 排序   │  SendInput / 光标定位         │
│  端点体检 / 设置 / 模块框架   │  UIA 候选窗读取（STA 线程）   │
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
  ├─ WH_KEYBOARD_LL 钩子（UI 线程，只做过滤与转交）
  │     ├→ 命中 ActivationMap：吞键 → 异步提交（不发网络、不读 UIA）
  │     └→ 否则 CompositionTracker：拼成 "yisitalu"，带自增代次号
  │
  ├─ 防抖 120ms（用户还在打字就取消上一次查询）
  │
  ├─ UiaCandidateWindowReader：先读候选窗
  │     ├→ 可见 → 中文模式，拿到本地候选（去重 + 数字键基准）与矩形（定位）
  │     └→ 不可见 → 英文模式（开关打开且输入能切成音节）或直接放弃
  │
  ├─ 简拼展开：无法完整切分时查词库，把 ystl 展开成 yisitalu
  │
  ├─ BingSuggestionClient
  │     ├→ 端点 A(osjson) 失败 → 端点 B(qsonhs) → …
  │     └→ 返回原始候选文本
  │
  ├─ ResultSanitizer：截断整句、限长、只留纯汉字、去重
  ├─ PinyinMatcher：候选驱动 DP，算出「整词 / 末音节未打完 / 简拼 / 扩展」
  ├─ SuggestionSelector：严格槽取第一条整词，扩展槽取前缀更长的词
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

## 扩展点

### 1. 新增一个功能模块

实现 `IFeatureModule` 并注册，设置窗口自动出现对应页面。详见 [FEATURE-GUIDE.md](FEATURE-GUIDE.md)。

### 2. 新增一个云词源

实现 `ICloudSuggestionProvider`：只负责"给定输入串返回原始候选文本"，清洗、校验、排序、缓存都在外层复用。

### 3. 接入更多输入法

`IInputIntegration` 已经抽象出能力模型。当前只实现了外挂浮窗路线（`CanInjectNativeCandidate = false`）。未来若要支持"把云词写进输入法自己的候选槽位"，新增一个实现即可，`CloudAssistController` 与 `Core` 无需改动。技术前置见 [ROADMAP.md](ROADMAP.md)。

## 线程模型

| 组件 | 线程 | 约束 |
|---|---|---|
| 键盘钩子 | 安装它的线程（UI 线程） | 回调内禁止任何 I/O 与跨进程调用，否则整机输入会卡顿 |
| 云端查询 | 线程池 | 结果回投时必须校验组字代次，代次不符直接丢弃 |
| 采纳上屏 | 线程池（`Task.Run`） | 里面有同步 UIA 读取；留在钩子线程会把整机键盘堵住约 1 秒。提交窗口内吞掉非修饰键 |
| UIA 读取 | 专用 STA 线程（`UiaWorker`） | 串行执行 + 带超时；超时后放弃结果，不阻塞调用方 |
| 浮窗 | UI 线程 | 事件从后台线程回投时经 `Dispatcher` 切回 |

## 外观与主题

```
src/BetterPinyin.App/Themes/
  Colors.Light.xaml / Colors.Dark.xaml   键名完全一致的配色，索引 0 可整体替换
  Metrics.xaml                           圆角/控件高/行高，启动时按系统版本调整
  Controls.xaml                          七个控件模板 + SettingRow/Card
src/BetterPinyin.App/Shell/
  ThemeManager.cs                        主题切换、资源解析、启动自检
  SystemTheme.cs                         读「应用模式」并监听系统变化
  ShellChrome.cs                         窗口背景与标题栏的唯一入口
```

三条约束：

1. **颜色一律 `DynamicResource`**。切主题是整字典替换，静态引用不会刷新；代码里赋过的画刷也不会。
2. **`Controls.xaml` 里不许出现跨字典的 `StaticResource`**，解析期找不到键会直接抛异常。
3. **背景与标题栏只在 `ShellChrome` 里决定**。Win11 走 `DWMWA_SYSTEMBACKDROP_TYPE`（设置窗口 Mica、浮窗 Acrylic），Win10 默认纯色；失败时回滚合成状态并铺实色。

## 配置与数据

| 内容 | 位置 |
|---|---|
| 设置 | `%APPDATA%\BetterPinyin\settings.json` |
| 日志 | `%LOCALAPPDATA%\BetterPinyin\logs\app.log`（超过 1MB 滚动） |

设置写入采用「临时文件 + 替换」，避免断电留下半个文件；读取失败退回默认值并在诊断页显示原因。

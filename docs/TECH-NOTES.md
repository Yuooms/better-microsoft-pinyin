# 技术笔记

记录开发过程中实测得到的事实与踩过的坑。这些结论大多无法从文档直接查到，改动相关代码前建议先读一遍。

## 一、候选窗：为什么枚举不到

微软拼音的候选条**不在普通桌面的窗口树里**。实测（Windows 10 19045）：

| 做法 | 结果 |
|---|---|
| `EnumWindows` 按进程名找 `TextInputHost` | 只有 1 个：`Windows.UI.Core.CoreWindow`，矩形 `(0,0) 1920x1080`，`DWMWA_CLOAKED = 1` |
| 对它做 `ElementFromHandle` + `FindAll(Descendants)` | **0 个元素**（因为它是被隐身的全屏宿主窗口） |
| 在插入符下方 `WindowFromPoint` | 命中真正的候选条：`(694,351) 602x45`，`cloaked = 0` |

**结论**：候选条位于沉浸式输入法的独立 window band，枚举类 API 一律无效，只能按坐标命中。曾因为误判"UIA 读不到"，方向跑偏过一轮 —— 判断依据必须同时看**矩形尺寸**和**是否 cloaked**，不能只看类名。

## 二、候选窗的 UIA 结构（实测）

```
type=50033 offscreen=0 rect=(694,351,602x45) id="IME_Prediction_Window" name="Microsoft 候选项 UI"
type=50007 offscreen=0 rect=(695,352,115x43) id="" name="一是他路"
type=50020 offscreen=0 rect=(695,352,23x43)  id="TEMPLATE_PART_CandidateItemIndex" name="1"
type=50020 offscreen=0 rect=(718,352,92x43)  id="TEMPLATE_PART_CandidateItemText"  name="一是他路"
type=50007 offscreen=0 rect=(810,352,97x43)  id="" name="伊斯塔"
...
type=50000 offscreen=0 rect=(1144,351,33x45) id="TEMPLATE_PART_PagePreviousButton" name="上一页"
type=50000 offscreen=0 rect=(1177,351,33x45) id="TEMPLATE_PART_PageNextButton"     name="下一页"
type=50000 offscreen=0 rect=(1210,351,49x45) id="TEMPLATE_PART_EmojiButton"        name="表情符号和符号"
type=50000 offscreen=0 rect=(1259,351,37x45) id="TEMPLATE_PART_searchSuggestionButton" name="搜索建议"
```

要点：

1. **容器 AutomationId 不一定是 `IME_Candidate_Window`**。实测是 `IME_Prediction_Window`。两者都要认，不能赌其中一个。
2. 候选项是 `ControlType = 50007`（ListItem），**`Name` 直接就是候选词文本**。
3. 每个候选项下还挂着 `TEMPLATE_PART_CandidateItemIndex`（序号）与 `TEMPLATE_PART_CandidateItemText`（文本）两个 Text 子元素 —— 按 `50007` 过滤就不会重复计数。
4. 容器矩形比候选项并集更准（含内边距），浮窗贴靠用它。
5. 翻页会**整树重建**，不要长期缓存 UIA 元素对象，每次读取重新查找。

## 三、坐标：三套体系别混

| 来源 | 坐标系 |
|---|---|
| UIA `BoundingRectangle` | 屏幕物理像素 |
| `GetGUIThreadInfo().rcCaret` | **所属窗口的客户区坐标**，必须 `ClientToScreen` |
| WPF `Window.Left/Top` | DIP（与 DPI 相关） |

浮窗定位全程用物理像素（`SetWindowPos`），避免 DIP 换算误差。进程在 `app.manifest` 中声明 PerMonitorV2。

## 四、键盘钩子的坑

1. **回调里什么都别做**。钩子在安装线程上执行，系统会等待它返回，任何 I/O 或跨进程调用都会让整机输入卡顿。
2. **委托必须保存成字段**，否则被 GC 回收后进程直接崩溃。
3. **自己注入的按键要能认出来**。用 `dwExtraInfo` 打标记（本项目的 `InjectionMarker`），只忽略带标记的按键；不要图省事忽略所有 `LLKHF_INJECTED`，否则自动化工具、远程桌面、部分宏键盘全都用不了。
4. **`INPUT` 结构在 x64 上是 40 字节**（联合体里最大的是 MOUSEINPUT）。用 `[StructLayout(LayoutKind.Explicit, Size = 40)]` 显式声明，否则 `SendInput` 会以"参数错误"静默失败。
5. Unicode 上屏要用 `KEYEVENTF_UNICODE`，字符放在 `wScan`、`wVk` 填 0。

## 五、上屏：为什么必须先等候选窗消失

`SendInput(Esc)` 之后立即发送 Unicode 文本，会出现「拼音与中文混杂」：Esc 是异步处理的，输入法还没取消组字，文本就已经注入。

正确顺序：

```
SendInput(Esc)
  → 轮询候选窗是否消失（每 20ms，上限 ~140ms）
  → 仍未消失：再发一次 Esc，再等 ~100ms
  → 仍未消失：放弃本次上屏（宁可不上，也不要上一半）
  → SendInput(Unicode 文本)
```

## 六、浮窗：绝不能抢焦点

三条样式缺一不可：

- `WS_EX_NOACTIVATE`：点击与显示都不激活窗口。
- `WS_EX_TOOLWINDOW`：不出现在任务栏与 Alt+Tab。
- `WS_EX_TRANSPARENT`：鼠标点击穿透，浮窗不挡下面的操作。

另外 WPF 侧要 `ShowActivated="False"`、`Focusable="False"`，且窗口内**不要放任何可交互控件**。

一旦浮窗抢到焦点，输入法会立刻提交当前组字 —— 用户还没按采纳键，拼音就已经上屏了。

## 七、后台进程抢不到前台

从托盘或后台启动的程序调用 `SetForegroundWindow` 会被系统拦下，窗口开在别的程序后面，用户会以为"点了没反应"。

解法：临时 `AttachThreadInput` 到当前前台线程，借用它的前台权限完成切换，再立刻解除附加。

## 八、网络：接口的几个实测特性

| 事实 | 影响 |
|---|---|
| `api.bing.com/osjson.aspx?query=x&market=zh-CN` 返回 `["x",["词1","词2"]]` | 首选格式，解析最简单 |
| `api.bing.com/qsonhs.aspx?type=cb&q=x` 即使不传 `cb` 也返回纯 JSON | 可作为第二解析路径交叉验证 |
| 响应可能带 JSONP 包装与尾部注释 `/* pageview_candidate */` | 解析前要截取首个 `{`/`[` 到最后一个 `}`/`]` |
| `cn.bing.com` 实测延迟更低，但结果经过内容过滤 | 可作降级源，但要给权重打折并在界面说明 |
| 纯缩写（`ystl`）直查返回的是拉丁垃圾（`ystl.jp`、`ystl stock`） | 必须靠"只留纯汉字"和读音校验过滤，否则会污染候选 |

## 九、工程细节

- **同时启用 WPF 与 WinForms 时**，SDK 会为两者注入全局 using，导致 `Application`、`UserControl`、`Brush`、`ComboBox` 全部二义。在 csproj 里 `<Using Remove="System.Windows.Forms" />`，只让托盘那个文件显式引入 WinForms。
- **UIA 事件需要消息泵**。当前 `UiaWorker` 是阻塞队列循环，收不到 UIA 事件；若将来要支持没有系统插入符的应用（Chromium/Electron），需要把它改成 `MsgWaitForMultipleObjects` + `GetMessage` 的混合循环。
- **托管 `System.Windows.Automation` 不要用**：某些应用发出的 UIA 事件 `runtimeId` 为 null，包装层会抛异常并可能终止进程。手写 COM 时把 `runtimeId` 声明成 `IntPtr` 且永不解引用即可。

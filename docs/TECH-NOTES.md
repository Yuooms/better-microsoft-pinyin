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

## 十、读音判定：为什么是候选驱动

最初的写法是「先把输入切成音节，再比较音节数」。这条路走不通，因为**输入的切分本来就不唯一**：

| 输入 | 唯一化切分 | 漏掉的可能 |
|---|---|---|
| `xian` | `[xian]` | 「西安」= `xi` + `an` |
| `fangan` | `[fang][an]` | 「反感」= `fan` + `gan` |
| `zuang` | 切不出来 | 模糊音用户手打的 `zhuang` |

现在的做法反过来：拿候选词每个字的**读音集合**当骨架，用输入从左往右覆盖它，只要存在一条走得通的路径就算匹配。DP 状态是 `(候选字下标, 输入位置)`，规模上限 16×33，单次匹配在几十微秒。

配套的三个设计：

1. **末音节残段只允许出现在候选最后一个音节**。`yisital` 能命中「伊斯塔露」，但 `ruonaw` 不会命中「若娜瓦壁纸」——否则打字打到一半就会冒出长词。
2. **模糊音不再是整串 Replace**。`zh→z` 之后再 `ng→n`，`an` 会变成 `al`，音节边界全乱。改为构建「拼写 → 规范音节 id 集合」的变体表，`zuang`、`lv`、`huar` 这些写法都能正确落到规范音节上。
3. **多音字用集合，不取首读音**。`银行` 同时接受 `yinhang` 与 `yinxing`；如果按首读音拼合，`yinhang` 会被误拒。

`ResultSanitizer` 只做粗筛（纯汉字、2~12 字、去重、回显剔除），真正的长度约束由音节数在匹配阶段完成。

## 十一、数字键拦截：三个必须处理的时序

扩展云联想用数字键采纳，键号接在本地候选之后（本地显示 5 个 → 6/7/8）。这条链路有三个坑：

1. **吞键后要连 keyup 一起吞**。只吞 keydown，目标窗口会收到孤立 keyup；按住不放时自动重复还会让正文冒出 `666`。用 `_swallowUntilKeyUp` 记录，直到抬起才移除。
2. **提交前必须复核**。从吞键到发 Esc 之间有几百毫秒，输入法可能补上了新候选。提交前再读一次候选窗：若该数字已经变成合法的本地编号，就放弃提交并把按键**注入回去**，让输入法自己选。
3. **不要在上屏前 `Reset()` 状态机**。`Reset()` 会递增代次，而提交前正好要用代次判断「组字有没有变化」，先 Reset 会让校验永远失败。正确做法是只在 `finally` 里 Reset。

另外，钩子回调里绝对不能做 UIA 或网络：映射表在查询完成时就构建好，钩子侧只做一次数组线性查找（最多 5 项）。

## 十二、亚克力：Win10 与 Win11 不是同一套 API

| 系统 | 方式 | 说明 |
|---|---|---|
| Windows 11 22621+ | `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)` | 公开接口，也就是系统「设置」用的那套预设 |
| Windows 10 1803+ | `SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)` | 未公开，但系统自身的设置、开始菜单用的也是它 |

两者都要求窗口**自己不要画不透明背景**：把 `CompositionTarget.BackgroundColor` 置为透明，并 `DwmExtendFrameIntoClientArea(-1)`，否则模糊会被 WPF 的绘制盖住。

两个容易踩的点：

- **分层窗口（`AllowsTransparency=True`）吃不到 DWM 模糊**。云浮窗的「亚克力」样式因此必须是非分层窗口，点击穿透改由 `WM_NCHITTEST` 返回 `HTTRANSPARENT` 实现；样式切换时只能重建窗口，WPF 不允许运行时改这个属性。
- **分层要跟系统一致**：系统「设置」只让侧边栏透出模糊，内容区接近不透明。整窗都用低透明度会让长文本在彩色壁纸上难以阅读。本项目的设置窗口采用同样的分层：导航 `#1FFFFFFF`，内容区 `#F7FFFFFF`。

失败时一律恢复实色背景并记日志——宁可看起来普通，也不要出现一块黑底或直接透出桌面。

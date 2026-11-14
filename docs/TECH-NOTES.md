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

## 十三、WPF 里怎么"用系统控件"

WPF 没有 Fluent 控件。它的默认模板（Aero2）是 2012 年的外观：方形勾选框、带渐变的按钮、
带独立箭头区的下拉框。系统「设置」用的是 UWP/WinUI 那一套。两者不是同一技术栈，
**没法"直接调用系统控件"**，只能按系统的实际尺寸和配色重绘模板。标题栏、滚动条之外的
系统菜单仍由 Windows 绘制。

重绘时最容易踩的是模板部件名——写错不报错，只是交互静默失效：

| 控件 | 必需部件 | 写错的后果 |
|---|---|---|
| `TextBox` | `PART_ContentHost`（类型必须是 `ScrollViewer`） | 文字完全不显示 |
| `ComboBox` | `PART_Popup` | 下拉永远打不开，且不抛异常 |
| `ScrollBar` | `PART_Track`，以及 `Track` 里的 `DecreaseRepeatButton` / `Thumb` / `IncreaseRepeatButton` | 没有滑块、不能翻页 |

另外几条经验：

1. **`CheckBox` 改成胶囊开关，但不要换控件类型**。`IsChecked`、Space 切换、Tab 聚焦、
   读屏名字全都来自类型本身，与模板无关。`ContentPresenter` 必须保留（可以 `Collapsed`），
   否则读屏软件读不到名字；文案则挪到设置行的标题里。
2. **`ComboBox` 内部必须重写 `ToggleButton` 的模板**，否则 Aero2 的灰色按钮会铺满整个下拉框，
   出现双层边框。`IsChecked` 绑 `IsDropDownOpen` 必须是 `TwoWay`；显示区要 `IsHitTestVisible=False`，
   否则点文字区域打不开。
3. **不要给 Popup 设 `StaysOpen=False`**。ComboBox 自己管理"点外面关闭"并同步状态，
   Popup 抢先关掉会让 `IsDropDownOpen` 残留为 true，表现为按钮一直高亮、再点一次没反应。
4. **下拉项用 `IsHighlighted` 而不是 `IsMouseOver`**，否则键盘上下移动时没有高亮，
   用户不知道回车会选哪一项。
5. **触发器里不要给颜色属性赋 `DynamicResource`**。只换字典、不换状态时，触发器不会重新求值。
   统一改成"前景色叠加层 + 只改 `Opacity`"，浅色下压暗、深色下提亮，一套代码通吃。
6. **列表里的勾选框要显式 `Padding="0"`**。WPF-UI 的 `CheckBox` 默认带 `11,5,11,6` 的
   内边距（给右侧文字留位置），放进 32px 宽的列里时，模板最外层的 `BulletDecorator`
   只剩下 8px，20px 的方框被裁掉右半边——看上去像半个圆角块，还没有报错。

## 十四、主题字典与动态资源

配色拆成 `Colors.Light.xaml` / `Colors.Dark.xaml` 两个键名完全一致的字典，固定在
`App.Resources.MergedDictionaries[0]`；切换主题时**整项替换**：

```csharp
dictionaries[0] = new ResourceDictionary { Source = new Uri("Themes/Colors.Dark.xaml", UriKind.Relative) };
```

只有整项替换才会触发 WPF 的失效链，让所有 `DynamicResource` 重新求值。改单个 key、
或往一个不在元素查找链上的字典里塞值，都不会刷新。

几条必须知道的边界：

- `Style="{StaticResource ...}"` 可以保留：样式对象本身不随主题变。
- **但颜色属性必须用 `DynamicResource`**，而且元素上的本地值优先级最高——
  代码里 `textBlock.Foreground = brush` 会让样式与动态资源一起失效，切主题后颜色停在旧值。
  代码生成的控件用 `SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush")`。
- **控件没有隐式 `Foreground` 样式**。隐式 `TextBlock` 样式不会作用到 `ContentPresenter`
  生成的文字上，所以 CheckBox / ComboBox / Button 的样式里都要显式设 `Foreground`，
  模板里再用 `TemplateBinding` 传给内容区。
- `DynamicResource` **找不到键不抛异常**，只是保留原值。表现为"某个字还是黑的"，极难排查。
  因此 `ThemeManager.Validate()` 在启动时把必需键逐个 `TryFindResource` 一遍，缺了就写日志。

深色下的两个取值细节：强调色用 `#60CDFF`（浅色的 `#005FB8` 在深色底上几乎看不见）；
亚克力着色用 `#99000000`（60% 黑），浅色的 45% 白在深色壁纸上会显得发灰。

标题栏要单独调 `DWMWA_USE_IMMERSIVE_DARK_MODE`（build ≥ 18985 用序号 20，否则 19），
并在之后补一次 `SetWindowPos(SWP_FRAMECHANGED)`，否则部分 19041/19042 不会重绘非客户区。

## 十五、Win10 亚克力为什么会让拖动卡住

`SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)` 配合
`DwmExtendFrameIntoClientArea(-1)` 与透明的 `CompositionTarget.BackgroundColor`，
等于告诉 DWM："整个客户区都是玻璃，背后的桌面要实时模糊。"

拖动时窗口下方的画面在变，DWM 就不能复用上一帧，必须每帧重新采样并重新高斯模糊整个窗口
（940×660 约 62 万像素）。Win11 的 `DWMWA_SYSTEMBACKDROP_TYPE` 是合成器内部的预设路径，
代价低得多；Win10 的 accent 是未公开实现，拖起来就是掉帧。

更不划算的是：满窗模糊里真正可见的只有左侧导航条那一窄条，内容区几乎把它盖住了。

因此设置窗口**已经放弃自研亚克力**，改用 WPF-UI 的 `FluentWindow`：
Win10 上是普通不透明窗口（`WindowBackdropType=None`），拖动走 DWM 的常规优化；
后来设置窗口的 Mica 就是在这条路线上做的，开关在「外观 → 窗口材质」，实现与坑见第二十二节。

`Backdrop.cs` 仍保留给候选浮窗：它只覆盖一个小窗口、不需要拖动，
`ACCENT_ENABLE_ACRYLICBLURBEHIND` 的逐帧代价可以接受；且浮窗是无焦点窗口，
WPF-UI 接管不了。失败回滚逻辑（还原 `Window.Background`、`BackgroundColor` 与
`-1` 的边框扩展）继续有效。

失败回滚也必须完整：只还原 `Window.Background` 而留下透明的 `BackgroundColor`
与 `-1` 的边框扩展，会得到"透明但没模糊"的窗口，直接透出桌面。

## 十六、采纳流程为什么必须离开钩子线程

低级键盘钩子的回调运行在安装线程上，而本项目是在 WPF UI 线程安装的——
所以"钩子线程"就是"UI 线程"，系统对每一次按键都要等它返回。

采纳流程里有同步的 UIA 读取：数字键复核的 `ReadBest(80)`，以及等候选窗消失时
首次 `Read`（单次上限 400ms）。这些都在第一个真正的 `await` 之前执行，
最坏情况会把整机键盘堵住约 0.8~1 秒。

`Task.Yield()` 解决不了：它会把续体投回 `DispatcherSynchronizationContext`，
而 Dispatcher 就是钩子线程本身。必须整体 `Task.Run` 到线程池。

搬走之后有两个新问题必须一起处理：

1. `_tracker.Reset()` 变成跨线程写。`CompositionTracker` 的所有入口（`Feed` / `Reset` /
   `End` / `Backspace` / `Version`）走同一把可重入锁。
2. 钩子立刻返回后，用户在"Esc → 上屏"这 100~150ms 内敲的键会插进中间，
   正文里冒出半截字母。所以 `_acceptInProgress` 期间要吞掉除修饰键外的所有 keydown，
   并在抬起时一并吞掉 keyup。

## 十七、自学习词库：能删，但必须让输入法重读

自学习词库是 `%APPDATA%\Microsoft\InputMethod\Chs\ChsPinyinUDL.dat`，
格式见 `MspyLearningStore` 的注释。想删掉不用的词，绕不开三个实测结论：

1. **改文件当场不生效**。输入法在登录时把整份词库读进内存（`ChsIME.exe` 从登录起一直活着），
   之后只写不读；改完再开一个新程序打字，看到的仍是内存里的旧数据。
2. **不重读就会被覆盖**。它下次学到新词时会用内存副本把文件整份写回，
   外部改动等于白改——这一点比"不生效"更危险，因为它会让人以为改成功了。
3. **切一次键盘布局能让它重读**。把持有输入法上下文的窗口切到英文布局再切回中文，
   输入法会重新加载词库：`ChsIME.exe` 的进程 Id 不变，不需要管理员权限，也不需要注销。
   这一步由 `ImeReloader` 完成，只影响设置窗口自己那个线程，不碰用户正在用的其它窗口。

删除的写法与输入法自己删词完全一致：整条 60 字节清零，只在 `+11` 留 `0xAA`，
槽位数（`0x0C`）保持不变——文件里本来就有这种空槽。读写都按共享方式打开，
输入法正开着文件也不会冲突。

只做删除、不做新增：每条记录尾部的 `n × u16` 音节索引指向输入法私有的 442 音节表，
没有公开对应关系，硬造会写出输不出词的坏条目。

界面上因此有三条约束：删除前按时间戳备份到 `%APPDATA%\BetterPinyin\udl-backup`；
写完立刻回读校验，被覆盖就重试一次；重新加载失败要如实说明，不能只报"已删除"。

备份与恢复另有两个坑：

- **`File.Copy` 会把源文件的修改时间一起复制过来**，所以备份文件的 mtime 是「那次改动之前词库最后被写的时刻」，
  不是备份时刻。列表要按文件名里的 `yyyyMMdd_HHmmss` 排序，按 mtime 排会把新备份插到旧位置。
- **恢复的文件长度可能和当前不同**。只覆盖前面的字节会留下旧尾巴，必须先 `SetLength` 再写；
  写完同样要重新加载，且因为会覆盖当前内容，恢复前必须再备份一份现状——否则「恢复」就是单向操作。

## 十八、开机启动：写 HKCU Run，而不是建快捷方式

开机启动有三条常见路线，这里选的是最轻的一条：

- **HKCU\...\Run 注册表值**：当前用户即可写，不需要管理员权限，卸载时删一个值就干净；
  命令里可以带上 `--autostart`，让程序在「首次运行默认打开设置」这条逻辑上跳过弹窗。
- **启动文件夹里的 .lnk**：目标路径写死在快捷方式里。程序换个目录，快捷方式会静默失效，
  而且更新 .lnk 还得走 COM，比写一个字符串麻烦。
- **计划任务**：能延迟启动、能提权，但需要管理员权限，也会在系统里留下用户不容易发现的条目。
  本程序不需要这些。

两个容易忽略的细节：

1. **路径必须加引号**。`C:\Program Files\...` 不加引号会被 Windows 拆成「C:\Program.exe」+ 参数；
   写入的是 `"C:\...\BetterMicrosoftPinyin.exe" --autostart`。
2. **「配置为开」不等于「应该重新创建」**。启动时只修「已登记但路径变了」的情况；
   如果注册表里的值已经被用户或其它工具删掉，就尊重这个结果并把配置改成关，
   否则用户刚在任务管理器里禁用，下一次启动又被悄悄写回来。

界面上的开关以注册表为准：进入页面先 `TryProbe`，读到什么显示什么；写入失败就把开关拨回去，
不显示假的「已开启」。

## 十九、Windows 11 实测：与 Win10 的异同

以下结论在 Windows 11 24H2（build 26100）+ 微软拼音上实测得到，用来核对前面各节基于 Windows 10 19045 的判断。

**一致的部分**（不需要按系统版本分支）：

- 候选条同样是「按坐标 `WindowFromPoint` 命中、`EnumWindows` 枚举不到」，候选窗属于 `TextInputHost`、
  类名 `Windows.UI.Core.CoreWindow`、未被隐身、尺寸远小于全屏宿主窗口。
- UIA 结构同一套：容器 + `ListItem` 候选项，`Quality` 能拿到 `Exact`，候选词文本与矩形都可读。
- 自定义短语写入 `ChsPinyinEUDPv1.lex` / `v2.lex` 后无需重启输入法，几秒内即可在原生候选中出现，
  且**同样排在候选第 1 位**（README 里「位置字段不影响排序」的结论在 Win11 上依旧成立）。
- 自学习词库 `ChsPinyinUDL.dat` 的路径与二进制格式不变，能正常读出词条与简拼。
- 「切一次键盘布局让输入法重读词库」这个技巧在 Win11 上同样有效。

**不同的部分**：

| 项目 | Windows 10 19045 | Windows 11 26100 |
|---|---|---|
| 候选窗容器的 AutomationId | `IME_Prediction_Window` | `IME_Candidate_Window` |
| 候选条尺寸（同一输入） | 602x45 | 617x36 |

两处差异都已经被现有代码覆盖（`IsCandidateContainer` 同时接受这两个 Id），因此**没有需要改的产品逻辑**。

**测试时最容易踩的坑不在系统，而在输入法本身**：同一台机器可以同时装搜狗拼音和微软拼音，
两者都注册在简体中文（0x0804）下、键盘布局 Id 完全一样，`GetKeyboardLayout` 分不出谁是谁。
搜狗会在目标进程内创建 `SoPY_Comp` 合成窗，类名与微软拼音完全不同 ——
如果测试时没先确认生效的是哪个输入法，会得出「候选窗类名变了、需要适配」的错误结论。
确认方法见 [DEVELOPMENT.md](DEVELOPMENT.md) 的 `--ime` / `--layout` 两节。

## 二十、输入统计：口径边界与三个坑

「今天打了多少字」这个问题，能做到的和做不到的必须先分清，否则数字会骗人。

**拿不到微软拼音原生上屏的汉字数。** 输入法是通过 TSF 在**目标程序进程内**提交文本的，
跨进程没有受支持的读取方式；UIA 的 `ValuePattern` 只覆盖部分编辑控件，
每次提交都去读一次的代价与失败率都不划算。所以统计里的「字数」只有两项：
拼音字母数（落到组字串里的按键）与云词上屏字数（我们自己注入的，长度已知）。
曲线默认用「组字次数」，因为它最接近「打了多少个词」的直觉。

**坑一：钩子必须共享，不能各装一个。** 同一个进程装两个 `WH_KEYBOARD_LL` 会按安装逆序串联，
而云联想会吞掉采纳键（返回 1 且不调 `CallNextHookEx`），排在后面的统计就再也看不到那些键；
反过来，如果统计先装、云联想后装，云联想的吞键判断也会受影响。所以有了 `KeyboardHub`：
引用计数式地持有唯一实例，分发顺序固定为「过滤器（可吞键）→ 组字状态机 → 观察者」。
另一个必改项是修饰键：`GetAsyncKeyState` 反映的是**此刻**的状态，
必须在钩子回调里算一次带下去，否则订阅方晚十几毫秒再查，Shift 可能已经松开。

共享之后还有一个容易漏的点：**统计必须自己 `Acquire`**，不能因为有云联想在装钩子就省掉。
用户完全可能关掉云联想、只留统计，那时没有任何人持有钩子，统计会安静地一直是 0。
现在两个模块各用各的 owner 标识（`cloud-assist` / `input-stats`），引用计数保证钩子只装一次；
装失败时页面会显示原因，而不是让用户对着 0 猜。

**坑二：云词采纳不会触发结束事件。** 采纳流程是「Esc 取消组字 → 注入文本 → `Reset()`」，
`Reset` 按设计不抛 `CompositionEnded`（抛了会让上屏前的代次校验失效）。
统计这边如果只等结束事件，就会以为这一段输入永远没结束，
之后所有不在组字的按键都会被算成「组字期间按键」。所以采纳成功时要主动收尾。

**坑三：状态机在抛事件前就清零了。** `CompositionTracker.End()` 先清缓冲再抛事件，
订阅方在回调里读 `RawInput` 只会拿到空串。现在 `CompositionEndedEventArgs` 带上了
`RawInput` 与 `Length`，是在 `End()` 里清零**之前**快照的。

其它几条约定：

- **跨天记在事件发生那一刻**。23:59 开始、00:01 提交的那次组字，组字次数落在前一天、
  提交次数落在后一天，所以某天的 `Committed + Cancelled` 允许比 `Compositions` 少一。
  这样不必在钩子线程里做跨天缓冲，崩溃最多丢最后一条。
- **只在组字开始时做过滤**：前台是本程序自己的窗口就不记（否则一打开设置页数字就跳）；
  前台不是中文键盘布局也不记（英文打字的字母不是拼音）。整段沿用同一个判断，不按键一次查一次。
- **UWP 应用只能记成「UWP 应用」**：记事本、计算器这类窗口都挂在 `ApplicationFrameHost` 上，
  真实应用名要走 `AppUserModelId`，代价与失败率都不划算，如实标注比猜一个名字好。
- **保留期按「最近 N 个有记录的日子」裁**，不按「今天往前 N 天」：后者一旦用户离开一段时间回来，
  整段历史会在下次落盘时被静默清空。
- **保留期必须跟着防抖落盘一起走**：`ScheduleSave` 只拿到快照，拿不到设置。
  一开始只有手动点「保存」才裁剪，用户从不打开统计页时文件会一直长；
  现在 `StatisticsStore` 记住最近一次的保留期，防抖刷盘与退出刷盘都按它来。

## 二十一、自更新：为什么要绕一个批处理

- **进程自己锁着自己**：运行中的 exe 不能被覆盖，所以只能「下载 → 解压到缓存目录 →
  起一个脚本等本进程退出 → 脚本复制 → 脚本重启」。脚本用 `cmd.exe` 而不是 PowerShell：
  后者受执行策略影响，绕过策略的启动参数又容易被安全软件盯上。
- **下载与安装必须分开**：用户要先知道「下到哪、占多大、能不能删」，再决定装不装。
  所以下载只写 `%LOCALAPPDATA%\BetterPinyin\updates\<tag>`，装不装是下一次点击的事；
  缓存目录常驻显示在「关于」页，带大小、打开文件夹与清理。放 `%TEMP%` 会被清理工具
  整片扫走，用户也找不到，所以没用临时目录。
- **路径不能写进脚本正文**：`cmd.exe` 按系统 OEM 代码页（简中是 936）读 .bat 文件，
  正文里写 UTF-8 中文路径会变成乱码，而用户的安装目录很可能带中文。
  解决办法是把脚本写成纯 ASCII，路径全部用命令行参数传进去——`CreateProcess` 走 UTF-16，
  参数不经过代码页转换；脚本里用 `%~1` 取回，路径带空格也没问题。
- **行尾必须是 CRLF**：只有 LF 的批处理里 `goto` 标签并不可靠，会静默跳到文件末尾。
  代码里 `string.Join("\r\n", lines)` 就是为这个。
- **等待用 PID + 进程名匹配**：`tasklist /FI "PID eq <pid>" /NH` 的输出是本地化的，
  「没有运行的任务」这句各语言不同，所以只 `findstr` 进程名，不匹配提示文字。
- **先解压再退出，不让脚本自己下载**：脚本里只留复制和重启两条逻辑，出错面最小；
  下载失败、解压失败都发生在进程还活着的时候，可以正常提示用户，不会把安装目录弄坏。
- **安装目录不可写就放弃自动替换**：放在 `Program Files` 时 UAC 会让 robocopy 大面积失败，
  与其留一半新一半旧，不如先探一下写权限，直接告诉用户手动解压。

## 二十二、窗口材质：为什么不用自己调 DwmSetWindowAttribute

- **先试过自己设，结论是不行**：设 `DWMWA_SYSTEMBACKDROP_TYPE` 并把
  `HwndSource.CompositionTarget.BackgroundColor` 改成透明之后，DWM 仍然把窗口表面
  当作不透明——壁纸一点都透不过来。缺的是 WPF-UI 在 `FluentWindow` 里配套做的几步
  （`WindowBackdropManager` 会处理窗口样式、框架扩展与背景刷的先后顺序）。
  所以设置窗口最终改用 WPF-UI 的 `WindowBackdropType`，候选浮窗继续用自研的
  `Backdrop`（那个窗口是 `AllowsTransparency` 的，走的是另一条路）。
- **材质和可读性是两件事**：Mica 只是把壁纸去饱和、调亮度，正文直接压上去在深色壁纸
  上会糊。所以窗口最底层铺一层 60% 的 `WindowGlassBrush` 着色，导航与页面保持透明，
  透明度靠这一层调。设了背景图时内容区那层 85% 的 `ContentGlassBrush` 要让开，
  否则图会被洗得几乎看不见。
- **`NavigationView` 有两层底色**：控件自身的 `Background` 只盖住外层，
  模板里还有一个独立的 Border 用 `NavigationViewContentBackground`。
  不覆盖这个键，右侧内容区永远是实色——这是最初「材质开了但看不出来」的真正原因。
  把它塞进 `RootNavigation.Resources` 就够了，不用改全局资源。
- **只在 22621+ 用**：更早的 Windows 11 与 Windows 10 走 `ApplyLegacyMicaBackdrop`
  （未公开的 accent 实现），拖动时逐帧重算整窗模糊，设置窗口会明显拖不动。
  判断统一用 `Backdrop.SupportsSystemBackdrop`，其余情况老实用纯色。
- **`WindowBackdropType` 属性回调只在值变化时触发**：窗口在 `SourceInitialized`
  时还没 Loaded，WPF-UI 会把那一次跳过；值没变又不会再触发。所以在 `Loaded` 里
  显式调一次 `WindowBackgroundManager.UpdateBackground`，否则「第一次打开没有材质」。

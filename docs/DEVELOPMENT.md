# 开发指南

## 环境

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 2004+ / Windows 11，x64 |
| SDK | .NET 8 SDK |
| IDE | Visual Studio 2022（可选，命令行即可开发） |
| 运行时 | Windows Desktop Runtime 8 |

唯一的运行期 NuGet 依赖是 **WPF-UI 4.3.0**（MIT，设置窗口的 Fluent 控件与导航），
其余全部自研：UIA、SendInput、钩子都是手写 P/Invoke 与 COM 互操作，
测试用自带的断言器而不是测试框架。依赖缺失时 `dotnet restore` 会从
`NuGet.config` 配置的镜像或官方源拉取。

## 构建与运行

```bash
dotnet build -c Debug
dotnet run --project src/BetterPinyin.App
```

首次运行会打开设置窗口。之后常驻托盘，双击托盘图标可再次打开设置。

## 测试

```bash
dotnet run --project src/BetterPinyin.Tests          # 离线单元测试
dotnet run --project src/BetterPinyin.Tests -- --live  # 额外跑一次真实联网验证
```

`--live` 会真的请求搜索建议接口，打印每个查询的原始条数、清洗结果、最终排序，用来确认端到端链路没坏。

## 诊断工具：ImeProbe

排查"候选窗读不到""浮窗位置不对"这类问题时的第一入口。

```bash
# 环境自检：键盘布局、是否装了微软拼音、模糊音/双拼开关、UIA 是否可用
dotnet run --project tools/ImeProbe

# 打开记事本模拟输入，验证候选窗能否读出
dotnet run --project tools/ImeProbe -- --self-test yisitalu

# 几何命中：在插入符附近逐点探测，找出真正的候选条窗口并导出 UIA 树
dotnet run --project tools/ImeProbe -- --hit-test yisitalu

# 截图取证（可裁剪、可缩放）
dotnet run --project tools/ImeProbe -- --shot-only --rect=470,170,1010,700 --scale=1 --shot=out.bmp

# 只截某个窗口本身：按进程名或标题匹配，窗口不在前台、被挡住都不影响
dotnet run --project tools/ImeProbe -- --shot-window=Notepad --shot=out.bmp
dotnet run --project tools/ImeProbe -- --shot-window=Better\ 微软拼音 --shot=out.bmp
```

`--shot-only` 走屏幕 DC 的 `BitBlt`，抓的是"屏幕上这一块像素"——窗口被遮挡时拿到的是遮挡者的内容。
`--shot-window` 走 `PrintWindow` + `PW_RENDERFULLCONTENT`，让窗口自己渲染，前后台无关，也**不会改变前台窗口**。
两者输出的都是 32 位 BMP。窗口最小化、受保护内容、或纯分层窗口（例如候选浮窗）可能拒绝渲染，
此时会打印原因并返回非零，可退回 `--shot-only` 从屏幕抓。

`--self-test` 的退出码：`0` 成功，`4` 没找到候选窗，`6` 前台抢焦失败（此时不会发送任何按键）。

新增的几件工具：

```bash
# 模拟"输入后按采纳键"，直接验证上屏链路（6 = 数字 6，oem3 = · 键）
dotnet run --project tools/ImeProbe -- --self-test ruonawa --accept=6
dotnet run --project tools/ImeProbe -- --self-test yisitalu --accept=oem3

# 英文模式：先按 Shift 切到英文状态，再输入、采纳
dotnet run --project tools/ImeProbe -- --self-test ruonawa --accept=6 --english

# 摆窗口 + 截图，便于核对界面
dotnet run --project tools/ImeProbe -- --activate-app --move-app=300,80,1000,700

# 发按键验证真实交互：Tab 到第 4 个控件，再用 F4 打开下拉框（十六进制 VK，d/u 前缀表示只按下/抬起）
dotnet run --project tools/ImeProbe -- --activate-app --send-keys=09,09,09,09,73
```

界面相关还有一个开发参数：`dotnet run --project src/BetterPinyin.App -- --page=外观` 可以直接打开指定的设置页（页名与左侧导航一致），省去手点。

### 在 Windows 11 上测试

Windows 11 改了记事本（UWP 应用），也更容易出现「同时装了多个中文输入法」的情况，
所以上面两条最常用的命令需要配合几个参数：

```bash
# Win11 的记事本窗口不属于启动它的进程，启动进程会立刻退出 → 用 --target 直接指定已存在的窗口
dotnet run --project tools/ImeProbe -- --self-test yisitalu --target=Notepad
dotnet run --project tools/ImeProbe -- --hit-test yisitalu --target=Notepad

# 只看不动手：把目标窗口切到前台、发一串真实按键（不带注入标记，程序会当成用户输入）
dotnet run --project tools/ImeProbe -- --focus=Notepad --hotkey=52,55,4F,4E,41,57,41

# 当前生效的是哪个输入法（搜狗 + 微软拼音可以同时装在简中下，键盘布局 Id 一样）
dotnet run --project tools/ImeProbe -- --ime

# 键盘布局是每线程的：控制台在中文不代表被测窗口也在中文，必要时钉住目标线程
dotnet run --project tools/ImeProbe -- --layout
dotnet run --project tools/ImeProbe -- --layout=apply:00000804 --target=Notepad

# 直接读本机微软拼音的两份词库文件，确认路径与格式还对得上
dotnet run --project tools/ImeProbe -- --wordbook
```

两个坑值得先知道：

1. **`--hotkey` 发的是「真实按键」而不是带标记的注入**。带标记的注入会被被测程序识别成自己人发的直接忽略，
   测出来全是假阴性（日志里 `own=True`）。
2. **`--ime` 的「当前生效」只能算尽力而为**：TSF 的语言配置是每线程的，后台进程查不到时会显示「未知」。
   判断输入法身份更可靠的办法是看候选窗类名 —— 搜狗是目标进程内的 `SoPY_Comp`，微软拼音是 `TextInputHost` 里的 `CoreWindow`。

`BetterPinyin.Tests -- --probe <拼音> candidate=<候选>` 可以单独观察某个候选的读音判定结果（`Strict` / `Extended` / `None`），排查"为什么这个词没出现"时最直接。

## 内置词库

`assets/lexicon.bin` 是构建产物，随仓库提交，所以 clone 后直接 `dotnet build` 即可。需要重新生成时：

```bash
# 1. 下载源数据（约 7.5 MB，不入库）
./tools/LexiconBuilder/fetch-sources.ps1

# 2. 生成 assets/lexicon.bin
dotnet run --project tools/LexiconBuilder -c Release -- \
  --pinyin-data tools/LexiconBuilder/sources/pinyin-data.txt \
  --phrase-data tools/LexiconBuilder/sources/phrase-pinyin.txt \
  --pinyin-pro  tools/LexiconBuilder/sources/pinyin-pro-dicts.txt \
  --word-freq   tools/LexiconBuilder/sources/jieba-dict.txt \
  --out         assets/lexicon.bin
```

源数据不随仓库分发；格式、许可与限制见 [LEXICON.md](LEXICON.md)。

## 目录约定

```
src/BetterPinyin.Contracts/   跨进程契约、注入路线接口。不得引用 UI / Win32
src/BetterPinyin.Core/        业务逻辑。不得引用 Win32 或 WPF
src/BetterPinyin.Interop/     Win32 / COM 封装。不得包含业务判断
src/BetterPinyin.App/         WPF 客户端与界面
```

新增代码时先判断它属于哪一层：如果一个改动需要 `Core` 引入 `System.Windows`，说明它放错了地方。

## 网络与依赖还原

`api.nuget.org` 在部分网络下直连超时，因此 `NuGet.config` 把镜像排在第一位、官方源兜底。海外网络下镜像不可达时 NuGet 会自动回退。

如果还原仍然失败：

1. 确认能访问 `mirrors.huaweicloud.com` 或 `api.nuget.org` 其中之一。
2. 运行期只引 `WPF-UI` 一个 NuGet 包；其余 Win32 / COM 互操作都是手写，不需要还原。
3. 提交 issue 时附上 `dotnet --info` 与还原报错。

## 品牌资源

应用图标与介绍图由 `LOGO/` 下的 2048px 源图生成，脚本只用 Python 标准库：

```bash
python tools/BrandBuilder/build-assets.py
```

输出：

- `assets/brand/logo0.png` / `logo1.png`：512px，关于页与 README 使用
- `assets/brand/app.ico`：16/24/32/48/64/128/256 七个尺寸

`LOGO/` 是本地设计源，已在 `.gitignore` 中忽略；换图后重新执行脚本即可。
`app.ico` 同时供 `ApplicationIcon`（exe）、窗口标题栏与托盘图标使用，三处保持一致。

## 发布

```bash
dotnet publish src/BetterPinyin.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

`app.manifest` 声明了 PerMonitorV2 DPI 感知，浮窗定位依赖它，发布时不要去掉。

## 编码约定

- 可空引用类型开启；`Core` 与 `Interop` 的公开 API 都要有 XML 文档注释。
- 注释解释**为什么**，不复述代码在做什么。
- 面向用户的字符串用中文；标识符、日志键名用英文。
- 任何跨进程调用都必须带超时，且不允许出现在键盘钩子回调里。
- 任何失败路径都要降级而不是抛异常到用户面前：输入流畅性优先。

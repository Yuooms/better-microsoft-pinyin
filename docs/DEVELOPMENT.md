# 开发指南

## 环境

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 2004+ / Windows 11，x64 |
| SDK | .NET 8 SDK |
| IDE | Visual Studio 2022（可选，命令行即可开发） |
| 运行时 | Windows Desktop Runtime 8 |

本项目**零第三方 NuGet 依赖**：UIA、SendInput、钩子都是手写 P/Invoke 与 COM 互操作，测试用自带的断言器而不是测试框架。这样在受限网络下也能直接构建。

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
```

`--self-test` 的退出码：`0` 成功，`4` 没找到候选窗，`6` 前台抢焦失败（此时不会发送任何按键）。

新增的几件工具：

```bash
# 模拟"输入后按采纳键"，直接验证上屏链路（6 = 数字 6，oem3 = · 键）
dotnet run --project tools/ImeProbe -- --self-test ruonawa --accept=6
dotnet run --project tools/ImeProbe -- --self-test yisitalu --accept=oem3

# 英文模式：先按 Shift 切到英文状态，再输入、采纳
dotnet run --project tools/ImeProbe -- --self-test ruonawa --accept=6 --english

# 摆窗口 + 截图，便于核对界面
dotnet run --project tools/ImeProbe -- --activate-app --move-app=300,80,940,660
```

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
2. 项目本身不引包，只有测试工程与 WPF 需要还原 SDK 内置组件。
3. 提交 issue 时附上 `dotnet --info` 与还原报错。

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

# 发布说明

面向维护者。发布包不进仓库（`publish/` 已在 `.gitignore` 里）。

## 版本号

唯一来源是根目录 `Directory.Build.props` 的 `<Version>`。设置窗口与「关于」页都读程序集版本，
改这一处即可；发布前先确认它和准备打的 tag 一致。

## 打包步骤

```bash
# 1. 回归
dotnet build -c Release
dotnet run --project src/BetterPinyin.Tests -c Release

# 2. 自包含发布（自带 .NET 运行时，用户不用另装）
dotnet publish src/BetterPinyin.App -c Release -r win-x64 --self-contained true \
  -p:DebugType=None -p:DebugSymbols=false \
  -o publish/v0.1.0

# 3. 压缩（Windows 上可以用资源管理器，或下面的 Python 一行）
python tools/Release/zip_release.py
```

`tools/Release/zip_release.py` 会：

- 把 `publish/v<版本>/` 压成 `publish/BetterMicrosoftPinyin-v<版本>-win-x64.zip`
- 输出同名 `.sha256` 文件

版本号从 `Directory.Build.props` 读取，不需要手改脚本。

## 发布前检查

- [ ] `dotnet build -c Release` 0 警告 0 错误
- [ ] `BetterPinyin.Tests` 全部通过
- [ ] Windows 10 上跑一遍：云联想采纳、词库删除、统计开关
- [ ] Windows 11 上跑一遍：同上（候选窗容器 Id 不同，已兼容）
- [ ] 关于页版本号与实际版本一致
- [ ] 解压 zip 到干净目录，双击能启动、托盘图标正常
- [ ] 发布说明里写清口径与已知限制

## 上传到 GitHub

1. 在仓库的 Releases 里新建 tag，例如 `v0.1.0`（与 `Directory.Build.props` 一致）。
2. 上传 `publish/BetterMicrosoftPinyin-v0.1.0-win-x64.zip`。
3. 把 `.sha256` 的内容贴到发布说明里，方便校验。
4. 发布说明直接用下面的模板。

### 发布说明模板

```markdown
首个公开版本。

**三个能力**

- 云联想：本地词库没有的词从搜索建议补到候选条旁，`·` 采纳整词，数字键采纳扩展词。
- 词库管理：自定义短语与自学习词的可视化列表，支持搜索、导入导出、批量删除与备份回退。
- 输入统计（默认关闭）：按天 / 按小时 / 按程序累计输入量，柱状图 + 程序排行，纯本地。

**安装**

下载 `BetterMicrosoftPinyin-v0.1.0-win-x64.zip`，解压后运行 `BetterMicrosoftPinyin.exe`。
自带 .NET 运行时，不需要管理员权限。

**系统要求**

Windows 10 2004 及以上，x64；系统自带的微软拼音。

**已知限制**

- 管理员权限运行的程序里无法上屏，也不会计入统计。
- 读不到微软拼音原生上屏的汉字数，统计里的「字数」只有云词采纳那部分。
- 删自学习词需要切一次键盘布局让输入法重读；只装了微软拼音一种布局时，要先加一个英文键盘。

SHA256：`<把 .sha256 文件的内容贴在这里>`
```

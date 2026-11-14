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
VERSION=$(grep -o '<Version>[^<]*' Directory.Build.props | cut -d'>' -f2)
dotnet publish src/BetterPinyin.App -c Release -r win-x64 --self-contained true \
  -p:DebugType=None -p:DebugSymbols=false \
  -o "publish/v$VERSION"

# 3. 压缩（Windows 上可以用资源管理器，或下面的 Python 一行）
python tools/Release/zip_release.py
```

版本号只改 `Directory.Build.props`，下面的 `$VERSION` 与脚本都从那里读，别在命令里写死。

`tools/Release/zip_release.py` 会：

- 把 `publish/v<版本>/` 压成 `publish/BetterMicrosoftPinyin-v<版本>-win-x64.zip`
- 输出同名 `.sha256` 文件

版本号从 `Directory.Build.props` 读取，不需要手改脚本。

> 包名要保持 `*win-x64.zip` 的形状：程序内置的更新检查按「名字带 win、以 .zip 结尾」挑附件，
> 改名会让老版本的更新检查找不到仓库与附件。

## 发布前检查

- [ ] `dotnet build -c Release` 0 警告 0 错误
- [ ] `BetterPinyin.Tests` 全部通过
- [ ] `<Version>` 与准备打的 tag 一致（`0.1.0` ↔ `v0.1.0`）；不一致会让用户一直看到「有新版本」
- [ ] `publish/v<版本>/` 是这次重新 publish 的，不是上一版的残留
- [ ] Windows 10 上跑一遍：云联想采纳、词库删除、统计开关
- [ ] Windows 11 上跑一遍：同上（候选窗容器 Id 不同，已兼容）
- [ ] 关于页版本号与实际版本一致
- [ ] 解压 zip 到干净目录，双击能启动、托盘图标正常
- [ ] 装一次上一版，点「检查更新」：能发现新版本、能下载到缓存、能安装并重启（验证发布页、附件名与接口都通）
- [ ] 发布说明里写清口径与已知限制

## 上传到 GitHub

1. 在仓库的 Releases 里新建 tag，例如 `v0.2.0`（与 `Directory.Build.props` 一致）。
2. 上传 `publish/BetterMicrosoftPinyin-v0.2.0-win-x64.zip`。
3. 把 `.sha256` 的内容贴到发布说明里，方便校验。
4. 发布说明参考下面的模板，按这一版的实际改动重写。

### 发布说明模板

```markdown
第二个公开版本（v0.2.0）。

**新增**

- 检查更新与自更新：「关于」页读 GitHub 的 releases/latest，启动时每天最多查一次，
  **只提示、不自动下载**；点「下载更新」存到本机缓存（显示路径与占用，可打开/清理），
  再点「安装并重启」才替换。
- Windows 11 窗口材质：22H2 及以上用 Mica，壁纸会透进设置窗口，可在「外观」关掉；
  Windows 10 自动使用纯色底。自定义背景图这次真正生效。
- 扩展云联想默认关闭（需要的人在云联想页打开）；采纳键支持自定义、增删与拖动排序。

**改动**

- 扩展云联想的候选默认不再写入自定义短语（可开关）：整句候选会长期占住原生候选第一位。

**安装**

下载 zip 解压后运行 `BetterMicrosoftPinyin.exe`，自带 .NET 运行时，不需要管理员权限。
从上一版升级也可以直接用「设置 → 关于」里的检查更新。

**系统要求**

Windows 10 2004 及以上，x64；系统自带的微软拼音。

**已知限制**

- 管理员权限运行的程序里无法上屏，也不会计入统计。
- 读不到微软拼音原生上屏的汉字数，统计里的「字数」只有云词采纳那部分。
- 删自学习词需要切一次键盘布局让输入法重读；只装了微软拼音一种布局时，要先加一个英文键盘。
- 自动更新要求安装目录可写；放在 Program Files 时会提示手动解压。

SHA256：`<把 .sha256 文件的内容贴在这里>`
```

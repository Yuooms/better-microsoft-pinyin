# 第三方声明

本项目自身代码以仓库根目录的 [LICENSE](LICENSE)（MIT）为准。

运行期依赖 **WPF-UI 4.3.0**（MIT）及其传递依赖 **WPF-UI.Abstractions 4.3.0**，用于设置窗口的
Fluent 控件、导航与标题栏。它们以二进制形式随程序分发。

内置词库在**构建期**使用了下列 MIT 许可的数据；生成的二进制文件包含这些数据的衍生形式，
因此在此保留原始版权声明。

## WPF-UI

- 项目：https://github.com/lepoco/wpfui
- 包：`WPF-UI` 4.3.0、`WPF-UI.Abstractions` 4.3.0
- 用途：`FluentWindow`、`TitleBar`、`NavigationView`、`CardControl`、`CardExpander`、
  `ToggleSwitch`、`SymbolIcon` 与主题资源
- 许可：MIT License，Copyright (C) 2021-2026 Leszek Pomianowski and WPF UI Contributors
- 分发形式：随程序以二进制形式分发，保留 MIT 版权与许可声明

## pinyin-data

- 项目：https://github.com/mozillazg/pinyin-data
- 用途：单字读音（含多音字）
- 许可：MIT License，Copyright (c) 2017 mozillazg

## phrase-pinyin-data

- 项目：https://github.com/mozillazg/phrase-pinyin-data
- 用途：词组读音
- 许可：MIT License，Copyright (c) 2017 mozillazg

## pinyin-pro

- 项目：https://github.com/zh-lx/pinyin-pro
- 用途：多音字词的读音覆盖
- 许可：MIT License，Copyright (c) 2021-present zh-lx

## jieba

- 项目：https://github.com/fxsjy/jieba
- 用途：词表与词频
- 许可：MIT License，Copyright (c) 2013 Sun Junyi

## 在线词库接口（非代码，不随程序分发）

运行期会按需向下列第三方服务发送**当前输入的拼音串**，用于取回云联想候选。这些是各家的公开 Web 端点，
不是官方开放 API，没有公开契约或许可承诺，可能随时变更、限流或停止服务：

- 必应搜索建议：`api.bing.com` / `cn.bing.com` / `www.bing.com`
- 搜狗网页输入法：`shouji.sogou.com`（私有二进制协议）

程序不随包分发上述服务的任何代码或数据，也不代表它们与本项目存在合作或背书关系。
搜狗协议的实现参考了 rime 社区公开项目（librime-cloud、rime-wenyun）对协议的描述，
**未复制其源代码**。用户可以在「设置 → 云联想 → 来源」里选择 Bing 或搜狗，或直接关闭云联想。

## 界面思路参考（未引入代码）

- 项目：BetterGI，https://github.com/babalae/better-genshin-impact
- 许可：GPL-3.0
- 说明：本项目设置窗口的信息架构（FluentWindow + TitleBar + NavigationView +
  CardControl / CardExpander 的分组方式）与窗口材质（Mica / 亚克力的降级策略）
  参考了 BetterGI 的公开界面。
  **仅参考交互与布局思路，未复制其源代码、XAML、样式或资源。**
  本项目不包含 GPL-3.0 衍生代码，因此仍按 MIT 分发。

---

MIT License 全文：

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

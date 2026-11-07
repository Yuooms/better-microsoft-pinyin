# 内置词库

云联想的读音校验与简拼展开依赖一份本地词库。词库是**构建期生成物**，运行期只读一个二进制文件，不依赖任何第三方包或在线服务。

> **格式版本 2**：在第 4 区段新增「词表」（汉字词面 + 词频 + 音节序列），文件约 5.0 MB。
> 有了它，设置里的「词库」页才能搜索、排序与调整权重。读取器仍接受 v1 文件：此时词表为空，
> 读音校验与简拼展开照常，只是没有词表可浏览。前三段的字节布局与 v1 完全一致。

## 为什么需要它

| 用途 | 说明 |
|---|---|
| 读音校验 | 判断「候选词读音是否等于用户输入」。没有它，云端返回的任意词都会被放行，这正是「输入 lianxiang 却给出联想电脑管家」的直接原因。 |
| 简拼展开 | `ystl` 这类输入无法直接查云端（Bing 会返回拉丁垃圾），需要先展开成 `yisitalu` 再查询。 |
| 扩展词排序 | 词频用于给简拼展开的候选排序。 |

## 数据来源

全部为 MIT 许可，构建时下载，**不随仓库分发**。

| 文件 | 用途 | 来源 | 大小 |
|---|---|---|---|
| `pinyin-data.txt` | 单字读音（含多音字） | [mozillazg/pinyin-data](https://github.com/mozillazg/pinyin-data) | 0.94 MB |
| `phrase-pinyin.txt` | 词组读音（成语、专名） | [mozillazg/phrase-pinyin-data](https://github.com/mozillazg/phrase-pinyin-data) | 1.20 MB |
| `pinyin-pro-dicts.txt` | 多音字词覆盖 | [pinyin-pro](https://www.npmjs.com/package/pinyin-pro)（npm，MIT） | 0.47 MB |
| `jieba-dict.txt` | 词表 + 词频 | [fxsjy/jieba](https://github.com/fxsjy/jieba) | 4.84 MB |

版权声明见仓库根目录的 `THIRD-PARTY-NOTICES.md`。

## 重建

```bash
# 1. 下载源数据（约 7.5 MB）
pwsh -File tools/LexiconBuilder/fetch-sources.ps1

# 2. 生成 assets/lexicon.bin
dotnet run --project tools/LexiconBuilder -c Release -- \
  --pinyin-data tools/LexiconBuilder/sources/pinyin-data.txt \
  --phrase-data tools/LexiconBuilder/sources/phrase-pinyin.txt \
  --pinyin-pro  tools/LexiconBuilder/sources/pinyin-pro-dicts.txt \
  --word-freq   tools/LexiconBuilder/sources/jieba-dict.txt \
  --out         assets/lexicon.bin \
  --max-words   150000
```

构建器会自检：每个音节都必须存在于规范音节表里；输出超过 8 MB 直接失败。产物 `assets/lexicon.bin` **随仓库提交**，所以 clone 后无需联网即可构建运行。

当前产物：`150000` 词、`44427` 个单字读音、约 `813 KB`。

## 二进制格式

单文件、小端、无指针，运行时直接在字节切片上二分，不做反序列化。

```
Header（8 字节）
  "BPXL" | u16 version | u16 sectionCount
区段表（sectionCount × 8 字节）
  u32 offset | u32 length
区段数据
```

| 区段 | 内容 |
|---|---|
| 0 音节表 | `u16 count` + 每个音节的 ASCII 拼写（顺序即音节 id） |
| 1 单字读音 | `u16 rangeCount` + 每块 `u32 start / u32 count / u8 slots / u8 pad` + `count × slots` 个 `u16`（`id + 1`，0 表示空槽） |
| 2 简拼索引 | `u32 wordCount` + `u32 entryCount` + 偏移表 + 按字典序排列的条目；条目为 `u8 keyLen / key / u8 optionCount / (u8 n + u16[n] syllableIds)` |

注意 `id + 1` 的偏移：音节 id 0 是 `a`，不能用 0 表示空槽。匹配器里所有槽位都保留这个偏移。

加载时会校验音节表与程序内置的表逐项一致，不一致直接拒绝加载并降级为空词库——宁可关闭读音校验，也不要读出乱码。

## 性能

| 指标 | 目标 | 实测 |
|---|---|---|
| 文件 | ≤ 8 MB | 0.79 MB |
| 加载 | < 300 ms | 约 10 ms（一次 `ReadAllBytes` + 头部校验） |
| 常驻内存 | < 40 MB | 约 1 MB（就是文件本身） |
| 单次匹配 | < 50 µs | 12 字候选 × 3 读音，DP 状态数 ≤ 16 × 33 |

## 已知限制

- **词表没有新词**。`若娜瓦`、`伊斯塔露` 这类近年出现的专名不在任何一份源数据里。但读音校验不需要词表——按单字读音组合即可正确匹配，所以严格/扩展分区仍然有效；只有简拼展开会受影响。
- **简拼展开按词频排序**。词频来自通用语料，与输入法场景不完全一致，偶尔会给出不常用的全拼串。展开只用于「拿去查云端」，最终结果仍会经过读音校验与去重。
- **多音字词组表覆盖有限**。词表里没有的词按单字首读音拼合，可能把 `银行` 展开成 `yinxing`。影响仅限于简拼展开的质量，不影响读音校验（校验用读音集合，不取首读音）。
- **扩展 B 区汉字**（U+20000 以后）没有收录读音，遇到时严格模式直接跳过该候选。

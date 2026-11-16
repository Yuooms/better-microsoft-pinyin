using System.Text;

namespace BetterPinyin.Core.Pinyin;

/// <summary>
/// 校验「候选词的读音」是否与「用户输入的串」对应。
/// </summary>
/// <remarks>
/// <para>
/// 这是云联想的质量闸门：云端返回的词必须读得通，否则会把「输入 yisital
/// 却给出毫不相干的词」这种噪声塞进候选栏。
/// </para>
/// <para>
/// 判定方向是**候选驱动**的：拿候选词每个字的读音集合当骨架，用输入从左往右覆盖它，
/// 而不是先把输入切成音节再比较。原因是输入的切分本来就不唯一——<c>xian</c> 既可能是
/// 一个音节（先），也可能是两个（西安）——先切分必然丢解。候选驱动只需要「存在一条
/// 走得通的路径」，两种读音都能命中。
/// </para>
/// <para>
/// 匹配分四档：整词命中、末音节未打完、简拼、以及「输入是候选的前缀但候选还有多余音节」。
/// 前三种进「云联想」，最后一种进「扩展云联想」，由 <see cref="Ranking.SuggestionSelector"/> 分区。
/// </para>
/// </remarks>
public sealed class PinyinMatcher
{
    /// <summary>候选词长度上限，防止异常输入把 DP 撑爆。</summary>
    private const int MaxLemmaLength = 16;

    private readonly IPinyinLexicon _lexicon;
    private readonly SyllableTable _table;

    /// <summary>创建匹配器。</summary>
    /// <param name="lexicon">读音词库；传 null 表示词库不可用，匹配器退回放行模式。</param>
    /// <param name="fuzzyEnabled">是否接受模糊音变体拼写。</param>
    /// <param name="table">音节表；一般传 null 使用共享实例。</param>
    public PinyinMatcher(
        IPinyinLexicon? lexicon = null,
        bool fuzzyEnabled = true,
        SyllableTable? table = null)
    {
        _lexicon = lexicon ?? NullPinyinLexicon.Instance;
        _table = table ?? SyllableTable.Default;
        FuzzyEnabled = fuzzyEnabled;
    }

    /// <summary>是否启用模糊音。</summary>
    public bool FuzzyEnabled { get; }

    /// <summary>是否具备读音校验能力。</summary>
    public bool IsAvailable => _lexicon.IsLoaded;

    /// <summary>词库加载状态，用于诊断。</summary>
    public IPinyinLexicon Lexicon => _lexicon;

    /// <summary>
    /// 判断候选词与输入串的对应关系。
    /// </summary>
    /// <param name="query">用户输入串（大小写不敏感，只允许 a~z）。</param>
    /// <param name="candidate">候选词。</param>
    /// <returns>匹配结果；不匹配时 <see cref="PinyinMatch.Kind"/> 为 <see cref="PinyinMatchKind.None"/>。</returns>
    public PinyinMatch Match(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate))
        {
            return PinyinMatch.None;
        }

        // 没有词库时无从校验，放行并交给其它规则过滤（长度、纯汉字、去重）。
        if (!_lexicon.IsLoaded)
        {
            return new PinyinMatch(PinyinMatchKind.Permissive, candidate.Length, 0);
        }

        var normalized = query.ToLowerInvariant();
        foreach (var ch in normalized)
        {
            if (ch is < 'a' or > 'z')
            {
                return PinyinMatch.None;
            }
        }

        var length = candidate.Length;
        if (length > MaxLemmaLength || !TryCollectReadings(candidate, out var readings))
        {
            return PinyinMatch.None;
        }

        var best = Search(normalized, readings);
        return best switch
        {
            1 => new PinyinMatch(PinyinMatchKind.Strict, length, 0),
            2 => new PinyinMatch(PinyinMatchKind.StrictPartialTail, length, 0),
            >= 10 => new PinyinMatch(PinyinMatchKind.Extended, length, best - 10),
            _ => TryAbbreviation(normalized, readings)
                ? new PinyinMatch(PinyinMatchKind.Abbreviation, length, 0)
                : PinyinMatch.None,
        };
    }

    /// <summary>
    /// 还原候选词在本次输入下的完整读音。
    /// </summary>
    /// <param name="query">用户实际输入串（全拼、简拼、或末音节没打完都算）。</param>
    /// <param name="candidate">候选词。</param>
    /// <param name="reading">还原出的无调全拼；失败时为空串。</param>
    /// <returns>能还原返回 true。</returns>
    /// <remarks>
    /// <para>
    /// 上屏之后要记账（写用户词库、进而写自定义短语），那里要的是一串**能被完整切分**的全拼。
    /// <see cref="Match"/> 只回答「配不配」，不带回读音；把用户敲的简拼（<c>chtqt</c>）
    /// 当拼音存进词库，<see cref="Wordbook.UserWordbook.Learn"/> 会在切分那一步直接拒绝，
    /// 词就静默地学不进去——日志里只有「已上屏」，永远等不到「已记入词库」。
    /// </para>
    /// <para>
    /// 还原依据是**候选词自己的读音集合**，不是输入串：同一个输入可能对上好几个词，
    /// 简拼展开出来的全拼属于「别的词」，照抄会把短语写到错误的拼音下面。
    /// </para>
    /// </remarks>
    public bool TryResolveReading(string query, string candidate, out string reading)
    {
        reading = string.Empty;
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate) || !IsAvailable)
        {
            return false;
        }

        var normalized = query.ToLowerInvariant();
        foreach (var ch in normalized)
        {
            if (ch is < 'a' or > 'z')
            {
                return false;
            }
        }

        var length = candidate.Length;
        if (length > MaxLemmaLength || !TryCollectReadings(candidate, out var readings))
        {
            return false;
        }

        // 输入本来就是全拼、且音节数与字数对得上：切分结果直接可用。
        // 但它会保留用户敲的模糊音拼写（zongguo 不会归一成 zhongguo），
        // 所以放在后面兜底，优先走下面按候选词读音还原的两条路。
        var split = _table.TrySplit(normalized, FuzzyEnabled, out var parts) && parts.Count == length;

        var chosen = new int[length];

        // 一个字母一个声母：简拼（chtqt → cihua ting qi tan）。
        if (normalized.Length == length && ResolveInitials(normalized, readings, chosen))
        {
            reading = Compose(chosen);
            return true;
        }

        // 末音节只打了一半（yisital → 伊斯塔露）。
        if (ResolveGreedy(normalized, readings, chosen))
        {
            reading = Compose(chosen);
            return true;
        }

        if (split)
        {
            reading = string.Concat(parts);
            return true;
        }

        return false;
    }

    /// <summary>取候选词每个字的读音槽位，匹配与还原读音共用。</summary>
    private bool TryCollectReadings(string candidate, out ushort[][] readings)
    {
        var length = candidate.Length;
        readings = new ushort[length][];
        for (var i = 0; i < length; i++)
        {
            if (!_lexicon.TryGetReadings(candidate[i], out var slots))
            {
                return false;
            }

            // 槽位是「音节 id + 1」，0 表示空槽。这里必须原样保留偏移，
            // 否则 id 0（音节 a）会和空槽撞在一起，后续再减 1 还会整体错位。
            var copy = new ushort[slots.Length];
            for (var s = 0; s < slots.Length; s++)
            {
                copy[s] = slots[s];
            }

            readings[i] = copy;
        }

        return true;
    }

    /// <summary>按「输入每个字母都是声母」逐个字挑读音；有一字对不上就失败。</summary>
    private bool ResolveInitials(string query, ushort[][] readings, int[] chosen)
    {
        for (var i = 0; i < query.Length; i++)
        {
            var wanted = SyllableTable.NormalizeInitial(query[i]);
            var found = -1;
            foreach (var raw in readings[i])
            {
                if (raw == 0)
                {
                    continue;
                }

                var id = raw - 1;
                if (id < _table.Count && SyllableTable.NormalizeInitial(_table.GetInitial(id)) == wanted)
                {
                    found = id;
                    break;
                }
            }

            if (found < 0)
            {
                return false;
            }

            chosen[i] = found;
        }

        return true;
    }

    /// <summary>
    /// 从左往右用输入逐个吃掉候选的音节，末音节允许只打了一半。
    /// </summary>
    /// <remarks>
    /// 只在全拼切分与简拼都对不上时才走这里，输入长度和字数通常不一致。
    /// 贪心足够：能走到这一步的输入，第一步可选音节往往是唯一的。
    /// </remarks>
    private bool ResolveGreedy(string query, ushort[][] readings, int[] chosen)
    {
        var position = 0;
        for (var i = 0; i < readings.Length; i++)
        {
            if (position >= query.Length)
            {
                return false; // 输入已经吃完，候选还有字
            }

            var picked = -1;
            var consumed = 0;
            foreach (var raw in readings[i])
            {
                if (raw == 0)
                {
                    continue;
                }

                var id = raw - 1;
                if (id >= _table.Count)
                {
                    continue;
                }

                foreach (var spelling in SpellingsOf(id))
                {
                    if (spelling.Length > query.Length - position ||
                        !query.AsSpan(position, spelling.Length).SequenceEqual(spelling))
                    {
                        continue;
                    }

                    picked = id;
                    consumed = spelling.Length;
                    break;
                }

                if (picked >= 0)
                {
                    break;
                }
            }

            // 残段只许出现在候选的最后一个音节上，和匹配规则一致。
            if (picked < 0 && i == readings.Length - 1)
            {
                foreach (var raw in readings[i])
                {
                    if (raw == 0)
                    {
                        continue;
                    }

                    var id = raw - 1;
                    if (id >= _table.Count)
                    {
                        continue;
                    }

                    foreach (var spelling in SpellingsOf(id))
                    {
                        if (!spelling.AsSpan().StartsWith(query.AsSpan(position), StringComparison.Ordinal))
                        {
                            continue;
                        }

                        picked = id;
                        consumed = query.Length - position;
                        break;
                    }

                    if (picked >= 0)
                    {
                        break;
                    }
                }
            }

            if (picked < 0)
            {
                return false;
            }

            chosen[i] = picked;
            position += consumed;
        }

        return position == query.Length;
    }

    /// <summary>按音节 id 拼出全拼；模糊音变体统一归一成规范拼写。</summary>
    private string Compose(int[] ids)
    {
        var builder = new StringBuilder(ids.Length * 4);
        foreach (var id in ids)
        {
            builder.Append(_table.GetCanonical(id));
        }

        return builder.ToString();
    }

    /// <summary>某个音节可以被写成哪几种拼写；关掉模糊音时只有规范拼写。</summary>
    private IEnumerable<string> SpellingsOf(int id) =>
        FuzzyEnabled ? _table.GetSpellings(id) : new[] { _table.GetCanonical(id) };

    /// <summary>
    /// 候选驱动的 DP。返回值编码：0 不匹配、1 整词命中、2 末音节未打完、
    /// 10+n 表示候选还多 n 个音节。数值越小越好，便于取最优。
    /// </summary>
    private int Search(string query, ushort[][] readings)
    {
        var lemmaLength = readings.Length;
        var width = query.Length + 1;
        var memo = new int[(lemmaLength + 1) * width];
        Array.Fill(memo, -1);

        return Dfs(0, 0);

        int Dfs(int charIndex, int queryIndex)
        {
            var key = (charIndex * width) + queryIndex;
            if (memo[key] >= 0)
            {
                return memo[key];
            }

            int best;
            if (queryIndex == query.Length)
            {
                // 输入吃完：音节也刚好用完才算整词命中，否则候选还有富余。
                best = charIndex == lemmaLength ? 1 : 10 + (lemmaLength - charIndex);
            }
            else if (charIndex >= lemmaLength)
            {
                best = 0;
            }
            else
            {
                best = 0;
                foreach (var raw in readings[charIndex])
                {
                    if (raw == 0)
                    {
                        continue;
                    }

                    var id = raw - 1;
                    if (id >= _table.Count)
                    {
                        continue;
                    }

                    if (FuzzyEnabled)
                    {
                        foreach (var spelling in _table.GetSpellings(id))
                        {
                            best = Consider(best, spelling, charIndex, queryIndex);
                        }
                    }
                    else
                    {
                        best = Consider(best, _table.GetCanonical(id), charIndex, queryIndex);
                    }
                }
            }

            memo[key] = best;
            return best;
        }

        int Consider(int best, string spelling, int charIndex, int queryIndex)
        {
            var remaining = query.Length - queryIndex;
            if (remaining >= spelling.Length)
            {
                if (query.AsSpan(queryIndex, spelling.Length).SequenceEqual(spelling))
                {
                    var result = Dfs(charIndex + 1, queryIndex + spelling.Length);
                    if (result > 0 && (best == 0 || result < best))
                    {
                        return result;
                    }
                }

                return best;
            }

            // 末音节未打完：只允许出现在候选的最后一个音节上。
            // 否则「打字打到一半」就会冒出长词（ruonaw 不应命中「若娜瓦壁纸」）。
            if (charIndex == lemmaLength - 1 &&
                spelling.AsSpan().StartsWith(query.AsSpan(queryIndex), StringComparison.Ordinal))
            {
                return best == 0 || 2 < best ? 2 : best;
            }

            return best;
        }
    }

    private bool TryAbbreviation(string query, ushort[][] readings)
    {
        if (query.Length != readings.Length)
        {
            return false;
        }

        for (var i = 0; i < readings.Length; i++)
        {
            var matched = false;
            foreach (var raw in readings[i])
            {
                if (raw == 0)
                {
                    continue;
                }

                var id = raw - 1;
                if (id >= _table.Count)
                {
                    continue;
                }

                if (SyllableTable.NormalizeInitial(_table.GetInitial(id)) ==
                    SyllableTable.NormalizeInitial(query[i]))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }
}

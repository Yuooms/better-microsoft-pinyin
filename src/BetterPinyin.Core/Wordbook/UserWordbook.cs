namespace BetterPinyin.Core.Wordbook;

/// <summary>
/// 用户词库：学到或手动添加的词，以及内置词的权重覆盖。
/// </summary>
/// <remarks>
/// <para>
/// 读多写少：采纳线程会调 <see cref="Learn"/>，设置页与查询线程会读。实现用「不可变快照 +
/// 整体替换」——所有可读状态装在一个 <see cref="State"/> 里，写入时在锁内构造新快照再原子发布。
/// 读侧不加锁、不分配，也不会看到写了一半的集合。
/// </para>
/// <para>
/// 词条数上限 5000：超过时按 <see cref="UserWord.LastUsedAt"/> 淘汰最旧的。
/// 这个数字不是拍脑袋——词库页一次最多显示 2000 条，5000 条足够覆盖几年的日常积累。
/// </para>
/// </remarks>
public sealed class UserWordbook
{
    /// <summary>词条上限。</summary>
    public const int MaxWords = 5000;

    private readonly object _gate = new();
    private State _state = State.Empty;
    private long _version;

    /// <summary>词库发生变化时触发，可能来自任意线程。</summary>
    public event EventHandler? Changed;

    /// <summary>当前快照版本号，每次变更递增。</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>词条数量（含已遗忘的）。</summary>
    public int Count => Volatile.Read(ref _state).Words.Length;

    /// <summary>当前全部词条的只读快照。</summary>
    public IReadOnlyList<UserWord> Words => Volatile.Read(ref _state).Words;

    /// <summary>内置词的覆盖记录快照。</summary>
    public IReadOnlyList<WordOverride> Overrides => Volatile.Read(ref _state).Overrides.Values.ToArray();

    /// <summary>被遗忘的词面集合（用户词与内置词合并）。</summary>
    public IReadOnlySet<string> HiddenTexts => Volatile.Read(ref _state).HiddenTexts;

    /// <summary>
    /// 整库替换（用于启动加载与导入）。
    /// </summary>
    /// <param name="words">词条。</param>
    /// <param name="overrides">内置词覆盖。</param>
    public void Reset(IEnumerable<UserWord> words, IEnumerable<WordOverride> overrides)
    {
        lock (_gate)
        {
            Publish(words, overrides);
        }

        RaiseChanged();
    }

    /// <summary>
    /// 学到一个词：已存在则累加使用次数，不存在则新增。
    /// </summary>
    /// <param name="text">词面。</param>
    /// <param name="pinyin">无调全拼。</param>
    /// <param name="code">用户输入串。</param>
    /// <param name="word">落库后的词条。</param>
    /// <returns>是否写入成功（词面为空或拼音无法切分时返回 false）。</returns>
    public bool Learn(string text, string pinyin, string code, out UserWord word)
    {
        word = default!;
        var normalized = NormalizeText(text);
        if (normalized.Length is < 2 or > 8 || pinyin.Length == 0)
        {
            return false;
        }

        var table = Pinyin.SyllableTable.Default;
        if (!table.TryGetInitials(pinyin, out var initials))
        {
            return false;
        }

        UserWord result;
        lock (_gate)
        {
            var state = _state;
            var now = DateTimeOffset.Now;

            if (state.ByText.TryGetValue(normalized, out var existing))
            {
                result = existing with
                {
                    Hits = existing.Hits + 1,
                    LastUsedAt = now,
                    Code = string.IsNullOrEmpty(code) ? existing.Code : code,
                };

                Publish(state.Words.Select(w => ReferenceEquals(w, existing) ? result : w), state.Overrides.Values);
            }
            else
            {
                result = new UserWord
                {
                    Text = normalized,
                    Pinyin = pinyin,
                    Initials = initials,
                    Code = code ?? string.Empty,
                    Hits = 1,
                    Adjustment = WordAdjustment.Normal,
                    UdpPosition = 0,
                    UdpState = UdpSyncState.Pending,
                    LearnedAt = now,
                    LastUsedAt = now,
                };

                var words = state.Words.Append(result).ToList();
                if (words.Count > MaxWords)
                {
                    // 淘汰最久未用的，保留新学到的这条。
                    var oldest = words
                        .Where(w => !ReferenceEquals(w, result))
                        .OrderBy(w => w.LastUsedAt)
                        .FirstOrDefault();
                    if (oldest is not null)
                    {
                        words.Remove(oldest);
                    }
                }

                Publish(words, state.Overrides.Values);
            }
        }

        word = result;
        RaiseChanged();
        return true;
    }

    /// <summary>手动添加一条词；已存在时只更新拼音与次数。</summary>
    /// <param name="text">词面。</param>
    /// <param name="pinyin">无调全拼。</param>
    /// <param name="code">输入串，可为空。</param>
    /// <returns>是否成功。</returns>
    public bool Add(string text, string pinyin, string code) => Learn(text, pinyin, code, out _);

    /// <summary>
    /// 设置权重档位。用户词改自身，内置词写入覆盖层。
    /// </summary>
    /// <param name="text">词面。</param>
    /// <param name="pinyin">无调全拼；内置词必填。</param>
    /// <param name="adjustment">目标档位。</param>
    /// <returns>是否成功。</returns>
    public bool SetAdjustment(string text, string pinyin, WordAdjustment adjustment)
    {
        var normalized = NormalizeText(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var state = _state;
            var overrides = new Dictionary<string, WordOverride>(state.Overrides, StringComparer.Ordinal);

            if (state.ByText.TryGetValue(normalized, out var existing))
            {
                var updated = existing with { Adjustment = adjustment, LastUsedAt = DateTimeOffset.Now };
                Publish(state.Words.Select(w => ReferenceEquals(w, existing) ? updated : w), overrides.Values);
            }
            else
            {
                if (adjustment == WordAdjustment.Normal)
                {
                    overrides.Remove(normalized);
                }
                else
                {
                    overrides[normalized] = new WordOverride
                    {
                        Text = normalized,
                        Pinyin = pinyin ?? string.Empty,
                        Adjustment = adjustment,
                    };
                }

                Publish(state.Words, overrides.Values);
            }
        }

        RaiseChanged();
        return true;
    }

    /// <summary>删除一条用户词；内置词的覆盖改成 <see cref="WordAdjustment.Normal"/>。</summary>
    /// <param name="text">词面。</param>
    /// <returns>是否确实删掉了什么。</returns>
    public bool Remove(string text)
    {
        var normalized = NormalizeText(text);
        lock (_gate)
        {
            var state = _state;
            if (!state.ByText.ContainsKey(normalized))
            {
                return false;
            }

            Publish(state.Words.Where(w => !string.Equals(w.Text, normalized, StringComparison.Ordinal)), state.Overrides.Values);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>更新同步状态。</summary>
    /// <param name="text">词面。</param>
    /// <param name="syncState">新状态。</param>
    /// <returns>是否成功。</returns>
    public bool SetUdpState(string text, UdpSyncState syncState)
    {
        var normalized = NormalizeText(text);
        lock (_gate)
        {
            var state = _state;
            if (!state.ByText.TryGetValue(normalized, out var existing))
            {
                return false;
            }

            var updated = existing with { UdpState = syncState };
            Publish(state.Words.Select(w => ReferenceEquals(w, existing) ? updated : w), state.Overrides.Values);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>设置写入微软拼音时的候选位置。</summary>
    /// <param name="text">词面。</param>
    /// <param name="position">1~9；0 表示跟随全局默认。</param>
    /// <returns>是否成功。</returns>
    public bool SetUdpPosition(string text, int position)
    {
        var normalized = NormalizeText(text);
        var clamped = Math.Clamp(position, 0, 9);
        lock (_gate)
        {
            var state = _state;
            if (!state.ByText.TryGetValue(normalized, out var existing))
            {
                return false;
            }

            if (existing.UdpPosition == clamped)
            {
                return true;
            }

            var updated = existing with { UdpPosition = clamped, UdpState = UdpSyncState.Pending };
            Publish(state.Words.Select(w => ReferenceEquals(w, existing) ? updated : w), state.Overrides.Values);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>把一个词的同步状态批量标记为待同步（词条或位置被改动时用）。</summary>
    /// <param name="texts">词面集合。</param>
    public void MarkPending(IEnumerable<string> texts)
    {
        var targets = new HashSet<string>(texts, StringComparer.Ordinal);
        if (targets.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var state = _state;
            var changed = false;
            var words = new UserWord[state.Words.Length];
            for (var i = 0; i < state.Words.Length; i++)
            {
                var word = state.Words[i];
                if (word.UdpState != UdpSyncState.Pending && targets.Contains(word.Text))
                {
                    words[i] = word with { UdpState = UdpSyncState.Pending };
                    changed = true;
                }
                else
                {
                    words[i] = word;
                }
            }

            if (!changed)
            {
                return;
            }

            Publish(words, state.Overrides.Values);
            RaiseChanged();
        }
    }

    /// <summary>取一个词的权重档位；用户词优先于内置覆盖。</summary>
    /// <param name="text">词面。</param>
    /// <returns>档位，未收录返回 <see cref="WordAdjustment.Normal"/>。</returns>
    public WordAdjustment AdjustmentOf(string text)
    {
        var state = Volatile.Read(ref _state);
        if (state.ByText.TryGetValue(NormalizeText(text), out var word))
        {
            return word.Adjustment;
        }

        return state.Overrides.TryGetValue(NormalizeText(text), out var entry)
            ? entry.Adjustment
            : WordAdjustment.Normal;
    }

    /// <summary>某个词面是否被遗忘。</summary>
    /// <param name="text">词面。</param>
    /// <returns>被遗忘返回 true。</returns>
    public bool IsHidden(string text) => Volatile.Read(ref _state).HiddenTexts.Contains(NormalizeText(text));

    /// <summary>某个拼音是否对应被遗忘的词，供简拼展开过滤内置词。</summary>
    /// <param name="pinyin">无调全拼。</param>
    /// <returns>被遗忘返回 true。</returns>
    public bool IsPinyinHidden(string pinyin) =>
        pinyin.Length > 0 && Volatile.Read(ref _state).HiddenPinyin.Contains(pinyin);

    /// <summary>
    /// 取某个拼音对应的权重档位，用于给简拼展开结果排序。
    /// </summary>
    /// <param name="pinyin">无调全拼。</param>
    /// <returns>档位；没有覆盖时返回 <see cref="WordAdjustment.Normal"/>。</returns>
    public WordAdjustment AdjustmentOfPinyin(string pinyin)
    {
        if (string.IsNullOrEmpty(pinyin))
        {
            return WordAdjustment.Normal;
        }

        return Volatile.Read(ref _state).PinyinAdjustments.TryGetValue(pinyin, out var adjustment)
            ? adjustment
            : WordAdjustment.Normal;
    }

    /// <summary>
    /// 按声母前缀取用户词，用于简拼展开。
    /// </summary>
    /// <param name="initials">声母串。</param>
    /// <param name="max">最多几条。</param>
    /// <returns>排好序的词条。</returns>
    public IReadOnlyList<UserWord> MatchInitials(string initials, int max)
    {
        if (string.IsNullOrEmpty(initials) || max <= 0)
        {
            return Array.Empty<UserWord>();
        }

        var query = initials.ToLowerInvariant();
        var state = Volatile.Read(ref _state);
        var results = new List<UserWord>(Math.Min(max, 8));

        foreach (var word in state.Words)
        {
            if (word.IsHidden || !word.Initials.StartsWith(query, StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(word);
        }

        return Rank(results, max);
    }

    /// <summary>
    /// 按完整拼音取用户词，用于「这个词我学过没有」的判断。
    /// </summary>
    /// <param name="pinyin">无调全拼。</param>
    /// <param name="max">最多几条。</param>
    /// <returns>排好序的词条。</returns>
    public IReadOnlyList<UserWord> MatchPinyin(string pinyin, int max)
    {
        if (string.IsNullOrEmpty(pinyin) || max <= 0)
        {
            return Array.Empty<UserWord>();
        }

        var query = pinyin.ToLowerInvariant();
        var state = Volatile.Read(ref _state);
        var results = new List<UserWord>(Math.Min(max, 8));

        foreach (var word in state.Words)
        {
            if (word.IsHidden || !string.Equals(word.Pinyin, query, StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(word);
        }

        return Rank(results, max);
    }

    /// <summary>按档位与使用次数排序：提升在前、降低在后，同档按次数降序。</summary>
    private static IReadOnlyList<UserWord> Rank(List<UserWord> words, int max)
    {
        if (words.Count == 0)
        {
            return Array.Empty<UserWord>();
        }

        words.Sort((a, b) =>
        {
            var byAdjustment = Weight(a.Adjustment).CompareTo(Weight(b.Adjustment));
            if (byAdjustment != 0)
            {
                return byAdjustment;
            }

            var byHits = b.Hits.CompareTo(a.Hits);
            return byHits != 0 ? byHits : string.CompareOrdinal(a.Text, b.Text);
        });

        return words.Count <= max ? words : words.GetRange(0, max);
    }

    private static int Weight(WordAdjustment adjustment) => adjustment switch
    {
        WordAdjustment.Boosted => 0,
        WordAdjustment.Normal => 1,
        WordAdjustment.Reduced => 2,
        _ => 3,
    };

    private void Publish(IEnumerable<UserWord> words, IEnumerable<WordOverride> overrides)
    {
        var list = words
            .Where(w => !string.IsNullOrEmpty(w.Text))
            .GroupBy(w => w.Text, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(w => w.LearnedAt)
            .ToArray();

        var byText = new Dictionary<string, UserWord>(list.Length, StringComparer.Ordinal);
        var hiddenTexts = new HashSet<string>(StringComparer.Ordinal);
        var hiddenPinyin = new HashSet<string>(StringComparer.Ordinal);
        var pinyinAdjustments = new Dictionary<string, WordAdjustment>(StringComparer.Ordinal);
        var overrideMap = new Dictionary<string, WordOverride>(StringComparer.Ordinal);

        foreach (var word in list)
        {
            byText[word.Text] = word;
            if (word.IsHidden)
            {
                hiddenTexts.Add(word.Text);
                hiddenPinyin.Add(word.Pinyin);
            }
            else if (word.Adjustment != WordAdjustment.Normal && word.Pinyin.Length > 0)
            {
                pinyinAdjustments[word.Pinyin] = word.Adjustment;
            }
        }

        foreach (var entry in overrides)
        {
            if (string.IsNullOrEmpty(entry.Text))
            {
                continue;
            }

            if (entry.Adjustment == WordAdjustment.Normal)
            {
                continue;
            }

            overrideMap[entry.Text] = entry;
            if (entry.Adjustment == WordAdjustment.Hidden)
            {
                hiddenTexts.Add(entry.Text);
                if (entry.Pinyin.Length > 0)
                {
                    hiddenPinyin.Add(entry.Pinyin);
                }
            }
            else if (entry.Pinyin.Length > 0)
            {
                pinyinAdjustments[entry.Pinyin] = entry.Adjustment;
            }
        }

        var next = new State(list, byText, overrideMap, hiddenTexts, hiddenPinyin, pinyinAdjustments);
        Volatile.Write(ref _state, next);
        Interlocked.Increment(ref _version);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string NormalizeText(string? text) => text?.Trim() ?? string.Empty;

    /// <summary>一份自洽的只读快照。</summary>
    private sealed record State(
        UserWord[] Words,
        Dictionary<string, UserWord> ByText,
        Dictionary<string, WordOverride> Overrides,
        HashSet<string> HiddenTexts,
        HashSet<string> HiddenPinyin,
        Dictionary<string, WordAdjustment> PinyinAdjustments)
    {
        public static State Empty { get; } = new(
            Array.Empty<UserWord>(),
            new Dictionary<string, UserWord>(StringComparer.Ordinal),
            new Dictionary<string, WordOverride>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, WordAdjustment>(StringComparer.Ordinal));
    }
}

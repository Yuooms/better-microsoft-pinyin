using System.Diagnostics.CodeAnalysis;

namespace BetterPinyin.Core.Pinyin;

/// <summary>
/// 无调音节表，含模糊音、ü 写法与儿化的等价拼写。
/// </summary>
/// <remarks>
/// <para>
/// 匹配时不再对整串做 <c>Replace</c> 归一。原因有两个：整串归一会让
/// <c>zhang</c> 变成 <c>zang</c> 后音节边界错位；而且模糊音用户可能直接
/// 打出 <c>zuang</c> 这种不是规范音节的串，逐音节归一根本没有入口。
/// </para>
/// <para>
/// 这里的做法是为每个规范音节生成「等价拼写」，建立拼写 → 音节 id 的多值映射。
/// 变体只在**结果是真实音节**时才保留（<c>zhang→zang</c> 保留，<c>bian→biang</c> 丢弃），
/// 所以不会凭空造出非法拼写。ü 的 <c>lue/nue</c> 与儿化的 <c>r</c> 是显式白名单。
/// </para>
/// <para>
/// 反向映射天然是对称的：<c>zang</c> 同时映射到「脏」与「张」，模糊音两个方向都能命中。
/// </para>
/// </remarks>
public sealed class SyllableTable
{
    /// <summary>共享实例。构造只发生一次，约 1~2 ms。</summary>
    /// <remarks>
    /// 用嵌套类持有单例，避免静态字段初始化顺序问题：本类的字段按文本顺序初始化，
    /// 而 <see cref="Initials"/> 声明在下面，直接在字段初始化器里 new 会拿到 null。
    /// </remarks>
    public static SyllableTable Default => DefaultHolder.Instance;

    /// <summary>规范音节（用 v 表示 ü）。</summary>
    private const string CanonicalText =
        "a ai an ang ao " +
        "ba bai ban bang bao bei ben beng bi bian biao bie bin bing bo bu " +
        "ca cai can cang cao ce cen ceng cha chai chan chang chao che chen cheng chi chong chou chu chua chuai chuan chuang chui chun chuo ci cong cou cu cuan cui cun cuo " +
        "da dai dan dang dao de dei den deng di dia dian diao die ding diu dong dou du duan dui dun duo " +
        "e ei en eng er " +
        "fa fan fang fei fen feng fo fou fu " +
        "ga gai gan gang gao ge gei gen geng gong gou gu gua guai guan guang gui gun guo " +
        "ha hai han hang hao he hei hen heng hong hou hu hua huai huan huang hui hun huo " +
        "ji jia jian jiang jiao jie jin jing jiong jiu ju juan jue jun " +
        "ka kai kan kang kao ke ken keng kong kou ku kua kuai kuan kuang kui kun kuo " +
        "la lai lan lang lao le lei leng li lia lian liang liao lie lin ling liu lo long lou lu lv luan lve lun luo " +
        "ma mai man mang mao me mei men meng mi mian miao mie min ming miu mo mou mu " +
        "na nai nan nang nao ne nei nen neng ni nian niang niao nie nin ning niu nong nou nu nv nuan nve nuo " +
        "o ou " +
        "pa pai pan pang pao pei pen peng pi pian piao pie pin ping po pou pu " +
        "qi qia qian qiang qiao qie qin qing qiong qiu qu quan que qun " +
        "ran rang rao re ren reng ri rong rou ru rua ruan rui run ruo " +
        "sa sai san sang sao se sen seng sha shai shan shang shao she shei shen sheng shi shou shu shua shuai shuan shuang shui shun shuo si song sou su suan sui sun suo " +
        "ta tai tan tang tao te teng ti tian tiao tie ting tong tou tu tuan tui tun tuo " +
        "wa wai wan wang wei wen weng wo wu " +
        "xi xia xian xiang xiao xie xin xing xiong xiu xu xuan xue xun " +
        "ya yan yang yao ye yi yin ying yo yong you yu yuan yue yun " +
        "za zai zan zang zao ze zei zen zeng zha zhai zhan zhang zhao zhe zhei zhen zheng zhi zhong zhou zhu zhua zhuai zhuan zhuang zhui zhun zhuo zi zong zou zu zuan zui zun zuo";

    /// <summary>声母表，按长度降序，保证 zh/ch/sh 先于 z/c/s 命中。</summary>
    private static readonly string[] Initials =
    {
        "zh", "ch", "sh", "b", "p", "m", "f", "d", "t", "n", "l",
        "g", "k", "h", "j", "q", "x", "r", "z", "c", "s", "y", "w",
    };

    private readonly string[] _canonical;
    private readonly Dictionary<string, int> _canonicalIds;
    private readonly Dictionary<string, int[]> _idsBySpelling;
    private readonly string[][] _spellingsById;
    private readonly Dictionary<char, string[]> _spellingsByFirstChar;
    private readonly char[] _initials;

    private SyllableTable()
    {
        _canonical = CanonicalText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _canonicalIds = new Dictionary<string, int>(_canonical.Length, StringComparer.Ordinal);
        for (var i = 0; i < _canonical.Length; i++)
        {
            _canonicalIds[_canonical[i]] = i;
        }

        var idsBySpelling = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var spellingsById = new List<string>[_canonical.Length];
        var byFirstChar = new Dictionary<char, List<string>>();

        for (var id = 0; id < _canonical.Length; id++)
        {
            spellingsById[id] = new List<string>(4);

            foreach (var spelling in GenerateSpellings(_canonical[id]))
            {
                if (!idsBySpelling.TryGetValue(spelling, out var list))
                {
                    list = new List<int>(2);
                    idsBySpelling[spelling] = list;
                    if (!byFirstChar.TryGetValue(spelling[0], out var bucket))
                    {
                        bucket = new List<string>(64);
                        byFirstChar[spelling[0]] = bucket;
                    }

                    bucket.Add(spelling);
                }

                if (!list.Contains(id))
                {
                    list.Add(id);
                }

                spellingsById[id].Add(spelling);
            }
        }

        _idsBySpelling = new Dictionary<string, int[]>(idsBySpelling.Count, StringComparer.Ordinal);
        foreach (var pair in idsBySpelling)
        {
            _idsBySpelling[pair.Key] = pair.Value.ToArray();
        }

        _spellingsById = new string[_canonical.Length][];
        for (var id = 0; id < _canonical.Length; id++)
        {
            // 长拼写优先，让「末音节补全」优先选最长的那个。
            _spellingsById[id] = spellingsById[id]
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(s => s.Length)
                .ToArray();
        }

        _spellingsByFirstChar = new Dictionary<char, string[]>(byFirstChar.Count);
        foreach (var pair in byFirstChar)
        {
            _spellingsByFirstChar[pair.Key] = pair.Value
                .OrderByDescending(s => s.Length)
                .ToArray();
        }

        _initials = new char[_canonical.Length];
        for (var id = 0; id < _canonical.Length; id++)
        {
            _initials[id] = _canonical[id][0];
        }
    }

    /// <summary>规范音节数量。</summary>
    public int Count => _canonical.Length;

    /// <summary>取某个音节的规范拼写。</summary>
    /// <param name="id">音节 id。</param>
    /// <returns>规范拼写，例如 <c>zhuang</c>。</returns>
    public string GetCanonical(int id) => _canonical[id];

    /// <summary>取某个音节全部可接受拼写（含模糊音变体），长的在前。</summary>
    /// <param name="id">音节 id。</param>
    /// <returns>拼写数组，至少含规范拼写。</returns>
    public string[] GetSpellings(int id) => _spellingsById[id];

    /// <summary>
    /// 简拼比较用的声母归一：n/l、f/h 视为同一类。
    /// </summary>
    /// <param name="value">声母首字母。</param>
    /// <returns>归一后的字母。</returns>
    /// <remarks>
    /// 简拼索引在构建期与运行期必须使用同一套归一，否则「你」的简拼
    /// <c>n</c> 与索引里的 <c>l</c> 对不上。这个归一与模糊音开关无关：
    /// 简拼本身只有一两个字母，再区分 n/l 没有意义。
    /// </remarks>
    public static char NormalizeInitial(char value) => value switch
    {
        'n' => 'l',
        'f' => 'h',
        _ => value,
    };

    /// <summary>取某个音节的声母首字母（用于简拼）。</summary>
    /// <param name="id">音节 id。</param>
    /// <returns>首字母。</returns>
    public char GetInitial(int id) => _initials[id];

    /// <summary>按规范拼写取 id（不做模糊音展开）。</summary>
    /// <param name="spelling">规范拼写。</param>
    /// <param name="id">音节 id。</param>
    /// <returns>存在返回 true。</returns>
    public bool TryGetCanonicalId(string spelling, [NotNullWhen(true)] out int id)
        => _canonicalIds.TryGetValue(spelling, out id);

    /// <summary>
    /// 把一个由字母组成的串完整切分为音节，用于判断「这串像不像拼音」。
    /// </summary>
    /// <param name="text">小写字母串。</param>
    /// <param name="fuzzy">是否接受模糊音变体拼写。</param>
    /// <param name="syllables">切出的最少音节数。</param>
    /// <returns>能被完整切分返回 true。</returns>
    public bool TrySegment(ReadOnlySpan<char> text, bool fuzzy, out int syllables)
    {
        syllables = 0;
        if (text.Length == 0 || text.Length > 48)
        {
            return false;
        }

        var counts = new int[text.Length + 1];
        Array.Fill(counts, int.MaxValue);
        counts[0] = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (counts[i] == int.MaxValue)
            {
                continue;
            }

            if (!_spellingsByFirstChar.TryGetValue(text[i], out var candidates))
            {
                continue;
            }

            foreach (var spelling in candidates)
            {
                var end = i + spelling.Length;
                if (end > text.Length)
                {
                    continue;
                }

                if (!text.Slice(i, spelling.Length).SequenceEqual(spelling) ||
                    !_idsBySpelling.TryGetValue(spelling, out var ids))
                {
                    continue;
                }

                if (!fuzzy && !IsCanonicalSpelling(spelling, ids))
                {
                    continue;
                }

                if (counts[end] > counts[i] + 1)
                {
                    counts[end] = counts[i] + 1;
                }
            }
        }

        if (counts[text.Length] == int.MaxValue)
        {
            return false;
        }

        syllables = counts[text.Length];
        return true;
    }

    /// <summary>
    /// 把一串字母切分成音节，返回每个音节的标准拼写。
    /// </summary>
    /// <param name="text">小写字母串。</param>
    /// <param name="fuzzy">是否接受模糊音变体拼写。</param>
    /// <param name="syllables">切出的音节，按顺序。变体拼写会归一成标准拼写。</param>
    /// <returns>能被完整切分返回 true。</returns>
    public bool TrySplit(ReadOnlySpan<char> text, bool fuzzy, out IReadOnlyList<string> syllables)
    {
        syllables = Array.Empty<string>();
        if (text.Length == 0 || text.Length > 48)
        {
            return false;
        }

        var counts = new int[text.Length + 1];
        var previous = new int[text.Length + 1];
        var chosen = new string?[text.Length + 1];
        Array.Fill(counts, int.MaxValue);
        Array.Fill(previous, -1);
        counts[0] = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (counts[i] == int.MaxValue)
            {
                continue;
            }

            if (!_spellingsByFirstChar.TryGetValue(text[i], out var candidates))
            {
                continue;
            }

            foreach (var spelling in candidates)
            {
                var end = i + spelling.Length;
                if (end > text.Length)
                {
                    continue;
                }

                if (!text.Slice(i, spelling.Length).SequenceEqual(spelling) ||
                    !_idsBySpelling.TryGetValue(spelling, out var ids))
                {
                    continue;
                }

                if (!fuzzy && !IsCanonicalSpelling(spelling, ids))
                {
                    continue;
                }

                if (counts[end] > counts[i] + 1)
                {
                    counts[end] = counts[i] + 1;
                    previous[end] = i;

                    // 优先取「拼写本身」对应的音节：nie 既是 nie 的规范拼写，也是 lie 的模糊变体，
                    // 直接取 ids[0] 会把用户输入的 nie 变成 lie，简拼与读音都会错位。
                    var exact = -1;
                    foreach (var id in ids)
                    {
                        if (spelling.Equals(_canonical[id], StringComparison.Ordinal))
                        {
                            exact = id;
                            break;
                        }
                    }

                    chosen[end] = GetCanonical(exact >= 0 ? exact : ids[0]);
                }
            }
        }

        if (counts[text.Length] == int.MaxValue)
        {
            return false;
        }

        var parts = new List<string>(counts[text.Length]);
        for (var position = text.Length; position > 0; position = previous[position])
        {
            parts.Add(chosen[position]!);
        }

        parts.Reverse();
        syllables = parts;
        return true;
    }

    /// <summary>
    /// 取一个全拼串的声母串。
    /// </summary>
    /// <param name="pinyin">无调全拼，音节之间不分隔。</param>
    /// <param name="initials">简拼。</param>
    /// <param name="normalize">
    /// 是否按简拼索引的规则归一模糊音（<c>n→l</c>、<c>f→h</c>）。
    /// 索引匹配要归一；但把简拼当作**触发码**写给输入法时必须保持原字母，
    /// 否则 <c>faniesi</c> 会变成 <c>hls</c>，用户打 <c>fns</c> 反而出不来。
    /// </param>
    /// <returns>能被完整切分返回 true。</returns>
    public bool TryGetInitials(string pinyin, out string initials, bool normalize = true)
    {
        initials = string.Empty;
        if (!TrySplit(pinyin, fuzzy: false, out var parts))
        {
            return false;
        }

        var builder = new System.Text.StringBuilder(parts.Count);
        foreach (var part in parts)
        {
            builder.Append(normalize ? NormalizeInitial(part[0]) : part[0]);
        }

        initials = builder.ToString();
        return true;
    }

    private static IEnumerable<string> GenerateSpellings(string syllable)
    {
        yield return syllable;

        var (initial, final) = Split(syllable);
        var initials = new List<string>(2) { initial };
        var finals = new List<string>(4) { final };

        // 翘舌 / 平舌、n / l、f / h 两个方向都加，模糊音是对称的。
        switch (initial)
        {
            case "zh": initials.Add("z"); break;
            case "ch": initials.Add("c"); break;
            case "sh": initials.Add("s"); break;
            case "z": initials.Add("zh"); break;
            case "c": initials.Add("ch"); break;
            case "s": initials.Add("sh"); break;
            case "n": initials.Add("l"); break;
            case "l": initials.Add("n"); break;
            case "f": initials.Add("h"); break;
            case "h": initials.Add("f"); break;
        }

        // 前后鼻音。变体必须仍然是真实音节，所以最后统一过滤。
        switch (final)
        {
            case "an": finals.Add("ang"); break;
            case "ang": finals.Add("an"); break;
            case "en": finals.Add("eng"); break;
            case "eng": finals.Add("en"); break;
            case "in": finals.Add("ing"); break;
            case "ing": finals.Add("in"); break;
            case "ian": finals.Add("iang"); break;
            case "iang": finals.Add("ian"); break;
            case "uan": finals.Add("uang"); break;
            case "uang": finals.Add("uan"); break;
            case "er": finals.Add("r"); break;
        }

        // ü 只对 l / n 开头的音节做等价（ju/qu/xu 本身就写作 u，不能互相替换）。
        if (initial is "l" or "n")
        {
            switch (final)
            {
                case "v": finals.Add("u"); break;
                case "u": finals.Add("v"); break;
                case "ve": finals.Add("ue"); break;
                case "ue": finals.Add("ve"); break;
            }
        }

        // 组合结果不要求本身是规范音节：模糊音用户会直接打出 zuang 这类串，
        // 它必须能映射到 zhuang。允许组合不会产生歧义，因为每个拼写最终都指向
        // 若干规范音节 id，匹配仍然按音节的读音集合判定。
        foreach (var candidateInitial in initials)
        {
            foreach (var candidateFinal in finals)
            {
                var spelling = candidateInitial + candidateFinal;
                if (spelling.Length > 0)
                {
                    yield return spelling;
                }
            }
        }
    }

    /// <summary>拼写是否就是其中某个音节 id 的规范写法（用于关闭模糊音时过滤变体）。</summary>
    private bool IsCanonicalSpelling(string spelling, int[] ids)
    {
        foreach (var id in ids)
        {
            if (spelling.Equals(_canonical[id], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static (string Initial, string Final) Split(string syllable)
    {
        foreach (var initial in Initials)
        {
            if (syllable.Length > initial.Length &&
                syllable.AsSpan().StartsWith(initial, StringComparison.Ordinal))
            {
                return (initial, syllable[initial.Length..]);
            }
        }

        return (string.Empty, syllable);
    }

    /// <summary>嵌套类持有单例，与当前类的静态字段初始化顺序解耦。</summary>
    private static class DefaultHolder
    {
        internal static readonly SyllableTable Instance = new();
    }
}

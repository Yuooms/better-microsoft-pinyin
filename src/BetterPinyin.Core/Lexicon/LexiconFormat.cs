namespace BetterPinyin.Core.Lexicon;

/// <summary>
/// 词库二进制格式的常量定义。
/// </summary>
/// <remarks>
/// <para>
/// 单文件、小端、无指针、4 字节对齐，运行时直接在 <see cref="ReadOnlySpan{T}"/> 上切片，
/// 不做反序列化。改结构必须递增 <see cref="Version"/>，旧文件读到新版本会直接拒绝加载，
/// 降级为空词库而不是读出乱码。
/// </para>
/// <para>
/// 布局：
/// <code>
/// Header（8 字节）
///   "BPXL" | u16 version | u16 sectionCount
/// 区段表（sectionCount × 8 字节）
///   u32 offset | u32 length
/// 区段数据
/// </code>
/// </para>
/// </remarks>
public static class LexiconFormat
{
    /// <summary>文件魔数。</summary>
    public static ReadOnlySpan<byte> Magic => "BPXL"u8;

    /// <summary>当前格式版本。</summary>
    public const ushort Version = 1;

    /// <summary>文件头长度。</summary>
    public const int HeaderSize = 8;

    /// <summary>区段表每项长度。</summary>
    public const int SectionEntrySize = 8;

    /// <summary>区段编号。</summary>
    public enum Section
    {
        /// <summary>规范音节表，顺序即音节 id。</summary>
        Syllables = 0,

        /// <summary>单字读音表，按码位分块直址。</summary>
        CharReadings = 1,

        /// <summary>简拼索引。</summary>
        Initials = 2,

        /// <summary>区段总数。</summary>
        Count = 3,
    }
}

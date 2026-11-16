using System.Buffers.Binary;
using System.Text;

namespace BetterPinyin.Core.Cloud.Sogou;

/// <summary>
/// 搜狗云输入响应帧的解析器。
/// </summary>
/// <remarks>
/// 设计目标是「任意字节流都不能让程序崩溃」：接口没有契约，截断、长度不符、
/// 字段越界都可能出现。所有偏移都先比较再累加，越界一律停止并保留已解析到的部分。
/// </remarks>
public static class SogouResponseParser
{
    /// <summary>候选数上限；实测只有 1~3 条，超过这个数说明帧已经错位。</summary>
    public const int MaxCandidates = 32;

    /// <summary>单条候选最大字符数，超长视为噪声。</summary>
    public const int MaxTextChars = 64;

    private const int MinLength = 0x16;

    private const int CountOffset = 0x12;

    private const int DataOffset = 0x14;

    /// <summary>
    /// 解析响应帧。
    /// </summary>
    /// <param name="response">原始响应体。</param>
    /// <returns>候选文本，按接口返回顺序；帧非法时返回空列表。</returns>
    public static IReadOnlyList<string> Parse(ReadOnlySpan<byte> response)
    {
        // 帧头声明的总长是唯一的自校验手段。不符就整帧拒绝：
        // 继续解只会把任意字节当成词长，产出乱码。
        if (response.Length < MinLength || response[0] + 2 != response.Length)
        {
            return Array.Empty<string>();
        }

        if (!TryReadUInt16(response, CountOffset, out var count) ||
            count == 0 ||
            count > MaxCandidates)
        {
            return Array.Empty<string>();
        }

        var words = new List<string>(count);
        var offset = DataOffset;

        for (var i = 0; i < count; i++)
        {
            if (!TryReadUInt16(response, offset, out var byteCount))
            {
                break;
            }

            offset += 2;

            // 词长是 UTF-16LE 的字节数；奇数或越界说明结构已经错位。
            if (byteCount == 0 || (byteCount & 1) != 0 || byteCount > response.Length - offset)
            {
                break;
            }

            var text = Encoding.Unicode.GetString(response.Slice(offset, byteCount));
            offset += byteCount;

            if (text.Length is > 0 && text.Length <= MaxTextChars && words.Count < MaxCandidates)
            {
                words.Add(text);
            }

            // 词后跟两个长度前缀的块，再额外 1 字节；任何一步越界都停止。
            if (!TrySkipBlock(response, ref offset) ||
                !TrySkipBlock(response, ref offset) ||
                offset >= response.Length)
            {
                break;
            }

            offset++;
        }

        return words;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> span, int offset, out ushort value)
    {
        value = 0;

        // 用减法比较而不是 offset + 2 <= Length：后者在极端偏移下可能整型溢出。
        if (offset < 0 || span.Length - offset < 2)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
        return true;
    }

    private static bool TrySkipBlock(ReadOnlySpan<byte> span, ref int offset)
    {
        if (!TryReadUInt16(span, offset, out var length))
        {
            return false;
        }

        offset += 2;

        if (length > span.Length - offset)
        {
            return false;
        }

        offset += length;
        return true;
    }
}

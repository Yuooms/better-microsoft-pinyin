namespace BetterPinyin.Core.Cloud.Sogou;

/// <summary>
/// 搜狗网页输入法的请求帧。
/// </summary>
/// <remarks>
/// 这是搜狗网页端的私有二进制协议，不是公开 API：帧头是固定 token，
/// 长度字段只有一个字节，末字节是所有前序字节的异或校验。
/// 协议随时可能变，因此组帧失败一律返回空数组，由调用方放弃本次查询。
/// </remarks>
public static class SogouFrame
{
    /// <summary>唯一的请求地址。</summary>
    /// <remarks>
    /// 查询串里的是网页端固定的匿名标识（<c>h</c> 全零、渠道 <c>store_mf_wandoujia</c>），
    /// 不包含任何用户或设备信息。
    /// </remarks>
    public const string Endpoint =
        "https://shouji.sogou.com/web_ime/mobile.php?durtot=0&h=000000000000000&r=store_mf_wandoujia&v=3.7";

    /// <summary>查询串最长字节数：长度字段只有一个字节。</summary>
    public const int MaxQueryBytes = 245;

    private static ReadOnlySpan<byte> Token =>
        new byte[] { 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x01 };

    /// <summary>
    /// 组装请求体。
    /// </summary>
    /// <param name="pinyin">全拼串，只允许英文字母。</param>
    /// <returns>请求体；输入不可用时返回空数组。</returns>
    public static byte[] Build(string? pinyin)
    {
        if (string.IsNullOrEmpty(pinyin) || pinyin.Length > MaxQueryBytes)
        {
            return Array.Empty<byte>();
        }

        // 布局：总长(1) + token(7) + 长度(1) + 拼音(n) + 校验(1)
        var frame = new byte[pinyin.Length + 10];
        frame[0] = (byte)frame.Length;
        Token.CopyTo(frame.AsSpan(1));
        frame[8] = (byte)pinyin.Length;

        for (var i = 0; i < pinyin.Length; i++)
        {
            var ch = pinyin[i];
            if (ch is >= 'A' and <= 'Z')
            {
                ch = (char)(ch + ('a' - 'A'));
            }

            // 非字母说明上游给了双拼、数字或汉字，搜狗不认这种输入，直接放弃。
            if (ch is < 'a' or > 'z')
            {
                return Array.Empty<byte>();
            }

            frame[9 + i] = (byte)ch;
        }

        var checksum = 0;
        for (var i = 0; i < frame.Length - 1; i++)
        {
            checksum ^= frame[i];
        }

        frame[^1] = (byte)checksum;
        return frame;
    }
}

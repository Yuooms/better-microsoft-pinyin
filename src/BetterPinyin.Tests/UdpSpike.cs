using System.Buffers.Binary;
using System.Text;
using BetterPinyin.Core.Wordbook;

namespace BetterPinyin.Tests;

/// <summary>
/// 微软拼音「用户定义的短语」文件的读写诊断。
/// </summary>
/// <remarks>
/// 用途：在不动产品代码的前提下，直接观察 <c>ChsPinyinEUDPv1.lex</c> 的真实字节，
/// 手工写入一条短语后再由输入法自己读一遍，验证格式假设是否成立。
/// 所有输出都是给人看的诊断信息，不参与断言。
/// </remarks>
internal static class UdpSpike
{
    /// <summary>执行诊断命令。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出码。</returns>
    public static int Run(string[] args)
    {
        var store = new MspyPhraseStore();
        Console.WriteLine($"文件：{store.FilePath}");
        Console.WriteLine($"存在：{store.Exists}");

        if (store.Exists)
        {
            DumpRaw(store.FilePath);
        }

        var phrases = store.Read(out var readError);
        Console.WriteLine($"读取：{phrases.Count} 条{(readError is null ? string.Empty : $"（错误：{readError}）")}");
        foreach (var phrase in phrases)
        {
            Console.WriteLine($"  [{phrase.Position}] {phrase.Pinyin} → {phrase.Text}");
        }

        var writeIndex = Array.FindIndex(args, a =>
            a.Equals("--udp-write", StringComparison.OrdinalIgnoreCase));
        if (writeIndex < 0 || writeIndex + 3 >= args.Length)
        {
            return 0;
        }

        var pinyin = args[writeIndex + 1];
        var position = int.TryParse(args[writeIndex + 2], out var parsed) ? parsed : 2;
        var text = args[writeIndex + 3];

        Console.WriteLine();
        Console.WriteLine($"写入：{pinyin} 位置 {position} → {text}");
        var ok = store.Merge(
            new[] { new MspyPhrase(pinyin, position, text) },
            overwriteOthers: true,
            out var conflicts);

        Console.WriteLine(ok
            ? $"写入成功（冲突 {conflicts.Count} 条）"
            : $"写入失败：{store.LastError}");

        if (ok)
        {
            DumpRaw(store.FilePath);
            var after = store.Read(out var afterError);
            Console.WriteLine($"回读：{after.Count} 条{(afterError is null ? string.Empty : $"（错误：{afterError}）")}");
            foreach (var phrase in after)
            {
                Console.WriteLine($"  [{phrase.Position}] {phrase.Pinyin} → {phrase.Text}");
            }
        }

        return ok ? 0 : 1;
    }

    private static void DumpRaw(string path)
    {
        var data = File.ReadAllBytes(path);
        Console.WriteLine($"字节数：{data.Length}");
        Console.WriteLine($"魔数：{Encoding.ASCII.GetString(data, 0, Math.Min(8, data.Length))}");
        if (data.Length < 0x20)
        {
            return;
        }

        Console.WriteLine(
            $"头：ver={BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8))} " +
            $"u10={BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10)):X4} " +
            $"u12={BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12))} " +
            $"u16={BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16))} " +
            $"u20={BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x14))} " +
            $"total={BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x18))} " +
            $"count={BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x1C))}");

        var shown = Math.Min(data.Length, 0x80);
        for (var i = 0; i < shown; i += 16)
        {
            var line = new StringBuilder($"  {i:X4}  ");
            for (var j = 0; j < 16 && i + j < shown; j++)
            {
                line.Append($"{data[i + j]:X2} ");
            }

            Console.WriteLine(line.ToString());
        }
    }
}

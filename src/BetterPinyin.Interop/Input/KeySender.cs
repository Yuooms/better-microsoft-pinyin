using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Interop.Input;

/// <summary>
/// 通过 SendInput 向当前焦点窗口注入按键。
/// </summary>
/// <remarks>
/// 所有注入的按键都带 <see cref="NativeMethods.InjectionMarker"/>，
/// 本程序的键盘钩子看到该标记会直接放行，避免自我循环。
/// </remarks>
public static class KeySender
{
    /// <summary>按下并抬起一个虚拟键。</summary>
    /// <param name="virtualKey">虚拟键码，例如 0x1B 为 Esc。</param>
    /// <param name="markAsOwn">
    /// 是否打上本程序的注入标记。本程序自己发的按键必须打标记，避免被自己的钩子重新计入拼音串；
    /// 仿真工具模拟用户输入时应传 false。
    /// </param>
    /// <returns>是否发送成功。</returns>
    public static bool SendVirtualKey(ushort virtualKey, bool markAsOwn = true)
    {
        var inputs = new[]
        {
            CreateKeyDown(virtualKey, unicode: false, markAsOwn),
            CreateKeyUp(virtualKey, unicode: false, markAsOwn),
        };

        return NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>())
               == (uint)inputs.Length;
    }

    /// <summary>逐字符发送 Unicode 文本。</summary>
    /// <param name="text">要上屏的文本。</param>
    /// <param name="markAsOwn">是否打上本程序的注入标记。</param>
    /// <returns>是否发送成功。</returns>
    public static bool SendUnicodeText(string text, bool markAsOwn = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = CreateKeyDown(text[i], unicode: true, markAsOwn);
            inputs[(i * 2) + 1] = CreateKeyUp(text[i], unicode: true, markAsOwn);
        }

        return NativeMethods.SendInput(
                   (uint)inputs.Length,
                   inputs,
                   System.Runtime.InteropServices.Marshal.SizeOf<INPUT>())
               == (uint)inputs.Length;
    }

    private static INPUT CreateKeyDown(ushort code, bool unicode, bool markAsOwn) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Keyboard = new KEYBDINPUT
        {
            VirtualKey = unicode ? (ushort)0 : code,
            ScanCode = unicode ? code : (ushort)0,
            Flags = unicode ? NativeMethods.KEYEVENTF_UNICODE : 0,
            ExtraInfo = markAsOwn ? NativeMethods.InjectionMarker : IntPtr.Zero,
        },
    };

    private static INPUT CreateKeyUp(ushort code, bool unicode, bool markAsOwn) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Keyboard = new KEYBDINPUT
        {
            VirtualKey = unicode ? (ushort)0 : code,
            ScanCode = unicode ? code : (ushort)0,
            Flags = (unicode ? NativeMethods.KEYEVENTF_UNICODE : 0) | NativeMethods.KEYEVENTF_KEYUP,
            ExtraInfo = markAsOwn ? NativeMethods.InjectionMarker : IntPtr.Zero,
        },
    };
}

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BetterPinyin.Tools.ImeProbe;

/// <summary>
/// 查询并切换当前生效的 TSF 输入法（语言配置）。
/// </summary>
/// <remarks>
/// <para>为什么需要它：本机可以同时装搜狗拼音和微软拼音，两者都注册在简体中文（0x0804）下，
/// 键盘布局 Id 完全一样，因此 <c>GetKeyboardLayout</c> 分不出谁是谁。
/// 唯一可靠的判据是 TSF 的「当前激活语言配置」。</para>
/// <para>诊断必须能回答「现在生效的是哪一个」，否则测出来的结论可能属于另一个输入法——
/// 搜索候选项、组字窗类名这些特征在两个输入法之间完全不同。</para>
/// </remarks>
internal static class ImeProfile
{
    private const ushort SimplifiedChinese = 0x0804;

    /// <summary>ITfInputProcessorProfiles 的 CLSID。</summary>
    private static readonly Guid ClassId = new("33C53A50-F456-4884-B049-85FD643ECFED");

    /// <summary>已注册的语言配置（来自注册表）。</summary>
    /// <param name="Clsid">输入法类标识。</param>
    /// <param name="ProfileId">语言配置标识。</param>
    /// <param name="Description">注册表里的说明文字。</param>
    public readonly record struct Entry(Guid Clsid, Guid ProfileId, string Description);

    /// <summary>枚举本机在简体中文下注册的全部输入法。</summary>
    /// <returns>语言配置列表。</returns>
    public static List<Entry> EnumerateInstalled()
    {
        var result = new List<Entry>();

        try
        {
            using var tip = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\CTF\TIP");
            if (tip is null)
            {
                return result;
            }

            foreach (var clsidName in tip.GetSubKeyNames())
            {
                if (!Guid.TryParse(clsidName, out var clsid))
                {
                    continue;
                }

                using var languageProfiles = tip.OpenSubKey($@"{clsidName}\LanguageProfile\0x00000804");
                if (languageProfiles is null)
                {
                    continue;
                }

                foreach (var profileName in languageProfiles.GetSubKeyNames())
                {
                    if (!Guid.TryParse(profileName, out var profileId))
                    {
                        continue;
                    }

                    using var profile = languageProfiles.OpenSubKey(profileName);
                    var description = profile?.GetValue("Description") as string ?? string.Empty;
                    result.Add(new Entry(clsid, profileId, description));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[输入法] 读取注册表失败：{ex.Message}");
        }

        return result;
    }

    /// <summary>查询当前激活的语言配置。</summary>
    /// <param name="verbose">是否打印每次调用的 HRESULT，排查用。</param>
    /// <returns>激活的输入法；查不到返回 null。</returns>
    public static Entry? GetActive(bool verbose = false)
    {
        var profiles = CreateProfiles();
        if (profiles is null)
        {
            return null;
        }

        Entry? found = null;

        foreach (var entry in EnumerateInstalled())
        {
            var clsid = entry.Clsid;
            var hr = profiles.GetActiveLanguageProfile(ref clsid, out var langId, out var activeProfile);
            if (verbose)
            {
                Console.WriteLine($"[输入法] GetActiveLanguageProfile({entry.Description}) hr=0x{hr:X8} " +
                                  $"lang=0x{langId:X4} profile={activeProfile}");
            }

            if (found is null && activeProfile != Guid.Empty && activeProfile == entry.ProfileId)
            {
                found = entry;
            }
        }

        return found;
    }

    /// <summary>把指定输入法切为当前生效的语言配置。</summary>
    /// <param name="entry">目标输入法。</param>
    /// <returns>调用是否成功（不代表已经切换到位，调用方应再用 <see cref="GetActive"/> 复核）。</returns>
    public static bool Activate(Entry entry)
    {
        var profiles = CreateProfiles();
        if (profiles is null)
        {
            return false;
        }

        var clsid = entry.Clsid;
        var profileId = entry.ProfileId;
        var hr = profiles.ActivateLanguageProfile(ref clsid, SimplifiedChinese, ref profileId);
        if (hr != 0)
        {
            Console.WriteLine($"[输入法] 激活失败 hr=0x{hr:X8}");
            return false;
        }

        return true;
    }

    /// <summary>按说明文字匹配一个已安装的输入法。</summary>
    /// <param name="keyword">说明文字里包含的关键词，例如 <c>微软拼音</c> 或 <c>Microsoft Pinyin</c>。</param>
    /// <returns>匹配到的输入法；没有则返回 null。</returns>
    public static Entry? FindByDescription(string keyword)
    {
        foreach (var entry in EnumerateInstalled())
        {
            if (entry.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static ITfInputProcessorProfiles? CreateProfiles()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ClassId);
            return type is null ? null : Activator.CreateInstance(type) as ITfInputProcessorProfiles;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[输入法] 创建 TSF 对象失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// <c>ITfInputProcessorProfiles</c> 的最小投影：方法顺序必须与 shobjidl/msctf.h 完全一致，
    /// 否则调用会落到别的函数上。
    /// </summary>
    [ComImport]
    [Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfiles
    {
        [PreserveSig]
        int Register([In] ref Guid clsid);

        [PreserveSig]
        int Unregister([In] ref Guid clsid);

        [PreserveSig]
        int AddLanguageProfile(
            [In] ref Guid clsid,
            ushort langId,
            [In] ref Guid profileId,
            [MarshalAs(UnmanagedType.LPWStr)] string description,
            uint descriptionLength,
            [MarshalAs(UnmanagedType.LPWStr)] string iconFile,
            uint iconFileLength,
            uint iconIndex);

        [PreserveSig]
        int RemoveLanguageProfile([In] ref Guid clsid, ushort langId, [In] ref Guid profileId);

        [PreserveSig]
        int EnumInputProcessorInfo(out IntPtr enumGuid);

        [PreserveSig]
        int GetDefaultLanguageProfile(ushort langId, [In] ref Guid categoryId, out Guid clsid, out Guid profileId);

        [PreserveSig]
        int SetDefaultLanguageProfile(ushort langId, [In] ref Guid clsid, [In] ref Guid profileId);

        [PreserveSig]
        int ActivateLanguageProfile([In] ref Guid clsid, ushort langId, [In] ref Guid profileId);

        [PreserveSig]
        int GetActiveLanguageProfile([In] ref Guid clsid, out ushort langId, out Guid profileId);

        [PreserveSig]
        int GetLanguageProfileDescription(
            [In] ref Guid clsid,
            ushort langId,
            [In] ref Guid profileId,
            [MarshalAs(UnmanagedType.BStr)] out string description);

        [PreserveSig]
        int GetCurrentLanguage(out ushort langId);

        [PreserveSig]
        int ChangeCurrentLanguage(ushort langId);

        [PreserveSig]
        int GetLanguageList(out IntPtr langIds, out uint count);

        [PreserveSig]
        int EnumLanguageProfiles(ushort langId, out IntPtr enumProfiles);

        [PreserveSig]
        int EnableLanguageProfile([In] ref Guid clsid, ushort langId, [In] ref Guid profileId, int enable);

        [PreserveSig]
        int IsEnabledLanguageProfile([In] ref Guid clsid, ushort langId, [In] ref Guid profileId, out int enabled);

        [PreserveSig]
        int EnableLanguageProfileByDefault([In] ref Guid clsid, ushort langId, [In] ref Guid profileId, int enable);

        [PreserveSig]
        int SubstituteKeyboardLayout([In] ref Guid clsid, ushort langId, [In] ref Guid profileId, IntPtr layout);
    }
}

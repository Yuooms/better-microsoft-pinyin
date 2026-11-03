using BetterPinyin.Contracts;
using BetterPinyin.Core.Pinyin;
using BetterPinyin.Core.Settings;
using BetterPinyin.Interop.Ime;

namespace BetterPinyin.App.Features.CloudAssist;

/// <summary>当前的输入状态。</summary>
internal enum InputMode
{
    /// <summary>不联想。</summary>
    None = 0,

    /// <summary>输入法正在组字。</summary>
    Chinese = 1,

    /// <summary>英文状态：字母已经上屏，采纳需要先删除。</summary>
    English = 2,
}

/// <summary>
/// 判断当前该不该联想、以及用哪种上屏方式。
/// </summary>
/// <remarks>
/// 拿不到 TSF 的中英文转换状态，只能分层判断。候选窗是否可见是第一层，
/// 但它不是唯一判据：用户可能关了候选窗、高权限进程读不到 UIA，
/// 这两种情况都不能当成英文模式——那会导致删掉正文。因此英文模式还要求：
/// 键盘布局是中文、输入串能完整切分成至少两个音节（<c>hello</c> 不行、<c>name</c> 可以）。
/// 最后上屏前还有一道文本校验，见 <c>CloudAssistController.CommitEnglishAsync</c>。
/// </remarks>
internal static class InputModeResolver
{
    /// <summary>解析当前输入状态。</summary>
    /// <param name="snapshot">候选窗快照。</param>
    /// <param name="raw">重建出的输入串。</param>
    /// <param name="settings">设置。</param>
    /// <returns>输入状态。</returns>
    public static InputMode Resolve(in CandidateWindowSnapshot snapshot, string raw, AppSettings settings)
    {
        if (snapshot.IsVisible)
        {
            return InputMode.Chinese;
        }

        if (!settings.EnglishModeAssistEnabled || !AppServices.UiaAvailable)
        {
            return InputMode.None;
        }

        // 切到别的语言布局时，按键根本不进微软拼音，不必联想。
        if (!MsPinyinDetector.IsChineseLayout())
        {
            return InputMode.None;
        }

        // 至少两个音节，挡掉 hello、world 这类英文单词的误报。
        if (!SyllableTable.Default.TrySegment(raw, settings.FuzzyPinyin, out var syllables) || syllables < 2)
        {
            return InputMode.None;
        }

        return InputMode.English;
    }
}

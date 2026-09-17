using System.Text;

namespace IncidentCompass.Application.Memory;

/// <summary>
/// Decides a word's writing system from the Unicode block its first letter falls in. .NET exposes no
/// Unicode Script property, so these explicit block ranges are the auditable substitute: enough to tell
/// the scripts a runbook corpus and a worker query are realistically written in apart, and deliberately
/// not a general Unicode script database. The word is walked as runes rather than UTF-16 units, because
/// a supplementary-plane letter is a surrogate pair and <c>char.IsLetter</c> is false for either half,
/// which would classify a whole word in such a script as carrying no letter at all. A block this list
/// does not name resolves to <see cref="WritingScript.Other" />, and a word with no letter in any plane
/// to <see cref="WritingScript.Neutral" />.
/// </summary>
internal static class WritingScriptClassifier
{
    public static WritingScript Classify(string word)
    {
        foreach (var rune in word.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                return ClassifyLetter(rune.Value);
            }
        }

        return WritingScript.Neutral;
    }

    // Only reached for a code point that is already a letter, so the ASCII range below is the ASCII
    // letters and the Latin-1 range excludes the punctuation signs that share its block.
    private static WritingScript ClassifyLetter(int codePoint) => codePoint switch
    {
        <= 0x007f => WritingScript.Latin,
        >= 0x00c0 and <= 0x024f => WritingScript.Latin,
        >= 0x1e00 and <= 0x1eff => WritingScript.Latin,
        >= 0x0370 and <= 0x03ff => WritingScript.Greek,
        >= 0x1f00 and <= 0x1fff => WritingScript.Greek,
        >= 0x0400 and <= 0x052f => WritingScript.Cyrillic,
        >= 0x0590 and <= 0x05ff => WritingScript.Hebrew,
        >= 0x0600 and <= 0x06ff => WritingScript.Arabic,
        >= 0x0750 and <= 0x077f => WritingScript.Arabic,
        >= 0x08a0 and <= 0x08ff => WritingScript.Arabic,
        >= 0x1100 and <= 0x11ff => WritingScript.Hangul,
        >= 0x3040 and <= 0x309f => WritingScript.Hiragana,
        >= 0x30a0 and <= 0x30ff => WritingScript.Katakana,
        >= 0x3130 and <= 0x318f => WritingScript.Hangul,
        >= 0x31f0 and <= 0x31ff => WritingScript.Katakana,
        >= 0x3400 and <= 0x4dbf => WritingScript.Han,
        >= 0x4e00 and <= 0x9fff => WritingScript.Han,
        >= 0xac00 and <= 0xd7af => WritingScript.Hangul,
        >= 0xf900 and <= 0xfaff => WritingScript.Han,
        // The supplementary CJK ideograph blocks, extensions B through H and the compatibility
        // supplement, run contiguously enough that one range is honest here.
        >= 0x20000 and <= 0x323af => WritingScript.Han,
        _ => WritingScript.Other
    };
}

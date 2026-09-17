namespace IncidentCompass.Application.Memory;

/// <summary>
/// The writing system one counted word is written in. <see cref="Neutral" /> is a word that carries no
/// letter at all, such as a number or a version, which belongs to no script and can occur in text of
/// any script.
/// </summary>
internal enum WritingScript
{
    Neutral,
    Latin,
    Cyrillic,
    Greek,
    Arabic,
    Hebrew,
    Han,
    Hiragana,
    Katakana,
    Hangul,
    Other
}

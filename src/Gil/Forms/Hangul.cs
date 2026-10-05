using System.Text;

namespace Gil.Forms;

/// <summary>
/// Hangul as it is typed. A syllable is composed from letters (jamo) one keystroke at a time — an initial consonant, a
/// vowel, a final consonant — and on the standard keyboard a compound vowel or final takes two keystrokes (ㅘ is ㅗ then
/// ㅏ, ㄺ is ㄹ then ㄱ), while a doubled consonant or ㅒ · ㅖ takes one. The keystrokes of a text are what a person
/// partway through typing it has entered, which a prefix of its characters is not: "바" is on the way to "박".
/// </summary>
internal static class Hangul
{
    private const int First = 0xAC00;
    private const int Last = 0xD7A3;

    private static readonly string[] Initials =
        ["ㄱ", "ㄲ", "ㄴ", "ㄷ", "ㄸ", "ㄹ", "ㅁ", "ㅂ", "ㅃ", "ㅅ", "ㅆ", "ㅇ", "ㅈ", "ㅉ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ"];

    private static readonly string[] Vowels =
        ["ㅏ", "ㅐ", "ㅑ", "ㅒ", "ㅓ", "ㅔ", "ㅕ", "ㅖ", "ㅗ", "ㅗㅏ", "ㅗㅐ", "ㅗㅣ", "ㅛ", "ㅜ", "ㅜㅓ", "ㅜㅔ", "ㅜㅣ", "ㅠ", "ㅡ", "ㅡㅣ", "ㅣ"];

    private static readonly string[] Finals =
        ["", "ㄱ", "ㄲ", "ㄱㅅ", "ㄴ", "ㄴㅈ", "ㄴㅎ", "ㄷ", "ㄹ", "ㄹㄱ", "ㄹㅁ", "ㄹㅂ", "ㄹㅅ", "ㄹㅌ", "ㄹㅍ", "ㄹㅎ", "ㅁ", "ㅂ", "ㅂㅅ", "ㅅ", "ㅆ", "ㅇ", "ㅈ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ"];

    /// <summary>Compatibility jamo typed on their own (ㄳ, ㅘ …), split as the syllables split them.</summary>
    private static readonly Dictionary<char, string> Compound = new()
    {
        ['ㄳ'] = "ㄱㅅ", ['ㄵ'] = "ㄴㅈ", ['ㄶ'] = "ㄴㅎ", ['ㄺ'] = "ㄹㄱ", ['ㄻ'] = "ㄹㅁ", ['ㄼ'] = "ㄹㅂ", ['ㄽ'] = "ㄹㅅ",
        ['ㄾ'] = "ㄹㅌ", ['ㄿ'] = "ㄹㅍ", ['ㅀ'] = "ㄹㅎ", ['ㅄ'] = "ㅂㅅ",
        ['ㅘ'] = "ㅗㅏ", ['ㅙ'] = "ㅗㅐ", ['ㅚ'] = "ㅗㅣ", ['ㅝ'] = "ㅜㅓ", ['ㅞ'] = "ㅜㅔ", ['ㅟ'] = "ㅜㅣ", ['ㅢ'] = "ㅡㅣ",
    };

    /// <summary>Whether <paramref name="text"/> has a Hangul syllable or letter in it.</summary>
    public static bool Has(string text)
    {
        foreach (var c in text)
        {
            if (c is >= (char)First and <= (char)Last or >= 'ㄱ' and <= 'ㅣ')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The keystrokes that type <paramref name="text"/>: each Hangul syllable as its letters, other characters as they are.</summary>
    public static string Keystrokes(string text)
    {
        var keys = new StringBuilder(text.Length * 3);
        foreach (var c in text)
        {
            if (c is >= (char)First and <= (char)Last)
            {
                var index = c - First;
                keys.Append(Initials[index / 588]).Append(Vowels[index % 588 / 28]).Append(Finals[index % 28]);
            }
            else if (Compound.TryGetValue(c, out var split))
            {
                keys.Append(split);
            }
            else
            {
                keys.Append(c);
            }
        }

        return keys.ToString();
    }
}

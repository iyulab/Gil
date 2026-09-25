using System.Security.Cryptography;
using System.Text;

namespace Gil.Habits;

/// <summary>
/// Rules shared by candidates learned from the log (promoted habits and shadows), so every implementation reading the
/// same log derives the same ids and labels.
/// </summary>
public static class DerivedHabits
{
    public const string PromotedPrefix = "promoted-";

    private const int LabelLength = 40;

    /// <summary>The prefix and the first ten hex digits of sha1 over the anchor id, a newline and the output.</summary>
#pragma warning disable CA5350 // An identifier, not a security boundary: it must match the ids other implementations derive.
    public static string Id(string prefix, string anchor, string text) =>
        prefix + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{anchor}\n{text}")))[..10];
#pragma warning restore CA5350

    /// <summary>The output's first forty characters, counted by code point so that none is split.</summary>
    public static string Label(string text)
    {
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes().Take(LabelLength))
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>Length in code points, the unit characters are counted in across implementations.</summary>
    public static int Length(string? text) => text?.EnumerateRunes().Count() ?? 0;
}

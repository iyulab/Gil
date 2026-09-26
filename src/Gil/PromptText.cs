using System.Text.RegularExpressions;

namespace Gil;

/// <summary>Fills a <see cref="PromptLanguage"/> template in one pass, so inserted text is never read as a placeholder.</summary>
internal static partial class PromptText
{
    public static string Fill(string template, params (string Name, string Value)[] values) =>
        Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            foreach (var (key, value) in values)
            {
                if (key == name)
                {
                    return value;
                }
            }

            return match.Value;
        });

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}

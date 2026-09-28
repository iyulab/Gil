using System.Text;

namespace Gil.Memory;

/// <summary>The one text normalisation every memory that compares text by its characters uses.</summary>
internal static class TextNormal
{
    /// <summary>NFKC, lower case, every run of whitespace one space, no space at either end.</summary>
    public static string Collapse(string text)
    {
        var normal = new StringBuilder(text.Length);
        var space = true;
        foreach (var c in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && normal.Length > 0)
            {
                normal.Append(' ');
            }

            normal.Append(c);
            space = false;
        }

        return normal.ToString();
    }
}

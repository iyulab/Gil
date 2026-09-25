using System.Security.Cryptography;
using System.Text;
using Gil.Judge;

namespace Gil.Habits;

/// <summary>
/// Shadows: answers already known at a node that are not habits yet, shown beside its habits at the leaf judgment.
/// Without them a judgment whose right answer is missing picks a sibling that shares its topic, with high confidence;
/// that request becomes a wrong habit answer, never reaches the fallback, and so its answer never gains the evidence to
/// be promoted — absorption seals itself. Choosing a shadow does not answer: it defers to the fallback. A shadow built on
/// weak evidence therefore cannot produce a wrong answer; the cost is one fallback.
/// </summary>
/// <remarks>
/// Shadows are runtime state derived from the log, not part of the tree: in the tree, review, promotion, attribution
/// and deactivation would read them as habits.
/// </remarks>
public static class ShadowIndex
{
    public const string Prefix = "shadow-";

    private const int LabelLength = 40;

    /// <summary>A stable id for an anchor's shadow, the same across implementations sharing a log.</summary>
#pragma warning disable CA5350 // An identifier, not a security boundary: it must match the ids other implementations derive.
    public static string Id(string anchor, string text) =>
        Prefix + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{anchor}\n{text}")))[..10];
#pragma warning restore CA5350

    public static bool IsShadow(string? candidateId) => candidateId?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The output known (or suspected) to be right for a request; null when unknown. A wrong answer's correction is
    /// right; a fallback or partial answer confirmed correct is right (a habit's confirmed answer is already in the
    /// tree). Without feedback, an exploration that disagreed with the answer is the only evidence — unverified, but a
    /// shadow only defers. Feedback wins over exploration.
    /// </summary>
    public static string? KnownAnswer(string? mode, string? output, string? verdict, string? correction, string? explored = null) =>
        verdict switch
        {
            "wrong" => string.IsNullOrEmpty(correction) ? null : correction.Trim(),
            "correct" => mode is "fallback" or "partial" && !string.IsNullOrEmpty(output) ? output.Trim() : null,
            _ => !string.IsNullOrEmpty(explored) && !string.IsNullOrEmpty(output) && explored.Trim() != output.Trim() ? explored.Trim() : null,
        };

    /// <summary>
    /// Shadows per anchor, from a task's evidence in arrival order. Only nodes with habits get them — a node without
    /// habits is skipped without a judgment, and a judgment over shadows alone defers whatever it picks. When habits
    /// plus shadows exceed the label scheme, the best-supported shadows stay (ties: the one seen first).
    /// </summary>
    /// <param name="evidence">The task's recorded requests (<see cref="IShadowEvidenceSource"/>), in arrival order.</param>
    /// <param name="root">The task's tree.</param>
    /// <param name="exclude">Outputs that mean "no answer"; as candidates they would duplicate "none of these".</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<Candidate>> Build(
        IEnumerable<ShadowEvidence> evidence, Node root, IReadOnlySet<string>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(root);
        var taken = root.Walk().SelectMany(n => n.Habits).Select(h => h.Text).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var support = new Dictionary<string, List<(string Text, string Example, int Count)>>(StringComparer.Ordinal);
        foreach (var row in evidence)
        {
            var answer = KnownAnswer(row.Mode, row.Output, row.Verdict, row.Correction, row.Explored);
            if (answer is null || taken.Contains(answer) || exclude?.Contains(answer) == true)
            {
                continue;
            }

            if (!support.TryGetValue(row.Anchor, out var counts))
            {
                support[row.Anchor] = counts = [];
            }

            // The description is the first input behind it — the same rule as a promoted option: an example, no authored gloss.
            var at = counts.FindIndex(c => c.Text == answer);
            if (at < 0)
            {
                counts.Add((answer, row.State, 1));
            }
            else
            {
                counts[at] = counts[at] with { Count = counts[at].Count + 1 };
            }
        }

        var index = new Dictionary<string, IReadOnlyList<Candidate>>(StringComparer.Ordinal);
        foreach (var (anchor, counts) in support)
        {
            if (root.Find(anchor) is not { Habits.Count: > 0 } node)
            {
                continue;
            }

            var room = LabelScheme.Capacity(node.LabelScheme) - node.Habits.Count;
            if (room <= 0)
            {
                continue;
            }

            // OrderBy is stable: equal support keeps first-seen order.
            index[anchor] = [.. counts.OrderByDescending(c => c.Count).Take(room)
                .Select(c => new Candidate(Id(anchor, c.Text), Truncate(c.Text), c.Example, c.Text))];
        }

        return index;
    }

    /// <summary>The first characters by code point, so a label never splits a character.</summary>
    private static string Truncate(string text)
    {
        var runes = text.EnumerateRunes().Take(LabelLength);
        var builder = new StringBuilder();
        foreach (var rune in runes)
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}

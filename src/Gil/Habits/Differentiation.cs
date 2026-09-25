using Gil.Judge;

namespace Gil.Habits;

/// <summary>Why a node may need splitting or a new category.</summary>
public enum DifferentiationKind
{
    /// <summary>The node holds as many candidates as its label scheme can label: no habit can be added; it needs intermediate categories.</summary>
    Capacity,

    /// <summary>The repeated answer is already in the tree: a layer above failed to find the way (thresholds, descriptions).</summary>
    Missed,

    /// <summary>Only one child's narrowed contract accepts the answer: it belongs under that child and piled up here because a layer above exited.</summary>
    Reanchor,

    /// <summary>No child accepts it: a new category is needed — genuine differentiation.</summary>
    Orphan,
}

/// <summary>A differentiation signal, for a person to act on. How to split is theirs to decide.</summary>
/// <param name="Kind">What the signal says.</param>
/// <param name="Node">The full node (capacity) or the anchor the answers piled up at.</param>
/// <param name="Output">The repeated confirmed answer; null for capacity.</param>
/// <param name="Target">Missed: the node holding the answer. Reanchor: the child that accepts it. Otherwise null.</param>
/// <param name="Support">Capacity: the node's candidates. Otherwise: how often the answer repeated.</param>
/// <param name="Sources">The requests behind it.</param>
public sealed record DifferentiationSignal(
    DifferentiationKind Kind,
    string Node,
    string? Output,
    string? Target,
    int Support,
    IReadOnlyList<string> Sources);

/// <summary>
/// Signals for what promotion cannot handle: a node that can take no more habits, and answers that keep coming back
/// at an anchor with children (which takes no habits). Promotion used to drop these silently.
/// </summary>
public static class Differentiation
{
    /// <summary>Nodes whose candidates reach the label scheme's capacity less <paramref name="headroom"/> — headroom warns before they are full.</summary>
    public static IReadOnlyList<DifferentiationSignal> Capacity(Node root, int headroom = 0)
    {
        ArgumentNullException.ThrowIfNull(root);
        return [.. root.Walk()
            .Where(node => node.CandidateIds.Count > 0 && node.CandidateIds.Count >= LabelScheme.Capacity(node.LabelScheme) - headroom)
            .Select(node => new DifferentiationSignal(DifferentiationKind.Capacity, node.Id, null, null, node.CandidateIds.Count, []))];
    }

    /// <summary>
    /// Confirmed fallback answers repeated at least <paramref name="minSupport"/> times at an anchor with children, told
    /// apart by cause. Within an anchor, the most repeated first (ties: first seen).
    /// </summary>
    /// <param name="root">The task's tree.</param>
    /// <param name="candidates">The task's promotion evidence (<see cref="IPromotionEvidenceSource.PromotionCandidates"/>).</param>
    /// <param name="contract">The task's output contract; a narrowable one tells reanchor from orphan.</param>
    /// <param name="minSupport">Repetitions below this are chance.</param>
    /// <param name="never">Answers that mean there is no answer.</param>
    public static IReadOnlyList<DifferentiationSignal> Anchored(
        Node root,
        IReadOnlyList<PromotionCandidate> candidates,
        IOutputContract contract,
        int minSupport,
        IReadOnlySet<string>? never = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(contract);

        // Where each answer lives; a text held twice counts as the later node, as the tree is walked.
        var home = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in root.Walk())
        {
            foreach (var habit in node.Habits.Where(h => h.Text is not null))
            {
                home[habit.Text!] = node.Id;
            }
        }

        var signals = new List<DifferentiationSignal>();
        foreach (var group in candidates.GroupBy(c => c.Anchor, StringComparer.Ordinal))
        {
            if (root.Find(group.Key) is not { Children.Count: > 0 } node)
            {
                continue;
            }

            var clusters = new List<(string Output, List<string> Sources)>();
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var candidate in group.Where(c => c.Verdict == "correct" && c.Output is not null))
            {
                var output = candidate.Output!.Trim();
                if (!at.TryGetValue(output, out var index))
                {
                    at[output] = index = clusters.Count;
                    clusters.Add((output, []));
                }

                clusters[index].Sources.Add(candidate.TraceId);
            }

            foreach (var (output, sources) in clusters.OrderByDescending(c => c.Sources.Count))
            {
                if (sources.Count < minSupport || never?.Contains(output) == true)
                {
                    continue;
                }

                var (kind, target) = home.TryGetValue(output, out var holder)
                    ? (DifferentiationKind.Missed, holder)
                    : Takers(node, output, contract) is [var only] ? (DifferentiationKind.Reanchor, only) : (DifferentiationKind.Orphan, (string?)null);
                signals.Add(new DifferentiationSignal(kind, group.Key, output, target, sources.Count, sources));
            }
        }

        return signals;
    }

    /// <summary>The children whose narrowed contract accepts the answer as an answer, not as its escape.</summary>
    private static List<string> Takers(Node node, string output, IOutputContract contract) =>
        contract is IScopableContract scopable
            ? [.. node.Children.Where(child => scopable.Scoped(child.Id) is { } scoped
                && output != scoped.Escape && scoped.Contract.Validate(output) is null).Select(child => child.Id)]
            : [];
}

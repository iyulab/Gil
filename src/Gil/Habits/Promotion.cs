using Gil.Judge;
using Gil.Ontology;

namespace Gil.Habits;

/// <summary>What promotion accepts as a habit.</summary>
/// <param name="MinSupport">Outputs repeated fewer times than this are chance, not habit.</param>
public sealed record PromotionPolicy(int MinSupport)
{
    /// <summary>Outputs that mean there is no answer; as habits they would duplicate the judgment's "none of these".</summary>
    public IReadOnlySet<string> NeverPromote { get; init; } = new HashSet<string>();

    /// <summary>
    /// Also count a wrong fallback's correction as a confirmed output. Answers the fallback cannot know (an
    /// organisation's own guidance) arrive only as corrections, so free-answer tasks need this. Off by default:
    /// turning it on changes what the same log proposes.
    /// </summary>
    public bool FromCorrections { get; init; }
}

/// <summary>
/// The energy of one judgment as a line over the characters it reads: <c>fixed + perChar × (input + shown labels and
/// descriptions)</c>, fitted on a task's judgments. It prices a first habit on an empty anchor by its own size
/// rather than by the mean judgment, which cold starts measure only at the crowded root.
/// </summary>
public sealed record JudgeCostModel(double Fixed, double PerChar, double MeanStateChars, int NoneChars)
{
    /// <summary>A judgment that did not happen before: the anchor had no habits and was skipped.</summary>
    public double NewCall(int optionChars) => Fixed + (PerChar * (MeanStateChars + NoneChars + optionChars));

    /// <summary>One more candidate on a judgment that already happens.</summary>
    public double Marginal(int optionChars) => PerChar * optionChars;

    /// <summary>Least squares; null unless at least two judgments differ in size.</summary>
    public static JudgeCostModel? Fit(IReadOnlyList<JudgeCostSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Select(s => s.Chars).Distinct().Count() < 2)
        {
            return null;
        }

        double sumX = 0, sumY = 0, sumState = 0;
        var noneChars = 0;
        foreach (var sample in samples)
        {
            (sumX, sumY, sumState) = (sumX + sample.Chars, sumY + sample.Energy, sumState + sample.StateChars);
            noneChars = Math.Max(noneChars, sample.NoneChars);
        }

        var (meanX, meanY) = (sumX / samples.Count, sumY / samples.Count);
        double covariance = 0, variance = 0;
        foreach (var sample in samples)
        {
            covariance += (sample.Chars - meanX) * (sample.Energy - meanY);
            variance += (sample.Chars - meanX) * (sample.Chars - meanX);
        }

        var slope = covariance / variance;
        return new JudgeCostModel(meanY - (slope * meanX), slope, sumState / samples.Count, noneChars);
    }
}

/// <summary>
/// Proposes a habit when a confirmed fallback output keeps recurring at the same anchor, and only when it pays: the
/// expected saving (hit rate × fallback energy) must exceed the judgment energy the new candidate adds.
/// </summary>
/// <remarks>
/// What counts as the same output is the grouping's call. The default groups identical text, which suits closed
/// outputs (classification, choice); free-form answers need a grouping by meaning.
/// </remarks>
public sealed class RepeatedOutputProposer : IPromotionProposer
{
    private readonly PromotionPolicy _policy;
    private readonly IReadOnlyDictionary<string, double> _judgeEnergy;
    private readonly JudgeCostModel? _costModel;
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<(string Output, IReadOnlyList<int> Positions)>> _grouping;
    private readonly Func<string, IReadOnlyList<string>, bool>? _sameAsExisting;
    private readonly double _newCallEnergy;

    /// <param name="policy">What counts as a habit.</param>
    /// <param name="judgeEnergy">Mean energy of one judgment per node (<see cref="IPromotionEvidenceSource.JudgeEnergyByNode"/>).</param>
    /// <param name="costModel">When given, the added judgment energy is priced by the new habit's size; otherwise by measured means.</param>
    /// <param name="grouping">Confirmed outputs → groups (representative output, positions in the input); identical text by default.</param>
    /// <param name="sameAsExisting">
    /// When given, a group whose output means the same as one of the anchor's existing habits is not proposed either.
    /// Two same-meaning siblings split the judgment's probability and neither clears the threshold.
    /// </param>
    public RepeatedOutputProposer(
        PromotionPolicy policy,
        IReadOnlyDictionary<string, double> judgeEnergy,
        JudgeCostModel? costModel = null,
        Func<IReadOnlyList<string>, IReadOnlyList<(string Output, IReadOnlyList<int> Positions)>>? grouping = null,
        Func<string, IReadOnlyList<string>, bool>? sameAsExisting = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(judgeEnergy);
        (_policy, _judgeEnergy, _costModel, _sameAsExisting) = (policy, judgeEnergy, costModel, sameAsExisting);
        _grouping = grouping ?? ExactGrouping;
        _newCallEnergy = judgeEnergy.Count > 0 ? judgeEnergy.Values.Sum() / judgeEnergy.Count : 0;
    }

    /// <summary>Groups the last <see cref="Propose"/> turned down, by reason: <c>min_support</c>, <c>same_as_existing</c>, <c>energy</c>.</summary>
    public IReadOnlyDictionary<string, int> Rejected { get; private set; } = new Dictionary<string, int>();

    /// <summary>Identical text is one group, in order of first appearance.</summary>
    public static IReadOnlyList<(string Output, IReadOnlyList<int> Positions)> ExactGrouping(IReadOnlyList<string> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var groups = new List<(string Output, List<int> Positions)>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < outputs.Count; i++)
        {
            if (!at.TryGetValue(outputs[i], out var group))
            {
                at[outputs[i]] = group = groups.Count;
                groups.Add((outputs[i], []));
            }

            groups[group].Positions.Add(i);
        }

        return [.. groups.Select(g => (g.Output, (IReadOnlyList<int>)g.Positions))];
    }

    public IReadOnlyList<PromotionProposal> Propose(Node root, IReadOnlyList<PromotionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(candidates);
        var taken = root.Walk().SelectMany(n => n.Habits).Where(h => h.Kind == HabitKind.Answer && h.Text is not null)
            .Select(h => h.Text!).ToHashSet(StringComparer.Ordinal);
        var rejected = new Dictionary<string, int>();
        var proposals = new List<PromotionProposal>();
        foreach (var group in candidates.GroupBy(c => c.Anchor, StringComparer.Ordinal))
        {
            // A node with children cannot take habits: that needs a new category (differentiation), not a promotion.
            if (root.Find(group.Key) is not { Children.Count: 0 } node)
            {
                continue;
            }

            var members = group.ToList();
            var confirmed = members.Select(c => (Candidate: c, Output: Confirmed(c))).Where(c => c.Output is not null).ToList();
            foreach (var (output, positions) in _grouping([.. confirmed.Select(c => c.Output!)]))
            {
                var supporting = positions.Select(i => confirmed[i].Candidate).ToList();
                var outputs = positions.Select(i => confirmed[i].Output!).ToHashSet(StringComparer.Ordinal);

                // Already an answer in the tree: here, the judgment missed it; elsewhere, a layer above went astray.
                if (outputs.Overlaps(taken) || outputs.Overlaps(_policy.NeverPromote))
                {
                    continue;
                }

                if (supporting.Count < _policy.MinSupport)
                {
                    Count(rejected, "min_support");
                    continue;
                }

                if (_sameAsExisting is not null
                    && _sameAsExisting(output, [.. node.Habits.Select(h => h.Text).OfType<string>()]))
                {
                    Count(rejected, "same_as_existing");
                    continue;
                }

                var proposal = Proposal(node, output, supporting, members.Count);
                if (proposal.NetSaving > 0)
                {
                    proposals.Add(proposal);
                }
                else
                {
                    Count(rejected, "energy");
                }
            }
        }

        Rejected = rejected;
        return [.. proposals.OrderByDescending(p => p.NetSaving)];
    }

    private static void Count(Dictionary<string, int> counts, string reason) =>
        counts[reason] = counts.GetValueOrDefault(reason) + 1;

    /// <summary>The answer feedback settled on; without feedback, or without a contract-meeting output, there is none.</summary>
    private string? Confirmed(PromotionCandidate candidate) =>
        candidate.Verdict switch
        {
            "correct" when candidate.Output is not null => candidate.Output.Trim(),
            "wrong" when _policy.FromCorrections && !string.IsNullOrEmpty(candidate.Correction) => candidate.Correction.Trim(),
            _ => null,
        };

    private PromotionProposal Proposal(Node node, string output, List<PromotionCandidate> supporting, int volume)
    {
        var habit = new Habit
        {
            Id = DerivedHabits.Id(DerivedHabits.PromotedPrefix, node.Id, output),
            Kind = HabitKind.Answer,
            Label = DerivedHabits.Label(output),
            // The description is the first input behind it — an example, as seeded habits have, not an authored gloss.
            Description = supporting[0].State,
            Text = output,
            Origin = "promoted",
        };

        double fallback = 0;
        foreach (var candidate in supporting)
        {
            fallback += candidate.FallbackEnergy;
        }

        fallback /= supporting.Count;
        var chars = DerivedHabits.Length(habit.Label) + DerivedHabits.Length(habit.Description);
        var added = _costModel is not null
            ? node.Habits.Count > 0 ? _costModel.Marginal(chars) : _costModel.NewCall(chars)
            : node.Habits.Count > 0
                ? _judgeEnergy.GetValueOrDefault(node.Id, _newCallEnergy) / node.Habits.Count
                : _newCallEnergy;

        var warnings = new List<string>();
        OntologyYaml.WarnOnSiblingOverlap(node with { Habits = [.. node.Habits, habit] }, warnings);
        return new PromotionProposal(
            node.Id,
            habit,
            [.. supporting.Select(c => c.TraceId)],
            supporting.Count,
            volume,
            (double)supporting.Count / volume * fallback,
            added,
            [.. warnings.Where(w => w.Contains(habit.Id, StringComparison.Ordinal))]);
    }
}

/// <summary>A promotion round prepared for a person: the tree before and after, as YAML, and what could not be applied.</summary>
/// <param name="Before">The tree as it is.</param>
/// <param name="After">The tree with the proposals applied.</param>
/// <param name="Proposals">What was proposed, with the requests behind each.</param>
/// <param name="NotApplied">Proposals the tree could not take, with the reason.</param>
public sealed record PromotionReview(string Before, string After, IReadOnlyList<PromotionProposal> Proposals, IReadOnlyList<string> NotApplied);

/// <summary>Applying proposals to a tree and preparing them for review.</summary>
public static class Promotion
{
    /// <summary>
    /// The tree with the proposals' habits added, and the proposals it could not take. A node whose label scheme is
    /// full takes no more — a silently mislabelled judgment would break every request there; it needs splitting.
    /// </summary>
    public static (Node Root, IReadOnlyList<string> NotApplied) Apply(Node root, IEnumerable<PromotionProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(proposals);
        var pending = proposals.GroupBy(p => p.Anchor, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(p => p.Habit).ToList());
        var notApplied = new List<string>();
        return (Graft(root), notApplied);

        Node Graft(Node node)
        {
            var children = node.Children.Select(Graft).ToList();
            if (!pending.TryGetValue(node.Id, out var additions))
            {
                return node with { Children = children };
            }

            var habits = node.Habits.ToList();
            var ids = habits.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var habit in additions.Where(h => !ids.Contains(h.Id)))
            {
                if (habits.Count + 1 > LabelScheme.Capacity(node.LabelScheme))
                {
                    notApplied.Add($"{node.Id}: the {node.LabelScheme} scheme is full, so {habit.Id} was not added — the node needs splitting");
                    continue;
                }

                habits.Add(habit);
                ids.Add(habit.Id);
            }

            return node with { Children = children, Habits = habits };
        }
    }

    /// <summary>The proposals as a reviewer receives them: before and after YAML to diff, and each proposal's evidence.</summary>
    public static PromotionReview Review(Node root, IReadOnlyList<PromotionProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        var (after, notApplied) = Apply(root, proposals);
        return new PromotionReview(OntologyYaml.Dump(root), OntologyYaml.Dump(after), proposals, notApplied);
    }
}

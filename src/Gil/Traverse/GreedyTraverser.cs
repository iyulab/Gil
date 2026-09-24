namespace Gil.Traverse;

/// <summary>
/// Acceptance thresholds. Inner layers ask "is the input in this category"; the leaf asks "is this habit a fitting
/// answer" — different questions, so they get separate thresholds. Upper layers are usually strictest: a wrong
/// category derails everything below it.
/// </summary>
public sealed record Thresholds(IReadOnlyList<double> PerLayer, double Leaf)
{
    public double For(int layer, bool isLeaf)
    {
        if (isLeaf)
        {
            return Leaf;
        }

        ArgumentNullException.ThrowIfNull(PerLayer);
        return PerLayer[Math.Min(layer - 1, PerLayer.Count - 1)];
    }
}

/// <summary>
/// Where a traversal ended. <see cref="Habit"/> is set when a habit was accepted; otherwise <see cref="ExitReason"/> says
/// why it stopped: skip, none_selected, untrusted, low_confidence or shadow.
/// </summary>
public sealed record TraverseResult(IReadOnlyList<PathStep> Path, Habit? Habit, string? ExitReason, double Energy, int Calls)
{
    /// <summary>The categories confirmed before the traversal stopped — the context a narrowed fallback works within.</summary>
    public IReadOnlyList<string> Confirmed =>
        [.. Path.TakeWhile(step => step.Outcome == "accept" && step.Chosen is not null).Select(step => step.Chosen!)];
}

/// <summary>
/// Walks the tree from the root, one judgment per layer, and decides only which node to ask next and when to stop.
/// It never calls a model itself — the judge is injected, so the whole walk is testable with recorded judgments.
/// </summary>
public sealed class GreedyTraverser(IJudge judge, Thresholds thresholds)
{
    /// <param name="state">The input to classify.</param>
    /// <param name="root">The tree to walk.</param>
    /// <param name="traceId">The request every judgment is recorded under.</param>
    /// <param name="shadows">
    /// Per node, known answers that are not habits yet. They are shown beside a node's habits; choosing one defers to
    /// the fallback instead of letting a similar-looking sibling habit absorb the request.
    /// </param>
    /// <param name="cancellationToken">Cancels the walk between judgments.</param>
    public async Task<TraverseResult> TraverseAsync(
        string state,
        Node root,
        string traceId,
        IReadOnlyDictionary<string, IReadOnlyList<Candidate>>? shadows = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var steps = new List<PathStep>();
        var current = root;
        var layer = 1;
        var energy = 0.0;
        var calls = 0;
        while (true)
        {
            var isLeaf = current.Habits.Count > 0;
            var shown = current.Children.Count > 0
                ? current.Children.Select(child => new Candidate(child.Id, child.Label, child.Description)).ToList()
                : current.Habits.Select(ToCandidate).ToList();
            if (shown.Count == 0)
            {
                // No habits here yet: stop without a model call, so a skip costs nothing.
                steps.Add(new PathStep(current.Id, layer, null, 0, "skip", new Dictionary<string, double>(), 0));
                return new TraverseResult(steps, null, "skip", energy, calls);
            }

            if (isLeaf && shadows is not null && shadows.TryGetValue(current.Id, out var extra))
            {
                shown.AddRange(extra);
            }

            var judgment = await judge.JudgeAsync(state, shown, traceId, current.Id, layer, cancellationToken).ConfigureAwait(false);
            calls++;
            energy += judgment.Call.Energy;

            var reason = !judgment.Trusted ? "untrusted"
                : judgment.Choice is null ? "none_selected"
                : judgment.Confidence < thresholds.For(layer, isLeaf) ? "low_confidence"
                : null;
            if (reason is not null)
            {
                steps.Add(new PathStep(current.Id, layer, null, judgment.Confidence, "exit", judgment.Probs, judgment.Call.Energy));
                return new TraverseResult(steps, null, reason, energy, calls);
            }

            var child = current.Children.FirstOrDefault(c => c.Id == judgment.Choice);
            var habit = current.Habits.FirstOrDefault(h => h.Id == judgment.Choice);
            if (child is null && habit is null)
            {
                // A shadow: a known answer that is not a habit yet. Defer to the fallback.
                steps.Add(new PathStep(current.Id, layer, judgment.Choice, judgment.Confidence, "defer", judgment.Probs, judgment.Call.Energy));
                return new TraverseResult(steps, null, "shadow", energy, calls);
            }

            steps.Add(new PathStep(current.Id, layer, judgment.Choice, judgment.Confidence, "accept", judgment.Probs, judgment.Call.Energy));
            if (habit is not null)
            {
                return new TraverseResult(steps, habit, null, energy, calls);
            }

            current = child!;
            layer++;
        }
    }

    private static Candidate ToCandidate(Habit habit) =>
        new(habit.Id, habit.Label, habit.Description, habit.Kind == HabitKind.Answer ? habit.Text : null);
}

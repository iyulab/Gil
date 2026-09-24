namespace Gil.Habits;

/// <summary>What one verdict changes in the reliability counts.</summary>
/// <param name="Reinforce">Items confirmed, root first.</param>
/// <param name="Penalize">The one item blamed, if any.</param>
/// <param name="Missed">A habit the tree failed to reach although it held the right answer.</param>
public sealed record Attribution(IReadOnlyList<string> Reinforce, string? Penalize = null, string? Missed = null)
{
    public static readonly Attribution None = new([]);
}

/// <summary>
/// Turns a verdict on a request into credit and blame for the judgments on its path. Each accepted step is scored
/// under the id it chose. Lumping the cases together would turn correction into damage, so they stay apart:
/// <list type="number">
/// <item>A habit answered: confirmed, every judgment on the path is reinforced; wrong, only the <em>first</em> wrong
/// judgment is penalized — those above it were right, and those below it were working on an already derailed path.</item>
/// <item>The tree stopped, the fallback was right, and its answer is an existing habit: nothing is penalized — the
/// habit was right and the judgment failed to find it, so the habit is marked missed.</item>
/// <item>No habit is involved: nothing changes. That is the promotion log's concern.</item>
/// </list>
/// </summary>
public static class HabitAttribution
{
    public static Attribution Attribute(Node root, IReadOnlyList<PathStep> path, string mode, string? output, bool correct, string? correction)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(mode);
        List<string> chosen = [.. path.Where(step => step.Outcome == "accept" && step.Chosen is not null).Select(step => step.Chosen!)];

        if (mode.StartsWith("habit/", StringComparison.Ordinal))
        {
            if (correct)
            {
                return new Attribution(chosen);
            }

            if (correction is null || AnswerLocation(root, correction.Trim()) is not { } truth)
            {
                // Where the right answer lives is unknown: blame only the habit that answered. The layers above may
                // have been wrong too, but they are not penalized without evidence.
                return new Attribution([], chosen.Count > 0 ? chosen[^1] : null);
            }

            for (var depth = 0; depth < chosen.Count; depth++)
            {
                if (depth >= truth.Count || chosen[depth] != truth[depth])
                {
                    return new Attribution(chosen.GetRange(0, depth), chosen[depth]);
                }
            }

            return new Attribution(chosen); // The path leads to the right answer: the judgments were right.
        }

        if (mode is "fallback" or "partial" && correct && output is not null && AnswerLocation(root, output.Trim()) is { } found)
        {
            return new Attribution([], Missed: found[^1]);
        }

        return Attribution.None;
    }

    /// <summary>The ids from the root's children down to the answer habit producing <paramref name="answer"/>, or null.</summary>
    private static List<string>? AnswerLocation(Node node, string answer)
    {
        foreach (var habit in node.Habits)
        {
            if (habit.Kind == HabitKind.Answer && habit.Text == answer)
            {
                return [habit.Id];
            }
        }

        foreach (var child in node.Children)
        {
            if (AnswerLocation(child, answer) is { } below)
            {
                below.Insert(0, child.Id);
                return below;
            }
        }

        return null;
    }
}

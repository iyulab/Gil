namespace Gil.Judge;

/// <summary>The first-token alternatives reduced to the labels the judge was allowed to answer with.</summary>
public sealed record LabelDistribution(IReadOnlyDictionary<string, double> Probs, double LabelMass, string? Choice, double Confidence, bool Trusted)
{
    /// <summary>
    /// Keeps only label tokens (trimmed — chat templates may emit " B"), renormalizes, and trusts the result only if
    /// the model put at least <paramref name="minLabelMass"/> on labels to begin with. Ties go to the label that sorts
    /// first, so the result is deterministic.
    /// </summary>
    public static LabelDistribution From(IEnumerable<TokenLogprob> topLogprobs, IReadOnlyCollection<string> labels, double minLabelMass)
    {
        ArgumentNullException.ThrowIfNull(topLogprobs);
        ArgumentNullException.ThrowIfNull(labels);
        var wanted = labels.ToHashSet(StringComparer.Ordinal);
        var raw = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in topLogprobs)
        {
            var token = item.Token.Trim();
            if (wanted.Contains(token))
            {
                raw[token] = raw.GetValueOrDefault(token) + Math.Exp(item.Logprob);
            }
        }

        var mass = raw.Values.Sum();
        if (mass <= 0)
        {
            return new LabelDistribution(new Dictionary<string, double>(), 0, null, 0, Trusted: false);
        }

        var probs = raw.ToDictionary(p => p.Key, p => p.Value / mass, StringComparer.Ordinal);
        var choice = probs.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
        return new LabelDistribution(probs, mass, choice, probs[choice], mass >= minLabelMass);
    }
}

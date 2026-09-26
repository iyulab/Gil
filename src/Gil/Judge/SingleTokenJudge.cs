using System.Security.Cryptography;
using System.Text;
using Gil.Llm;

namespace Gil.Judge;

/// <summary>Settings for <see cref="SingleTokenJudge"/>.</summary>
public sealed record SingleTokenJudgeOptions
{
    /// <summary>Below this much probability on valid labels, the judgment is not trusted and traversal stops there.</summary>
    public double MinLabelMass { get; init; } = 0.9;

    public string Scheme { get; init; } = LabelScheme.Letters;

    /// <summary>
    /// When set, candidates are shown in an order derived from the request (seed, node, input, candidate ids) to
    /// counter position bias. Deriving it from the request rather than from a running random stream means the same
    /// request always sees the same order — across runs, across compared configurations, and after a resume.
    /// </summary>
    public int? OrderSeed { get; init; }

    public int TopLogprobs { get; init; } = 20;
}

/// <summary>
/// A judge built on any chat model: one generated token, read as a distribution over candidate labels through the
/// first token's log-probabilities. "None of these" is always offered, in the same symbol family as the candidates.
/// </summary>
public sealed class SingleTokenJudge(CallRecorder recorder, SingleTokenJudgeOptions? options = null) : IJudge
{
    private readonly SingleTokenJudgeOptions _options = options ?? new SingleTokenJudgeOptions();

    public async Task<Judgment> JudgeAsync(
        string state,
        IReadOnlyList<Candidate> candidates,
        PromptLanguage language,
        string traceId,
        string? nodeId = null,
        int? layer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(language);
        var ordered = Order(state, candidates, nodeId);
        var none = LabelScheme.NoneLabel(_options.Scheme);
        var labels = LabelScheme.Labels(_options.Scheme, ordered.Count, none);
        var byLabel = labels.Zip(ordered, (label, candidate) => (label, candidate)).ToDictionary(p => p.label, p => p.candidate, StringComparer.Ordinal);
        var request = new ChatRequest
        {
            Messages =
            [
                new ChatMessage("system", language.JudgeSystem),
                new ChatMessage("user", UserPrompt(state, ordered, labels, none, language)),
            ],
            MaxTokens = 1,
            Temperature = 0,
            TopLogprobs = _options.TopLogprobs,
        };

        LabelDistribution? distribution = null;
        var call = await recorder.CompleteAsync(
            request,
            "judge",
            traceId,
            nodeId,
            layer,
            annotate: record =>
            {
                distribution = LabelDistribution.From(record.TopLogprobs, [none, .. labels], _options.MinLabelMass);
                var choice = Choice(distribution, none, byLabel);
                return record with
                {
                    Distribution = ById(distribution, byLabel),
                    LabelMass = distribution.LabelMass,
                    Confidence = distribution.Confidence,
                    Outcome = choice is null ? "exit" : "accept",
                    Candidates =
                    [
                        new ShownCandidate(null, none, language.NoneOfThese, null, null),
                        .. labels.Select(label => new ShownCandidate(byLabel[label].Id, label, byLabel[label].Label, byLabel[label].Description, byLabel[label].Answer)),
                    ],
                };
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var result = distribution!;
        return new Judgment
        {
            Probs = ById(result, byLabel),
            Choice = Choice(result, none, byLabel),
            Confidence = result.Confidence,
            NoneProb = result.Probs.GetValueOrDefault(none),
            LabelMass = result.LabelMass,
            Trusted = result.Trusted,
            Call = call,
        };
    }

    private List<Candidate> Order(string state, IReadOnlyList<Candidate> candidates, string? nodeId)
    {
        if (_options.OrderSeed is not int seed)
        {
            return [.. candidates];
        }

        var ids = string.Join('\u001f', candidates.Select(c => c.Id).Order(StringComparer.Ordinal));
        var request = $"{seed}\u001e{nodeId}\u001e{state}\u001e{ids}";
        return [.. candidates.OrderBy(c => Hash(request + "\u001d" + c.Id), StringComparer.Ordinal)];
    }

    private static string UserPrompt(string state, IReadOnlyList<Candidate> ordered, IReadOnlyList<string> labels, string none, PromptLanguage language)
    {
        var listed = labels.Zip(ordered, (label, c) => $"{label}. {c.Label} — {c.Description}");
        var choices = string.Join('\n', [$"{none}. {language.NoneOfThese}", .. listed]);
        return PromptText.Fill(language.Input, ("state", state)) + "\n\n" + PromptText.Fill(language.JudgeQuestion, ("choices", choices));
    }

    private static string? Choice(LabelDistribution distribution, string none, Dictionary<string, Candidate> byLabel) =>
        distribution.Trusted && distribution.Choice is string label && label != none ? byLabel[label].Id : null;

    private static Dictionary<string, double> ById(LabelDistribution distribution, Dictionary<string, Candidate> byLabel) =>
        distribution.Probs.Where(p => byLabel.ContainsKey(p.Key)).ToDictionary(p => byLabel[p.Key].Id, p => p.Value, StringComparer.Ordinal);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

using Gil.Llm;

namespace Gil.Fallback;

/// <summary>The outcome of a fallback: the output, or null with the last violation when the contract was never met.</summary>
public sealed record FallbackResult(string? Output, int Attempts, double Energy, string? FailedReason);

/// <summary>
/// Full generation under an output contract. The categories the tree already confirmed are passed as context: they
/// are settled, so the model need not infer them again. A violation is fed back into the next attempt.
/// </summary>
public sealed class FallbackGenerator(CallRecorder recorder, int maxAttempts = 3)
{
    private readonly int _maxAttempts = Math.Max(1, maxAttempts);

    public async Task<FallbackResult> GenerateAsync(
        string state,
        IOutputContract contract,
        PromptLanguage language,
        string traceId,
        IReadOnlyList<string>? pathLabels = null,
        IReadOnlyList<string>? examples = null,
        int maxTokens = 512,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(language);
        var energy = 0.0;
        string? complaint = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var request = new ChatRequest
            {
                Messages =
                [
                    new ChatMessage("system", language.FallbackSystem),
                    new ChatMessage("user", Prompt(state, contract, language, pathLabels, examples, complaint)),
                ],
                MaxTokens = maxTokens,
            };
            var call = await recorder.CompleteAsync(request, "fallback", traceId, cancellationToken: cancellationToken).ConfigureAwait(false);
            energy += call.Energy;
            var text = call.Content.Trim();
            complaint = contract.Validate(text, language);
            if (complaint is null)
            {
                return new FallbackResult(text, attempt, energy, null);
            }
        }

        return new FallbackResult(null, _maxAttempts, energy, complaint);
    }

    private static string Prompt(
        string state, IOutputContract contract, PromptLanguage language, IReadOnlyList<string>? pathLabels, IReadOnlyList<string>? examples, string? complaint)
    {
        var parts = new List<string> { PromptText.Fill(language.Input, ("state", state)), "" };
        if (pathLabels is { Count: > 0 })
        {
            parts.Add(PromptText.Fill(language.FallbackPath, ("path", string.Join(" > ", pathLabels))));
            parts.Add("");
        }

        if (examples is { Count: > 0 })
        {
            parts.Add(language.FallbackExamples);
            parts.AddRange(examples.Select(e => $"- {e}"));
            parts.Add("");
        }

        parts.Add(contract.Instruction(language));
        if (complaint is not null)
        {
            parts.Add("");
            parts.Add(PromptText.Fill(language.FallbackRetry, ("reason", complaint)));
        }

        return string.Join('\n', parts);
    }
}

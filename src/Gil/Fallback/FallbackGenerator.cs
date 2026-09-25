using Gil.Llm;

namespace Gil.Fallback;

/// <summary>The outcome of a fallback: the output, or null with the last violation when the contract was never met.</summary>
public sealed record FallbackResult(string? Output, int Attempts, double Energy, string? FailedReason);

/// <summary>The wording of a fallback prompt. The defaults are the wording the runtime's behaviour was measured with.</summary>
public sealed record FallbackPromptTemplate
{
    public string System { get; init; } = "너는 업무 담당자다. 주어진 계약을 지켜 답한다.";

    /// <summary>Placeholder: {path}.</summary>
    public string Path { get; init; } = "분류 경로: {path} (이 범위 안에서 답하라)";

    public string Examples { get; init; } = "참고 예시:";

    /// <summary>Placeholder: {reason}.</summary>
    public string Retry { get; init; } = "직전 응답이 계약을 어겼다: {reason}. 다시 답하라.";
}

/// <summary>
/// Full generation under an output contract. The categories the tree already confirmed are passed as context: they
/// are settled, so the model need not infer them again. A violation is fed back into the next attempt.
/// </summary>
public sealed class FallbackGenerator(CallRecorder recorder, int maxAttempts = 3, FallbackPromptTemplate? template = null)
{
    private readonly FallbackPromptTemplate _template = template ?? new FallbackPromptTemplate();
    private readonly int _maxAttempts = Math.Max(1, maxAttempts);

    public async Task<FallbackResult> GenerateAsync(
        string state,
        IOutputContract contract,
        string traceId,
        IReadOnlyList<string>? pathLabels = null,
        IReadOnlyList<string>? examples = null,
        int maxTokens = 512,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var energy = 0.0;
        string? complaint = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var request = new ChatRequest
            {
                Messages =
                [
                    new ChatMessage("system", _template.System),
                    new ChatMessage("user", Prompt(state, contract, pathLabels, examples, complaint)),
                ],
                MaxTokens = maxTokens,
            };
            var call = await recorder.CompleteAsync(request, "fallback", traceId, cancellationToken: cancellationToken).ConfigureAwait(false);
            energy += call.Energy;
            var text = call.Content.Trim();
            complaint = contract.Validate(text);
            if (complaint is null)
            {
                return new FallbackResult(text, attempt, energy, null);
            }
        }

        return new FallbackResult(null, _maxAttempts, energy, complaint);
    }

    private string Prompt(string state, IOutputContract contract, IReadOnlyList<string>? pathLabels, IReadOnlyList<string>? examples, string? complaint)
    {
        var parts = new List<string> { $"<입력>\n{state}\n</입력>", "" };
        if (pathLabels is { Count: > 0 })
        {
            parts.Add(_template.Path.Replace("{path}", string.Join(" > ", pathLabels), StringComparison.Ordinal));
            parts.Add("");
        }

        if (examples is { Count: > 0 })
        {
            parts.Add(_template.Examples);
            parts.AddRange(examples.Select(e => $"- {e}"));
            parts.Add("");
        }

        parts.Add(contract.Instruction());
        if (complaint is not null)
        {
            parts.Add("");
            parts.Add(_template.Retry.Replace("{reason}", complaint, StringComparison.Ordinal));
        }

        return string.Join('\n', parts);
    }
}

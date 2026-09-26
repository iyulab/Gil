using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gil.Llm;

namespace Gil.Fallback;

/// <summary>The filled template, or null with the reason when some blanks could not be filled.</summary>
public sealed record SlotFillResult(string? Output, double Energy, string? FailedReason);

/// <summary>
/// Fills a template habit's blanks. The habit owns the wording; only the blanks are generated, so output stays short
/// and on-message. Fixed slots are never asked for, and a template with nothing to fill costs no call at all.
/// </summary>
/// <param name="recorder">Sends and records each call.</param>
/// <param name="maxAttempts">Calls before giving up on missing blanks.</param>
public sealed partial class SlotFiller(CallRecorder recorder, int maxAttempts = 2)
{
    public async Task<SlotFillResult> FillAsync(string state, Habit habit, PromptLanguage language, string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(habit);
        ArgumentNullException.ThrowIfNull(language);
        var template = habit.Template ?? "";
        var values = habit.Slots.Where(s => s.Fixed is not null).ToDictionary(s => s.Name, s => s.Fixed!);
        var wanted = habit.Slots.Where(s => s.Fixed is null).ToList();
        if (wanted.Count == 0)
        {
            return new SlotFillResult(Substitute(template, values), 0, null);
        }

        var energy = 0.0;
        var missing = wanted.Select(s => s.Name).ToList();
        string? complaint = null;
        for (var attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            var request = new ChatRequest
            {
                Messages = [new ChatMessage("system", language.SlotSystem), new ChatMessage("user", Prompt(state, template, wanted, complaint, language))],
                MaxTokens = 256,
            };
            var call = await recorder.CompleteAsync(request, "slot_fill", traceId, cancellationToken: cancellationToken).ConfigureAwait(false);
            energy += call.Energy;
            var parsed = Parse(call.Content);
            if (parsed is null)
            {
                complaint = language.SlotNotJson;
                continue;
            }

            foreach (var slot in wanted.Where(s => parsed.ContainsKey(s.Name)))
            {
                values[slot.Name] = parsed[slot.Name];
            }

            missing = [.. wanted.Where(s => !values.ContainsKey(s.Name)).Select(s => s.Name)];
            if (missing.Count == 0)
            {
                return new SlotFillResult(Substitute(template, values), energy, null);
            }

            complaint = PromptText.Fill(language.SlotMissing, ("blanks", string.Join(", ", missing)));
        }

        return new SlotFillResult(null, energy, PromptText.Fill(language.SlotFailed, ("blanks", string.Join(", ", missing))));
    }

    private static string Prompt(string state, string template, IEnumerable<Slot> wanted, string? complaint, PromptLanguage language)
    {
        var listed = string.Join('\n', wanted.Select(s => $"- \"{s.Name}\": {s.Instruction}"));
        var parts = new List<string>
        {
            PromptText.Fill(language.Input, ("state", state)), "",
            PromptText.Fill(language.SlotTemplate, ("template", template)), "",
            PromptText.Fill(language.SlotBlanks, ("blanks", listed)), "",
            language.SlotOutput,
        };
        if (complaint is not null)
        {
            parts.Add("");
            parts.Add(PromptText.Fill(language.SlotRetry, ("reason", complaint)));
        }

        return string.Join('\n', parts);
    }

    private static Dictionary<string, string>? Parse(string content)
    {
        var stripped = Fence().Replace(content.Trim(), "").Trim();
        try
        {
            using var document = JsonDocument.Parse(stripped);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return document.RootElement.EnumerateObject().ToDictionary(
                p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Substitute(string template, Dictionary<string, string> values) =>
        values.Aggregate(template, (text, pair) => text.Replace("{" + pair.Key + "}", pair.Value, StringComparison.Ordinal));

    [GeneratedRegex(@"^```(?:json)?\s*|\s*```$", RegexOptions.Multiline)]
    private static partial Regex Fence();
}

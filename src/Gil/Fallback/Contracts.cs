using System.Globalization;

namespace Gil.Fallback;

/// <summary>Free text in the task's language, optionally bounded in length.</summary>
public sealed record TextContract(int? MaxChars = null) : IOutputContract
{
    public string Instruction(PromptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return MaxChars is int max
            ? PromptText.Fill(language.TextInstructionMax, ("max", Number(max)))
            : language.TextInstruction;
    }

    public string? Validate(string text, PromptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        if (string.IsNullOrWhiteSpace(text))
        {
            return language.TextEmpty;
        }

        return MaxChars is int max && text.Length > max
            ? PromptText.Fill(language.TextTooLong, ("max", Number(max)), ("length", Number(text.Length)))
            : null;
    }

    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Exactly one of a fixed set of outputs.</summary>
public sealed record ChoiceContract(IReadOnlyList<string> Choices) : IOutputContract
{
    public string Instruction(PromptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return PromptText.Fill(language.ChoiceInstruction, ("choices", string.Join(" / ", Choices)));
    }

    public string? Validate(string text, PromptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        var answer = text.Trim();
        return Choices.Contains(answer) ? null : PromptText.Fill(language.ChoiceInvalid, ("answer", answer));
    }
}

/// <summary>One integer in a closed range.</summary>
public sealed record ScoreContract(int Low, int High) : IOutputContract
{
    public string Instruction(PromptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return PromptText.Fill(language.ScoreInstruction, ("low", TextContract.Number(Low)), ("high", TextContract.Number(High)));
    }

    public string? Validate(string text, PromptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        var answer = text.Trim();
        if (!int.TryParse(answer, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return PromptText.Fill(language.ScoreNotInteger, ("answer", answer));
        }

        return value < Low || value > High
            ? PromptText.Fill(language.ScoreOutOfRange, ("low", TextContract.Number(Low)), ("high", TextContract.Number(High)), ("answer", answer))
            : null;
    }
}

/// <summary>
/// One of the answer habits in a tree, or the language's "none of these". The list always comes from the whole tree —
/// the generator must be able to answer requests that have no habit yet — and narrows to the subtree below a confirmed
/// category, where the language's <see cref="PromptLanguage.OutOfCategory"/> takes the place of "none of these".
/// </summary>
public sealed record TreeAnswerContract(Node Root) : IOutputContract, IScopableContract
{
    /// <summary>Narrowed to a confirmed category: the escape is offered instead of "none of these".</summary>
    private bool Narrowed { get; init; }

    public IReadOnlyList<string> Answers =>
        [.. Root.Walk().SelectMany(node => node.Habits).Where(h => h.Kind == HabitKind.Answer && h.Text is not null).Select(h => h.Text!)];

    public string Instruction(PromptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var listed = Root.Walk().SelectMany(node => node.Habits)
            .Where(h => h.Kind == HabitKind.Answer && h.Text is not null)
            .Select(h => string.IsNullOrEmpty(h.Description)
                ? $"- {h.Text}"
                : $"- {h.Text}" + PromptText.Fill(language.TreeExample, ("description", h.Description)));
        return PromptText.Fill(language.TreeInstruction, ("answers", string.Join('\n', listed)), ("none", None(language)));
    }

    public string? Validate(string text, PromptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        var answer = text.Trim();
        return answer == None(language) || Answers.Contains(answer) ? null : PromptText.Fill(language.TreeInvalid, ("answer", answer));
    }

    public IOutputContract? Scoped(string nodeId)
    {
        var below = Root.Find(nodeId);
        if (below is null)
        {
            return null;
        }

        var narrowed = this with { Root = below, Narrowed = true };
        return narrowed.Answers.Count == 0 ? null : narrowed;
    }

    private string None(PromptLanguage language) => Narrowed ? language.OutOfCategory : language.NoneOfThese;
}

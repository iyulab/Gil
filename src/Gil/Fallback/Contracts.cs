using System.Globalization;

namespace Gil.Fallback;

/// <summary>Free text, optionally bounded in length.</summary>
public sealed record TextContract(int? MaxChars = null, string Language = "한국어") : IOutputContract
{
    public string Instruction() =>
        MaxChars is int max ? $"자연스러운 {Language} 문장으로, {max}자 이내로 답하라." : $"자연스러운 {Language} 문장으로 답하라.";

    public string? Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "빈 응답";
        }

        return MaxChars is int max && text.Length > max ? $"{max}자를 넘었다 (현재 {text.Length}자)" : null;
    }
}

/// <summary>Exactly one of a fixed set of outputs.</summary>
public sealed record ChoiceContract(IReadOnlyList<string> Choices) : IOutputContract
{
    public string Instruction() => $"다음 중 하나를 그대로 출력하라: {string.Join(" / ", Choices)}";

    public string? Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var answer = text.Trim();
        return Choices.Contains(answer) ? null : $"허용된 선택지가 아니다 — 받은 값: '{answer}'";
    }
}

/// <summary>One integer in a closed range.</summary>
public sealed record ScoreContract(int Low, int High) : IOutputContract
{
    public string Instruction() => $"{Low}에서 {High} 사이의 정수 하나만 출력하라.";

    public string? Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var answer = text.Trim();
        if (!int.TryParse(answer, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return $"정수가 아니다 — 받은 값: '{answer}'";
        }

        return value < Low || value > High ? $"{Low}~{High} 범위를 벗어났다 — 받은 값: {answer}" : null;
    }
}

/// <summary>
/// One of the answer habits in a tree, or "none of these". The list always comes from the whole tree — the generator
/// must be able to answer requests that have no habit yet — and narrows to the subtree below a confirmed category,
/// with an escape output for when that category turns out wrong.
/// </summary>
public sealed record TreeAnswerContract(Node Root, string None = "해당 없음", string Escape = "이 범주에 없음") : IOutputContract, IScopableContract
{
    public IReadOnlyList<string> Answers =>
        [.. Root.Walk().SelectMany(node => node.Habits).Where(h => h.Kind == HabitKind.Answer && h.Text is not null).Select(h => h.Text!)];

    public string Instruction()
    {
        var listed = Root.Walk().SelectMany(node => node.Habits)
            .Where(h => h.Kind == HabitKind.Answer && h.Text is not null)
            .Select(h => $"- {h.Text} (예: {h.Description})");
        return $"위 입력이 다음 중 어디에 해당하는가?\n{string.Join('\n', listed)}\n- {None}\n이름 하나만 그대로 출력하라.";
    }

    public string? Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var answer = text.Trim();
        return answer == None || Answers.Contains(answer) ? null : $"목록에 없는 이름이다 — 받은 값: '{answer}'";
    }

    public ScopedContract? Scoped(string nodeId)
    {
        var below = Root.Find(nodeId);
        if (below is null)
        {
            return null;
        }

        var narrowed = this with { Root = below, None = Escape };
        return narrowed.Answers.Count == 0 ? null : new ScopedContract(narrowed, Escape);
    }
}

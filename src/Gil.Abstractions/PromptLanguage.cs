namespace Gil;

/// <summary>
/// Every piece of wording a model reads: judgment, fallback, slot filling, the contracts' instructions and the
/// violations fed back to the model on a retry. A task declares the language its tree is written in; the built-ins are
/// <see cref="Korean"/> and <see cref="English"/>, and any piece can be replaced with <c>with { ... }</c>.
/// </summary>
/// <remarks>
/// Placeholders are written as <c>{name}</c> and are listed on each property. Only the template's own placeholders are
/// substituted — text inserted for one (such as the input) is never scanned for another. "None of these" and the
/// tree's candidates must share a language: a label from another language than the candidates weakens the judge's
/// and the generator's rejection of out-of-scope input.
/// </remarks>
public sealed record PromptLanguage
{
    /// <summary>
    /// The wording the runtime's behaviour was measured with. It is kept byte for byte, defects included, and changes
    /// only together with a new measurement. One difference from 0.1.0, outside the measured trees (where every answer
    /// had a description): an answer without a description no longer gets an empty example in the tree contract.
    /// </summary>
    public static PromptLanguage Korean { get; } = new()
    {
        Input = "<입력>\n{state}\n</입력>",
        NoneOfThese = "해당 없음",
        OutOfCategory = "이 범주에 없음",
        JudgeSystem = "너는 분류기다. 반드시 라벨 하나만 출력한다.",
        JudgeQuestion = "위 입력이 다음 중 어디에 해당하는가?\n{choices}\n라벨 하나만 답하라.",
        FallbackSystem = "너는 업무 담당자다. 주어진 계약을 지켜 답한다.",
        FallbackPath = "분류 경로: {path} (이 범위 안에서 답하라)",
        FallbackExamples = "참고 예시:",
        FallbackRetry = "직전 응답이 계약을 어겼다: {reason}. 다시 답하라.",
        Procedure = "절차: {steps}",
        SlotSystem = "너는 빈칸을 채운다. 반드시 JSON 객체 하나만 출력한다.",
        SlotTemplate = "아래 틀의 빈칸을 채운다.\n틀: {template}",
        SlotBlanks = "채울 빈칸:\n{blanks}",
        SlotOutput = "빈칸 이름을 키로 하는 JSON 객체 하나만 출력하라.",
        SlotRetry = "직전 응답 문제: {reason}. 다시 답하라.",
        SlotNotJson = "JSON 으로 읽히지 않았다",
        SlotMissing = "빠진 빈칸: [{blanks}]",
        SlotFailed = "빈칸을 채우지 못했다 — [{blanks}]",
        TextInstruction = "자연스러운 한국어 문장으로 답하라.",
        TextInstructionMax = "자연스러운 한국어 문장으로, {max}자 이내로 답하라.",
        TextEmpty = "빈 응답",
        TextTooLong = "{max}자를 넘었다 (현재 {length}자)",
        ChoiceInstruction = "다음 중 하나를 그대로 출력하라: {choices}",
        ChoiceInvalid = "허용된 선택지가 아니다 — 받은 값: '{answer}'",
        ScoreInstruction = "{low}에서 {high} 사이의 정수 하나만 출력하라.",
        ScoreNotInteger = "정수가 아니다 — 받은 값: '{answer}'",
        ScoreOutOfRange = "{low}~{high} 범위를 벗어났다 — 받은 값: {answer}",
        TreeInstruction = "위 입력이 다음 중 어디에 해당하는가?\n{answers}\n- {none}\n이름 하나만 그대로 출력하라.",
        TreeExample = " (예: {description})",
        TreeInvalid = "목록에 없는 이름이다 — 받은 값: '{answer}'",
    };

    /// <summary>English wording. The tree instruction asks for a line of the list, since the listed items are answers, not names.</summary>
    public static PromptLanguage English { get; } = new()
    {
        Input = "<input>\n{state}\n</input>",
        NoneOfThese = "None of these",
        OutOfCategory = "Not in this category",
        JudgeSystem = "You are a classifier. Output exactly one label.",
        JudgeQuestion = "Which of these does the input above belong to?\n{choices}\nAnswer with one label only.",
        FallbackSystem = "You handle requests. Answer within the given contract.",
        FallbackPath = "Category: {path} (answer within this category)",
        FallbackExamples = "Examples:",
        FallbackRetry = "The previous response broke the contract: {reason}. Answer again.",
        Procedure = "Procedure: {steps}",
        SlotSystem = "You fill in blanks. Output exactly one JSON object.",
        SlotTemplate = "Fill in the blanks of the template below.\nTemplate: {template}",
        SlotBlanks = "Blanks to fill:\n{blanks}",
        SlotOutput = "Output one JSON object whose keys are the blank names.",
        SlotRetry = "Problem with the previous response: {reason}. Answer again.",
        SlotNotJson = "not readable as JSON",
        SlotMissing = "missing blanks: [{blanks}]",
        SlotFailed = "could not fill the blanks: [{blanks}]",
        TextInstruction = "Answer in natural English.",
        TextInstructionMax = "Answer in natural English, in at most {max} characters.",
        TextEmpty = "empty response",
        TextTooLong = "longer than {max} characters ({length})",
        ChoiceInstruction = "Output exactly one of these, as written: {choices}",
        ChoiceInvalid = "not one of the allowed choices: '{answer}'",
        ScoreInstruction = "Output a single integer from {low} to {high}.",
        ScoreNotInteger = "not an integer: '{answer}'",
        ScoreOutOfRange = "outside {low} to {high}: {answer}",
        TreeInstruction = "Which of these fits the input above?\n{answers}\n- {none}\nOutput one line of the list exactly as written, without the dash or the example.",
        TreeExample = " (e.g. {description})",
        TreeInvalid = "not a line of the list: '{answer}'",
    };

    /// <summary>How the input is shown to the model. Placeholder: {state}.</summary>
    public required string Input { get; init; }

    /// <summary>The judgment's "none of these" option, and the whole tree contract's answer for input outside the task.</summary>
    public required string NoneOfThese { get; init; }

    /// <summary>
    /// A narrowed contract's escape: the answer is outside the confirmed category, so the judgment that confirmed it was
    /// wrong and the request is solved again under the full contract.
    /// </summary>
    public required string OutOfCategory { get; init; }

    /// <summary>The judge's system message.</summary>
    public required string JudgeSystem { get; init; }

    /// <summary>Follows the input. Placeholder: {choices} (the labelled candidates, "none of these" first).</summary>
    public required string JudgeQuestion { get; init; }

    /// <summary>The fallback generator's system message.</summary>
    public required string FallbackSystem { get; init; }

    /// <summary>The categories the tree confirmed. Placeholder: {path}.</summary>
    public required string FallbackPath { get; init; }

    /// <summary>Heads the list of examples.</summary>
    public required string FallbackExamples { get; init; }

    /// <summary>Placeholder: {reason} (the contract's violation).</summary>
    public required string FallbackRetry { get; init; }

    /// <summary>A procedure habit's steps, appended to the input it is generated from. Placeholder: {steps}.</summary>
    public required string Procedure { get; init; }

    /// <summary>The slot filler's system message.</summary>
    public required string SlotSystem { get; init; }

    /// <summary>Placeholder: {template}.</summary>
    public required string SlotTemplate { get; init; }

    /// <summary>Placeholder: {blanks} (one line per blank).</summary>
    public required string SlotBlanks { get; init; }

    /// <summary>Asks for the filled blanks as one JSON object.</summary>
    public required string SlotOutput { get; init; }

    /// <summary>Placeholder: {reason}.</summary>
    public required string SlotRetry { get; init; }

    /// <summary>The retry reason when the response is not a JSON object.</summary>
    public required string SlotNotJson { get; init; }

    /// <summary>The retry reason when blanks are missing. Placeholder: {blanks} (their names).</summary>
    public required string SlotMissing { get; init; }

    /// <summary>The reason recorded when blanks stayed empty; not sent to the model. Placeholder: {blanks}.</summary>
    public required string SlotFailed { get; init; }

    /// <summary>A free-text contract's instruction.</summary>
    public required string TextInstruction { get; init; }

    /// <summary>Placeholder: {max}.</summary>
    public required string TextInstructionMax { get; init; }

    /// <summary>The violation when a free-text answer is empty.</summary>
    public required string TextEmpty { get; init; }

    /// <summary>Placeholders: {max}, {length}.</summary>
    public required string TextTooLong { get; init; }

    /// <summary>Placeholder: {choices}.</summary>
    public required string ChoiceInstruction { get; init; }

    /// <summary>Placeholder: {answer}.</summary>
    public required string ChoiceInvalid { get; init; }

    /// <summary>Placeholders: {low}, {high}.</summary>
    public required string ScoreInstruction { get; init; }

    /// <summary>Placeholder: {answer}.</summary>
    public required string ScoreNotInteger { get; init; }

    /// <summary>Placeholders: {low}, {high}, {answer}.</summary>
    public required string ScoreOutOfRange { get; init; }

    /// <summary>
    /// Placeholders: {answers} (one line per answer), {none} (<see cref="NoneOfThese"/>, or <see cref="OutOfCategory"/>
    /// when narrowed).
    /// </summary>
    public required string TreeInstruction { get; init; }

    /// <summary>Appended to a listed answer whose habit has a description. Placeholder: {description}.</summary>
    public required string TreeExample { get; init; }

    /// <summary>Placeholder: {answer}.</summary>
    public required string TreeInvalid { get; init; }
}

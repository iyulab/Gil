using AwesomeAssertions;
using Gil.Fallback;
using Gil.Judge;
using Gil.Llm;
using Gil.Ontology;
using Gil.Traverse;

namespace Gil.Tests;

public sealed class PromptLanguageTests
{
    private static readonly Node Tree = OntologyYaml.Parse("""
        id: root
        children:
          - id: work
            label: Work
            description: office matters
            options:
              - {id: pto, kind: answer, label: pto, description: book a day off, text: pto_request}
              - id: received
                kind: template
                label: received
                description: acknowledge
                template: "{item} received by {team}."
                slots:
                  - {name: item, instruction: what was sent}
                  - {name: team, instruction: fixed, fixed: HR}
              - {id: steps, kind: procedure, label: steps, description: a procedure, steps: Read page two.}
          - id: travel
            label: Travel
            description: trips
            options:
              - {id: book, kind: answer, label: book, description: book a flight, text: book_flight}
        """).Root;

    [Fact]
    public async Task An_english_task_sends_no_korean_on_any_path()
    {
        // Request 1: a template whose blanks are never filled (unreadable, then incomplete), a narrowed fallback that
        // is violated and then escaped, and the full fallback. Request 2: a procedure whose first answer is empty.
        var rig = new Rig(
            PromptLanguage.English,
            ["B", "C", "B", "D"],
            "not json", """{"other": "x"}""", "", "Not in this category", "book_flight", "", "Done.");

        var template = await rig.Resolver.ResolveAsync(rig.Task, "I sent the form", "t1", TestContext.Current.CancellationToken);
        var procedure = await rig.Resolver.ResolveAsync(rig.Task, "How do I start?", "t2", TestContext.Current.CancellationToken);

        (template.Output, template.Mode).Should().Be(("book_flight", "partial"));
        (procedure.Output, procedure.Mode).Should().Be(("Done.", "habit/procedure"));
        var sent = rig.Model.Requests.SelectMany(r => r.Messages).Select(m => m.Content).ToList();
        sent.Should().AllSatisfy(content => HasHangul(content).Should().BeFalse(content));
        var joined = string.Join("\n", sent);
        joined.Should().Contain("A. None of these")
            .And.Contain("Problem with the previous response: not readable as JSON. Answer again.")
            .And.Contain("- Not in this category\nOutput one line of the list")
            .And.Contain("The previous response broke the contract: not a line of the list: ''. Answer again.")
            .And.Contain("\n\nProcedure: Read page two.")
            .And.Contain("The previous response broke the contract: empty response. Answer again.");
    }

    [Fact]
    public void English_contract_wording_has_no_korean()
    {
        var english = PromptLanguage.English;
        IOutputContract[] contracts = [new TextContract(), new TextContract(MaxChars: 5), new ChoiceContract(["yes", "no"]), new ScoreContract(1, 5), new TreeAnswerContract(Tree), new TreeAnswerContract(Tree).Scoped("work")!];
        var wording = contracts.SelectMany(c => new[] { c.Instruction(english), c.Validate("toolong", english), c.Validate(" ", english) })
            .Append(new ScoreContract(1, 5).Validate("9", english))
            .OfType<string>()
            .ToList();

        wording.Should().NotBeEmpty();
        wording.Should().AllSatisfy(text => HasHangul(text).Should().BeFalse(text));
        typeof(PromptLanguage).GetProperties().Where(p => p.PropertyType == typeof(string))
            .Select(p => (string)p.GetValue(english)!)
            .Should().AllSatisfy(text => HasHangul(text).Should().BeFalse(text));
    }

    [Fact]
    public void An_answer_without_a_description_is_listed_without_an_example()
    {
        var root = new Node
        {
            Id = "root",
            Label = "root",
            Description = "",
            Habits =
            [
                new Habit { Id = "a", Kind = HabitKind.Answer, Label = "a", Description = "", Text = "reset_password" },
                new Habit { Id = "b", Kind = HabitKind.Answer, Label = "b", Description = "card stolen", Text = "block_card" },
            ],
        };
        var contract = new TreeAnswerContract(root);

        contract.Instruction(PromptLanguage.English).Should().Be(
            "Which of these fits the input above?\n- reset_password\n- block_card (e.g. card stolen)\n- None of these\n"
            + "Output one line of the list exactly as written, without the dash or the example.");
        contract.Instruction(PromptLanguage.Korean).Should().Contain("\n- reset_password\n- block_card (예: card stolen)\n");
    }

    [Fact]
    public async Task The_korean_wording_is_the_measured_wording()
    {
        var korean = PromptLanguage.Korean;
        new TextContract().Instruction(korean).Should().Be("자연스러운 한국어 문장으로 답하라.");
        new TextContract(MaxChars: 80).Instruction(korean).Should().Be("자연스러운 한국어 문장으로, 80자 이내로 답하라.");
        new TextContract(MaxChars: 5).Validate("toolong", korean).Should().Be("5자를 넘었다 (현재 7자)");
        new ChoiceContract(["yes", "no"]).Instruction(korean).Should().Be("다음 중 하나를 그대로 출력하라: yes / no");
        new ScoreContract(1, 5).Validate("9", korean).Should().Be("1~5 범위를 벗어났다 — 받은 값: 9");
        new TreeAnswerContract(Tree).Instruction(korean).Should().Be(
            "위 입력이 다음 중 어디에 해당하는가?\n- pto_request (예: book a day off)\n- book_flight (예: book a flight)\n- 해당 없음\n이름 하나만 그대로 출력하라.");
        new TreeAnswerContract(Tree).Validate("x", korean).Should().Be("목록에 없는 이름이다 — 받은 값: 'x'");

        var rig = new Rig(korean, ["B", "C", "B", "D"], "not json", """{"other": "x"}""", "pto_request", "", "Done.");
        await rig.Resolver.ResolveAsync(rig.Task, "I sent the form", "t1", TestContext.Current.CancellationToken);
        await rig.Resolver.ResolveAsync(rig.Task, "How do I start?", "t2", TestContext.Current.CancellationToken);

        var judge = rig.Model.Requests[0].Messages;
        judge[0].Content.Should().Be("너는 분류기다. 반드시 라벨 하나만 출력한다.");
        judge[1].Content.Should().Be("<입력>\nI sent the form\n</입력>\n\n위 입력이 다음 중 어디에 해당하는가?\nA. 해당 없음\nB. Work — office matters\nC. Travel — trips\n라벨 하나만 답하라.");
        var slots = rig.Model.Requests[3].Messages;
        slots[0].Content.Should().Be("너는 빈칸을 채운다. 반드시 JSON 객체 하나만 출력한다.");
        slots[1].Content.Should().Be(
            "<입력>\nI sent the form\n</입력>\n\n아래 틀의 빈칸을 채운다.\n틀: {item} received by {team}.\n\n채울 빈칸:\n- \"item\": what was sent\n\n"
            + "빈칸 이름을 키로 하는 JSON 객체 하나만 출력하라.\n\n직전 응답 문제: JSON 으로 읽히지 않았다. 다시 답하라.");
        rig.Model.Requests[4].Messages[0].Content.Should().Be("너는 업무 담당자다. 주어진 계약을 지켜 답한다.");
        rig.Model.Requests[4].Messages[1].Content.Should().StartWith("<입력>\nI sent the form\n</입력>\n\n분류 경로: Work (이 범위 안에서 답하라)\n\n")
            .And.EndWith("\n- 이 범주에 없음\n이름 하나만 그대로 출력하라.");
        rig.Model.Requests[^1].Messages[1].Content.Should().Be(
            "<입력>\nHow do I start?\n\n절차: Read page two.\n</입력>\n\n자연스러운 한국어 문장으로 답하라.\n\n직전 응답이 계약을 어겼다: 빈 응답. 다시 답하라.");
    }

    [Fact]
    public async Task Text_inserted_for_a_placeholder_is_not_read_as_another()
    {
        var model = new RoutedModel([], "ok");
        var generator = new FallbackGenerator(new CallRecorder(model, new EnergyModel(0, 0, 0, 0)));

        await generator.GenerateAsync("say {reason} and {path}", new TextContract(), PromptLanguage.English, "t", ["Work"], cancellationToken: TestContext.Current.CancellationToken);

        model.Requests[0].Messages[1].Content.Should().StartWith("<input>\nsay {reason} and {path}\n</input>\n\nCategory: Work");
    }

    private static bool HasHangul(string text) => text.Any(c => c is >= '가' and <= '힣');

    private sealed class Rig
    {
        public Rig(PromptLanguage language, string[] labels, params string[] answers)
        {
            Model = new RoutedModel(labels, answers);
            var recorder = new CallRecorder(Model, new EnergyModel(1, 0, 0, 0));
            Resolver = new Resolver(new GreedyTraverser(new SingleTokenJudge(recorder)), new FallbackGenerator(recorder), new SlotFiller(recorder));
            Task = new TaskDefinition("support", new TreeAnswerContract(Tree), Tree, new TaskPolicy { Thresholds = new([0.5], 0.5), FallbackScope = FallbackScope.Path }, language);
        }

        public RoutedModel Model { get; }

        public Resolver Resolver { get; }

        public TaskDefinition Task { get; }
    }

    /// <summary>Answers one-token judgments with the next label, certainly, and every other call with the next answer.</summary>
    private sealed class RoutedModel(string[] labels, params string[] answers) : IChatModel
    {
        private int _label;
        private int _answer;

        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var content = request.MaxTokens == 1 ? labels[_label++] : answers[_answer++];
            return System.Threading.Tasks.Task.FromResult(new ChatResult
            {
                Model = "m",
                Content = content,
                PromptTokens = 1,
                CachedTokens = 0,
                CompletionTokens = 1,
                LatencyMs = 1,
                FirstToken = content,
                TopLogprobs = [new TokenLogprob(content, 0)],
                RawResponse = "{}",
            });
        }
    }
}

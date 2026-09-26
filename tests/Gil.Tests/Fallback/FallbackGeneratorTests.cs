using System.Text.Json;
using AwesomeAssertions;
using Gil.Fallback;
using Gil.Judge;
using Gil.Llm;
using Gil.Ontology;

namespace Gil.Tests.Fallback;

public sealed class FallbackGeneratorTests
{
    private const string Tree = """
        id: root
        children:
          - id: work
            label: Work
            description: office matters
            options:
              - {id: pto, kind: answer, label: pto, description: book a day off, text: pto_request}
              - {id: payroll, kind: answer, label: payroll, description: salary questions, text: payroll}
          - id: travel
            label: Travel
            description: trips
            options:
              - {id: book, kind: answer, label: book, description: book a flight, text: book_flight}
        """;

    [Fact]
    public void Contracts_validate_their_shapes()
    {
        new TextContract(MaxChars: 5).Validate("toolong", PromptLanguage.Korean).Should().Contain("5자를 넘었다");
        new TextContract().Validate("  ", PromptLanguage.Korean).Should().Be("빈 응답");
        new ChoiceContract(["yes", "no"]).Validate(" yes ", PromptLanguage.Korean).Should().BeNull();
        new ChoiceContract(["yes", "no"]).Validate("maybe", PromptLanguage.Korean).Should().Contain("maybe");
        new ScoreContract(1, 5).Validate("3", PromptLanguage.Korean).Should().BeNull();
        new ScoreContract(1, 5).Validate("9", PromptLanguage.Korean).Should().Contain("범위를 벗어났다");
        new ScoreContract(1, 5).Validate("x", PromptLanguage.Korean).Should().Contain("정수가 아니다");
    }

    [Fact]
    public void The_tree_answer_contract_narrows_to_a_confirmed_category_with_an_escape()
    {
        var contract = new TreeAnswerContract(OntologyYaml.Parse(Tree).Root);

        contract.Validate("book_flight", PromptLanguage.Korean).Should().BeNull();
        contract.Validate("해당 없음", PromptLanguage.Korean).Should().BeNull();
        var scoped = contract.Scoped("work")!;
        scoped.Validate("pto_request", PromptLanguage.Korean).Should().BeNull();
        scoped.Validate("book_flight", PromptLanguage.Korean).Should().NotBeNull();
        scoped.Validate("이 범주에 없음", PromptLanguage.Korean).Should().BeNull();
        scoped.Validate("해당 없음", PromptLanguage.Korean).Should().NotBeNull();
        scoped.Validate("Not in this category", PromptLanguage.English).Should().BeNull();
        scoped.Instruction(PromptLanguage.Korean).Should().EndWith("\n- 이 범주에 없음\n이름 하나만 그대로 출력하라.");
        contract.Scoped("missing").Should().BeNull();
    }

    [Fact]
    public async Task A_violation_is_fed_back_and_the_second_attempt_can_pass()
    {
        var (generator, model) = Generator("", "a proper answer");

        var result = await generator.GenerateAsync("hello", new TextContract(), PromptLanguage.Korean, "t", ["Work", "PTO"], cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be(new FallbackResult("a proper answer", 2, 2.0, null));
        model.Requests[0].Messages[^1].Content.Should().Contain("분류 경로: Work > PTO (이 범위 안에서 답하라)");
        model.Requests[1].Messages[^1].Content.Should().EndWith("직전 응답이 계약을 어겼다: 빈 응답. 다시 답하라.");
    }

    [Fact]
    public async Task A_contract_never_met_returns_no_output_with_the_last_reason()
    {
        var (generator, _) = Generator("maybe", "perhaps", "unsure");

        var result = await generator.GenerateAsync("hello", new ChoiceContract(["yes", "no"]), PromptLanguage.Korean, "t", cancellationToken: TestContext.Current.CancellationToken);

        result.Output.Should().BeNull();
        result.Attempts.Should().Be(3);
        result.FailedReason.Should().Contain("unsure");
    }

    [Fact]
    public async Task Prompts_match_the_wording_another_implementation_was_measured_with()
    {
        // Prompts another implementation rendered for fixed inputs: the committed synthetic fixture, or GIL_COMPAT_PROMPTS.
        var path = Conformance.Fixture("GIL_COMPAT_PROMPTS", "prompts.json");
        if (path is null)
        {
            Assert.Skip("GIL_COMPAT_PROMPTS is not set and the committed fixture is missing");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var fixture = document.RootElement;
        var root = OntologyYaml.Parse(fixture.GetProperty("tree").GetString()!).Root;
        var ct = TestContext.Current.CancellationToken;

        foreach (var expected in fixture.GetProperty("judges").EnumerateArray())
        {
            var node = root.Find(expected.GetProperty("node").GetString()!)!;
            var candidates = node.Children.Count > 0
                ? node.Children.Select(c => new Candidate(c.Id, c.Label, c.Description)).ToList()
                : node.Habits.Select(h => new Candidate(h.Id, h.Label, h.Description)).ToList();
            var model = new ScriptedModel("A");
            var judge = new SingleTokenJudge(new CallRecorder(model, new EnergyModel(0, 1, 0, 1)), new SingleTokenJudgeOptions { Scheme = expected.GetProperty("scheme").GetString()! });
            await judge.JudgeAsync(expected.GetProperty("state").GetString()!, candidates, PromptLanguage.Korean, "t", node.Id, cancellationToken: ct);
            model.Requests[0].Messages[0].Content.Should().Be(expected.GetProperty("system").GetString());
            model.Requests[0].Messages[1].Content.Should().Be(expected.GetProperty("user").GetString(), node.Id);
        }

        foreach (var expected in fixture.GetProperty("fallbacks").EnumerateArray())
        {
            var name = expected.GetProperty("contract").GetString()!;
            IOutputContract contract = name switch
            {
                "text" => new TextContract(),
                "text-max" => new TextContract(MaxChars: 80),
                "choice" => new ChoiceContract(["yes", "no"]),
                "score" => new ScoreContract(1, 5),
                "tree" => new TreeAnswerContract(root),
                "tree-scoped" => new TreeAnswerContract(root).Scoped(expected.GetProperty("scoped_node").GetString()!)!,
                _ => throw new InvalidOperationException(name),
            };
            var complaint = expected.GetProperty("complaint").GetString();
            // Reproduce the recorded violation with a first answer that causes it; the second prompt carries it.
            var first = complaint switch { "빈 응답" => "", not null => "x", _ => "ok" };
            var (generator, model) = Generator(first, first, "3");
            await generator.GenerateAsync(
                expected.GetProperty("state").GetString()!,
                contract,
                PromptLanguage.Korean,
                "t",
                [.. expected.GetProperty("path").EnumerateArray().Select(p => p.GetString()!)],
                [.. expected.GetProperty("examples").EnumerateArray().Select(p => p.GetString()!)],
                cancellationToken: ct);
            var rendered = model.Requests[complaint is null ? 0 : 1].Messages;
            rendered[0].Content.Should().Be(expected.GetProperty("system").GetString());
            rendered[1].Content.Should().Be(expected.GetProperty("user").GetString(), name);
        }
    }

    private static (FallbackGenerator Generator, ScriptedModel Model) Generator(params string[] answers)
    {
        var model = new ScriptedModel(answers);
        return (new FallbackGenerator(new CallRecorder(model, new EnergyModel(1, 0, 0, 0))), model);
    }

    private sealed class ScriptedModel(params string[] answers) : IChatModel
    {
        private int _next;

        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var answer = answers[Math.Min(_next++, answers.Length - 1)];
            return Task.FromResult(new ChatResult
            {
                Model = "m",
                Content = answer,
                PromptTokens = 10,
                CachedTokens = 0,
                CompletionTokens = 1,
                LatencyMs = 1,
                FirstToken = answer,
                TopLogprobs = [new TokenLogprob(answer.Length > 0 ? answer : "A", 0)],
                RawResponse = "{}",
            });
        }
    }
}

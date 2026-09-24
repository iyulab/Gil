using AwesomeAssertions;
using Gil.Fallback;
using Gil.Llm;
using Gil.Ontology;
using Gil.Traverse;

namespace Gil.Tests;

public sealed class ResolverTests
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

    private static readonly Thresholds Strict = new([0.5], Leaf: 0.9);

    [Fact]
    public async Task A_confident_tree_answers_from_the_habit_without_generation()
    {
        var rig = new Rig(Judgments(("work", 0.95), ("pto", 0.95)));

        var result = await rig.Resolver.ResolveAsync(Task(), "I need Friday off", "t", TestContext.Current.CancellationToken);

        (result.Output, result.Mode).Should().Be(("pto_request", "habit/answer"));
        rig.Model.Requests.Should().BeEmpty();
        rig.Sink.Traces["t"].Outcome!.Path.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_template_habit_fills_only_the_open_slots()
    {
        var rig = new Rig(Judgments(("work", 0.95), ("received", 0.95)), """{"item": "the form"}""");

        var result = await rig.Resolver.ResolveAsync(Task(), "sent the form", "t", TestContext.Current.CancellationToken);

        (result.Output, result.Mode).Should().Be(("the form received by HR.", "habit/template"));
        rig.Model.Requests.Single().Messages[^1].Content.Should().Contain("\"item\"").And.NotContain("\"team\"");
    }

    [Fact]
    public async Task A_procedure_habit_is_generated_with_its_steps()
    {
        var rig = new Rig(Judgments(("work", 0.95), ("steps", 0.95)), "done");

        var result = await rig.Resolver.ResolveAsync(Task(), "extract it", "t", TestContext.Current.CancellationToken);

        result.Mode.Should().Be("habit/procedure");
        rig.Model.Requests.Single().Messages[^1].Content.Should().Contain("절차: Read page two.");
    }

    [Fact]
    public async Task A_confirmed_category_narrows_the_fallback()
    {
        var rig = new Rig(Judgments(("work", 0.95), ("pto", 0.6)), "pto_request");

        var result = await rig.Resolver.ResolveAsync(Task(FallbackScope.Path), "day off?", "t", TestContext.Current.CancellationToken);

        (result.Output, result.Mode).Should().Be(("pto_request", "partial"));
        var prompt = rig.Model.Requests.Single().Messages[^1].Content;
        prompt.Should().Contain("분류 경로: Work").And.Contain("- pto_request").And.NotContain("book_flight").And.Contain("이 범주에 없음");
    }

    [Fact]
    public async Task Escaping_the_narrowed_contract_solves_again_in_full()
    {
        var rig = new Rig(Judgments(("work", 0.95), ("pto", 0.6)), "이 범주에 없음", "book_flight");

        var result = await rig.Resolver.ResolveAsync(Task(FallbackScope.Path), "a flight please", "t", TestContext.Current.CancellationToken);

        (result.Output, result.Mode).Should().Be(("book_flight", "partial"));
        rig.Model.Requests.Should().HaveCount(2);
        rig.Model.Requests[1].Messages[^1].Content.Should().Contain("book_flight").And.NotContain("분류 경로");
    }

    [Fact]
    public async Task With_nothing_confirmed_the_full_fallback_answers()
    {
        var rig = new Rig(Judgments((null, 0.9)), "book_flight");

        var result = await rig.Resolver.ResolveAsync(Task(FallbackScope.Path), "hmm", "t", TestContext.Current.CancellationToken);

        (result.Output, result.Mode).Should().Be(("book_flight", "fallback"));
    }

    [Fact]
    public async Task Without_fallback_the_request_is_handed_over()
    {
        var rig = new Rig(Judgments((null, 0.9)));

        var result = await rig.Resolver.ResolveAsync(Task(allowFallback: false), "hmm", "t", TestContext.Current.CancellationToken);

        (result.Output, result.Mode).Should().Be((null, "abstain"));
        rig.Model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_remembered_answer_is_returned_before_the_tree_and_its_confidence_is_not_a_probability()
    {
        var rig = new Rig(Judgments());
        rig.Memory.Items.Add(("old", "book_flight", 0.93));

        var result = await rig.Resolver.ResolveAsync(Task(memoryThreshold: 0.9), "fly me", "t", TestContext.Current.CancellationToken);

        (result.Output, result.Mode, result.Confidence).Should().Be(("book_flight", "memory", null));
        result.Recall.Should().Be(new Recall("old", 0.93, 0.9, Hit: true));
        result.Path.Should().BeEmpty();
    }

    [Fact]
    public async Task A_memory_miss_still_records_the_nearest_neighbour()
    {
        var rig = new Rig(Judgments(("travel", 0.95), ("book", 0.95)));
        rig.Memory.Items.Add(("old", "pto_request", 0.4));

        var result = await rig.Resolver.ResolveAsync(Task(memoryThreshold: 0.9), "fly me", "t", TestContext.Current.CancellationToken);

        result.Mode.Should().Be("habit/answer");
        result.Recall.Should().Be(new Recall("old", 0.4, 0.9, Hit: false));
    }

    [Fact]
    public async Task Feedback_remembers_confirmed_answers_and_forgets_a_wrong_memory()
    {
        var rig = new Rig(Judgments(("travel", 0.95), ("book", 0.95)));
        rig.Memory.Items.Add(("old", "pto_request", 0.95));
        var task = Task(memoryThreshold: 0.9);

        await rig.Resolver.ResolveAsync(task, "fly me", "t1", TestContext.Current.CancellationToken); // memory hit: pto_request
        await rig.Resolver.FeedbackAsync(task, "t1", correct: false, correction: "book_flight", TestContext.Current.CancellationToken);

        rig.Memory.Forgotten.Should().Equal("old");
        rig.Memory.Remembered.Should().Equal(("t1", "fly me", "book_flight"));
        rig.Sink.Traces["t1"].Verdict.Should().Be("wrong");
    }

    [Fact]
    public async Task Without_a_threshold_memory_is_neither_consulted_nor_updated()
    {
        var rig = new Rig(Judgments(("travel", 0.95), ("book", 0.95)));
        rig.Memory.Items.Add(("old", "pto_request", 0.99));

        var result = await rig.Resolver.ResolveAsync(Task(), "fly me", "t", TestContext.Current.CancellationToken);
        await rig.Resolver.FeedbackAsync(Task(), "t", correct: true, cancellationToken: TestContext.Current.CancellationToken);

        (result.Mode, result.Recall).Should().Be(("habit/answer", null));
        rig.Memory.Remembered.Should().BeEmpty();
    }

    private static TaskDefinition Task(FallbackScope scope = FallbackScope.Full, bool allowFallback = true, double? memoryThreshold = null) =>
        new("support", new TreeAnswerContract(Tree), Tree, new TaskPolicy { FallbackScope = scope, AllowFallback = allowFallback, MemoryThreshold = memoryThreshold });

    private static (string? Choice, double Confidence)[] Judgments(params (string? Choice, double Confidence)[] script) => script;

    private sealed class Rig
    {
        public Rig((string? Choice, double Confidence)[] judgments, params string[] answers)
        {
            Model = new ScriptedModel(answers);
            var recorder = new CallRecorder(Model, new EnergyModel(1, 0, 0, 0), Sink);
            Resolver = new Resolver(
                new GreedyTraverser(new ScriptedJudge(judgments), Strict),
                new FallbackGenerator(recorder, maxAttempts: 1),
                new SlotFiller(recorder),
                Sink,
                Memory);
        }

        public ListSink Sink { get; } = new();

        public FakeMemory Memory { get; } = new();

        public ScriptedModel Model { get; }

        public Resolver Resolver { get; }
    }

    private sealed class ScriptedJudge((string? Choice, double Confidence)[] script) : IJudge
    {
        private int _next;

        public Task<Judgment> JudgeAsync(string state, IReadOnlyList<Candidate> candidates, string traceId, string? nodeId = null, int? layer = null, CancellationToken cancellationToken = default)
        {
            var (choice, confidence) = script[_next++];
            return System.Threading.Tasks.Task.FromResult(new Judgment
            {
                Probs = choice is null ? new Dictionary<string, double>() : new Dictionary<string, double> { [choice] = confidence },
                Choice = choice,
                Confidence = confidence,
                NoneProb = choice is null ? confidence : 1 - confidence,
                LabelMass = 0.99,
                Trusted = true,
                Call = new CallRecord { CallId = "c", TraceId = traceId, CreatedAt = DateTimeOffset.UnixEpoch, Role = "judge", Model = "m", PromptTokens = 0, CachedTokens = 0, CompletionTokens = 0, LatencyMs = 0, Energy = 2 },
            });
        }
    }

    private sealed class ScriptedModel(params string[] answers) : IChatModel
    {
        private int _next;

        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var answer = answers[_next++];
            return System.Threading.Tasks.Task.FromResult(new ChatResult { Model = "m", Content = answer, PromptTokens = 1, CachedTokens = 0, CompletionTokens = 1, LatencyMs = 1, RawResponse = "{}" });
        }
    }

    private sealed class FakeMemory : IMemory
    {
        public List<(string Source, string Answer, double Similarity)> Items { get; } = [];

        public List<string> Forgotten { get; } = [];

        public List<(string TraceId, string State, string Answer)> Remembered { get; } = [];

        public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default)
        {
            var best = Items.OrderByDescending(i => i.Similarity).Select(i => new MemoryMatch(i.Source, i.Similarity, i.Answer)).FirstOrDefault();
            return System.Threading.Tasks.Task.FromResult((best, 0.5));
        }

        public Task<double> RememberAsync(string task, string traceId, string state, string answer, CancellationToken cancellationToken = default)
        {
            Remembered.Add((traceId, state, answer));
            return System.Threading.Tasks.Task.FromResult(0.0);
        }

        public void Forget(string task, string traceId) => Forgotten.Add(traceId);
    }
}

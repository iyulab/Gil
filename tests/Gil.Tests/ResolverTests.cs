using AwesomeAssertions;
using Gil.Fallback;
using Gil.Habits;
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

    [Fact]
    public async Task Thresholds_belong_to_the_task_so_one_resolver_serves_tasks_that_accept_differently()
    {
        var rig = new Rig(Judgments(("work", 0.7), ("pto", 0.95), ("work", 0.7)), "pto_request");
        var lenient = Task() with { Policy = Task().Policy with { Thresholds = new([0.5], Leaf: 0.9) } };
        var strict = Task() with { Policy = Task().Policy with { Thresholds = new([0.9], Leaf: 0.9) } };

        var first = await rig.Resolver.ResolveAsync(lenient, "I need Friday off", "t1", TestContext.Current.CancellationToken);
        var second = await rig.Resolver.ResolveAsync(strict, "I need Friday off", "t2", TestContext.Current.CancellationToken);

        (first.Mode, second.Mode).Should().Be(("habit/answer", "fallback"));
        second.Output.Should().Be("pto_request");
    }

    [Fact]
    public async Task A_wrong_habit_blames_only_the_first_wrong_judgment_and_visits_are_counted_per_task()
    {
        var rig = new Rig(Judgments(("work", 0.95), ("pto", 0.95)));

        await rig.Resolver.ResolveAsync(Task(), "I need Friday off", "t", TestContext.Current.CancellationToken);
        await rig.Resolver.FeedbackAsync(Task(), "t", correct: false, correction: "book_flight", TestContext.Current.CancellationToken);

        rig.Statistics.Paths.Should().ContainSingle().Which.Scope.Should().Be("support");
        rig.Statistics.Outcomes.Should().Equal(("support", "work", new HabitCounts(Penalized: 1)));
    }

    [Fact]
    public async Task A_correct_fallback_that_an_existing_habit_already_gives_marks_the_habit_missed()
    {
        var rig = new Rig(Judgments(("work", 0.95), ("pto", 0.6)), "pto_request");

        await rig.Resolver.ResolveAsync(Task(FallbackScope.Path), "day off?", "t", TestContext.Current.CancellationToken);
        await rig.Resolver.FeedbackAsync(Task(FallbackScope.Path), "t", correct: true, cancellationToken: TestContext.Current.CancellationToken);

        rig.Statistics.Outcomes.Should().Equal(("support", "pto", new HabitCounts(Missed: 1)));
    }

    [Fact]
    public async Task A_memory_hit_leaves_the_habit_statistics_untouched()
    {
        var rig = new Rig(Judgments());
        rig.Memory.Items.Add(("old", "book_flight", 0.93));
        var task = Task(memoryThreshold: 0.9);

        await rig.Resolver.ResolveAsync(task, "fly me", "t", TestContext.Current.CancellationToken);
        await rig.Resolver.FeedbackAsync(task, "t", correct: true, cancellationToken: TestContext.Current.CancellationToken);

        rig.Statistics.Paths.Should().BeEmpty();
        rig.Statistics.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_explored_habit_is_cross_checked_by_the_full_fallback_without_the_path()
    {
        var rig = new Rig(Judgments(("work", 0.95), ("pto", 0.95)), new FixedRandom(0.1), "pto_request");
        var task = Task(explorationRate: 0.2);

        var result = await rig.Resolver.ResolveAsync(task, "I need Friday off", "t", TestContext.Current.CancellationToken);

        (result.Output, result.Mode).Should().Be(("pto_request", "habit/answer"));  // the habit still answers
        rig.Model.Requests.Single().Messages[^1].Content.Should().NotContain("Work");  // no path context
        rig.Sink.Traces["t"].Outcome!.ExploredOutput.Should().Be("pto_request");
        rig.Statistics.Outcomes.Should().Equal(("support", "pto", new HabitCounts(Explored: 1)));
    }

    [Fact]
    public async Task A_disagreeing_cross_check_is_recorded_as_disputed_not_penalized()
    {
        var rig = new Rig(Judgments(("work", 0.95), ("pto", 0.95)), new FixedRandom(0.1), "book_flight");

        await rig.Resolver.ResolveAsync(Task(explorationRate: 0.2), "I need Friday off", "t", TestContext.Current.CancellationToken);

        rig.Sink.Traces["t"].Outcome!.ExploredOutput.Should().Be("book_flight");
        rig.Statistics.Outcomes.Should().Equal(("support", "pto", new HabitCounts(Explored: 1, Disputed: 1)));
    }

    [Theory]
    [InlineData(0.0, 0.0)]  // exploration off: no draw decides anything
    [InlineData(0.2, 0.5)]  // the draw falls outside the rate
    public async Task Unexplored_habits_make_no_extra_call(double rate, double draw)
    {
        var rig = new Rig(Judgments(("work", 0.95), ("pto", 0.95)), new FixedRandom(draw));

        await rig.Resolver.ResolveAsync(Task(explorationRate: rate), "I need Friday off", "t", TestContext.Current.CancellationToken);

        rig.Model.Requests.Should().BeEmpty();
        rig.Sink.Traces["t"].Outcome!.ExploredOutput.Should().BeNull();
        rig.Statistics.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_task_without_a_tree_goes_from_memory_straight_to_the_full_fallback_without_a_judgment()
    {
        // Free-answer tasks can gain nothing from judgments (their fallback rarely writes the organisation's answer;
        // accuracy comes from memory), so a task may be defined with a bare root: nothing to judge, nothing to pay.
        var bare = OntologyYaml.Parse("id: root").Root;
        var task = new TaskDefinition("support", new TextContract(), bare, new TaskPolicy { Thresholds = Strict, MemoryThreshold = 0.9 });
        var rig = new Rig(Judgments(), "Your parcel ships tomorrow.");
        rig.Memory.Items.Add(("refund please", "Refunds take five days.", 0.95));

        var remembered = await rig.Resolver.ResolveAsync(task, "refund please", "t1", TestContext.Current.CancellationToken);
        rig.Memory.Items.Clear();
        rig.Memory.Items.Add(("refund please", "Refunds take five days.", 0.5));
        var generated = await rig.Resolver.ResolveAsync(task, "where is my parcel", "t2", TestContext.Current.CancellationToken);

        (remembered.Output, remembered.Mode).Should().Be(("Refunds take five days.", "memory"));
        (generated.Output, generated.Mode).Should().Be(("Your parcel ships tomorrow.", "fallback"));
        rig.Judge.Shown.Should().BeEmpty();
        rig.Sink.Traces["t2"].Outcome!.Path.Should().ContainSingle().Which.Outcome.Should().Be("skip");
    }

    [Fact]
    public async Task A_known_answer_that_is_not_a_habit_is_shown_as_a_shadow_and_picking_it_defers_to_the_fallback()
    {
        // A request already answered "leave_balance" by the fallback under "work" and confirmed. The next one like it
        // must not be absorbed by the sibling habit "pto": the shadow is shown, picked, and the fallback answers.
        var shadow = ShadowIndex.Id("work", "leave_balance");
        var evidence = new FixedEvidence(new ShadowEvidence("how many days off do I have left", "work", "fallback", "leave_balance", "correct", null, null));
        var rig = new Rig(Judgments(("work", 0.95), (shadow, 0.95)), null, evidence, "leave_balance");

        // The contract knows more answers than the tree has habits for, as in any task still growing its tree.
        var contract = OntologyYaml.Parse("""
            id: root
            children:
              - id: work
                label: Work
                description: office matters
                options:
                  - {id: pto, kind: answer, label: pto, description: book a day off, text: pto_request}
                  - {id: leave, kind: answer, label: leave, description: days off left, text: leave_balance}
            """).Root;
        var task = Task(shadows: true) with { Contract = new TreeAnswerContract(contract) };

        var result = await rig.Resolver.ResolveAsync(task, "days of leave remaining?", "t", TestContext.Current.CancellationToken);

        (result.Output, result.Mode).Should().Be(("leave_balance", "partial"));
        rig.Judge.Shown[1].Select(c => c.Id).Should().Contain(shadow);
        rig.Sink.Traces["t"].Outcome!.Path[^1].Chosen.Should().Be(shadow);
        evidence.Asked.Should().Equal("support");
    }

    [Fact]
    public async Task Shadows_are_off_unless_the_task_asks_for_them()
    {
        var evidence = new FixedEvidence(new ShadowEvidence("how many days off do I have left", "work", "fallback", "leave_balance", "correct", null, null));
        var rig = new Rig(Judgments(("work", 0.95), ("pto", 0.95)), null, evidence);

        await rig.Resolver.ResolveAsync(Task(), "days of leave remaining?", "t", TestContext.Current.CancellationToken);

        rig.Judge.Shown[1].Select(c => c.Id).Should().NotContain(id => ShadowIndex.IsShadow(id));
        evidence.Asked.Should().BeEmpty();
    }

    private sealed class FixedEvidence(params ShadowEvidence[] rows) : IShadowEvidenceSource
    {
        public List<string> Asked { get; } = [];

        public IReadOnlyList<ShadowEvidence> ShadowEvidence(string task)
        {
            Asked.Add(task);
            return rows;
        }
    }

    private static TaskDefinition Task(FallbackScope scope = FallbackScope.Full, bool allowFallback = true, double? memoryThreshold = null, double explorationRate = 0, bool shadows = false) =>
        new("support", new TreeAnswerContract(Tree), Tree, new TaskPolicy
        {
            Thresholds = Strict,
            FallbackScope = scope,
            AllowFallback = allowFallback,
            MemoryThreshold = memoryThreshold,
            ExplorationRate = explorationRate,
            Shadows = shadows,
        });

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private static (string? Choice, double Confidence)[] Judgments(params (string? Choice, double Confidence)[] script) => script;

    private sealed class Rig
    {
        public Rig((string? Choice, double Confidence)[] judgments, params string[] answers)
            : this(judgments, null, answers)
        {
        }

        public Rig((string? Choice, double Confidence)[] judgments, Random? random, params string[] answers)
            : this(judgments, random, null, answers)
        {
        }

        public Rig((string? Choice, double Confidence)[] judgments, Random? random, IShadowEvidenceSource? shadows, params string[] answers)
        {
            Model = new ScriptedModel(answers);
            Judge = new ScriptedJudge(judgments);
            var recorder = new CallRecorder(Model, new EnergyModel(1, 0, 0, 0), Sink);
            Resolver = new Resolver(
                new GreedyTraverser(Judge),
                new FallbackGenerator(recorder, maxAttempts: 1),
                new SlotFiller(recorder),
                Sink,
                Memory,
                Statistics,
                random,
                shadows);
        }

        public ScriptedJudge Judge { get; }

        public RecordingStatistics Statistics { get; } = new();

        public ListSink Sink { get; } = new();

        public FakeMemory Memory { get; } = new();

        public ScriptedModel Model { get; }

        public Resolver Resolver { get; }
    }

    private sealed class ScriptedJudge((string? Choice, double Confidence)[] script) : IJudge
    {
        private int _next;

        public List<IReadOnlyList<Candidate>> Shown { get; } = [];

        public Task<Judgment> JudgeAsync(string state, IReadOnlyList<Candidate> candidates, string traceId, string? nodeId = null, int? layer = null, CancellationToken cancellationToken = default)
        {
            Shown.Add(candidates);
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

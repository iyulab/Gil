using AwesomeAssertions;
using Gil.Fallback;
using Gil.Memory;
using Gil.Ontology;

namespace Gil.Tests.Memory;

public sealed class LexicalMemoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_lookup_finds_the_nearest_remembered_answer_without_a_model_call()
    {
        var memory = new LexicalMemory();

        var (empty, emptyCost) = await memory.LookupAsync("task", "refund please", "t0", Ct);
        var remembered = await memory.RememberAsync("task", "t1", "refund for my last order please", "refund_policy", "t1", Ct);
        await memory.RememberAsync("task", "t2", "my card was blocked at the shop", "cards_block", "t2", Ct);
        var (match, cost) = await memory.LookupAsync("task", "please refund my order", "t3", Ct);

        (empty, emptyCost, remembered, cost).Should().Be((null, 0.0, 0.0, 0.0));
        (match!.Source, match.Answer).Should().Be(("t1", "refund_policy"));
        match.Similarity.Should().BeInRange(0.3, 1.0);
    }

    [Fact]
    public async Task The_same_text_scores_one_and_unrelated_text_scores_near_zero()
    {
        var memory = new LexicalMemory();
        await memory.RememberAsync("task", "t1", "Printer on floor 3 is jammed", "facilities", "t1", Ct);
        await memory.RememberAsync("task", "t2", "VPN drops every ten minutes", "network", "t2", Ct);

        var (same, _) = await memory.LookupAsync("task", "printer  on FLOOR 3 is jammed", "q1", Ct);
        var (unrelated, _) = await memory.LookupAsync("task", "xyzzy", "q2", Ct);

        same!.Similarity.Should().BeApproximately(1.0, 1e-9); // case and whitespace are normalised away
        unrelated!.Similarity.Should().BeLessThan(0.1);
    }

    [Fact]
    public async Task Korean_text_matches_without_a_tokenizer_and_decomposed_hangul_is_composed_first()
    {
        var memory = new LexicalMemory();
        await memory.RememberAsync("task", "t1", "로그인 화면에서 비밀번호 오류가 납니다", "auth", "t1", Ct);
        await memory.RememberAsync("task", "t2", "결제 금액이 두 번 청구되었어요", "billing", "t2", Ct);

        var (match, _) = await memory.LookupAsync("task", "비밀번호 오류로 로그인이 안 돼요", "q1", Ct);
        // The same sentence typed as conjoining jamo (NFD) reads as the composed syllables.
        var (decomposed, _) = await memory.LookupAsync("task", "결제 금액이 두 번 청구되었어요".Normalize(System.Text.NormalizationForm.FormD), "q2", Ct);

        match!.Answer.Should().Be("auth");
        decomposed!.Answer.Should().Be("billing");
        decomposed.Similarity.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task Weights_follow_what_is_remembered_so_words_every_row_shares_count_for_less()
    {
        var memory = new LexicalMemory();
        await memory.RememberAsync("task", "a", "alpha common", "a", "a", Ct);
        var (alone, _) = await memory.LookupAsync("task", "common", "q1", Ct);

        await memory.RememberAsync("task", "b", "bravo common", "b", "b", Ct);
        await memory.RememberAsync("task", "c", "charlie common", "c", "c", Ct);
        var (shared, _) = await memory.LookupAsync("task", "common", "q2", Ct);

        memory.Forget("task", "b");
        memory.Forget("task", "c");
        var (restored, _) = await memory.LookupAsync("task", "common", "q3", Ct);

        shared!.Similarity.Should().BeLessThan(alone!.Similarity);
        restored!.Similarity.Should().BeApproximately(alone.Similarity, 1e-12);
    }

    [Fact]
    public async Task Weights_are_taken_afresh_in_steps_once_the_memory_has_grown_by_a_tenth()
    {
        var memory = new LexicalMemory();
        for (var i = 0; i < 20; i++)
        {
            await memory.RememberAsync("task", $"r{i}", $"row{i:D2} common", $"a{i}", $"r{i}", Ct);
        }

        var (taken, _) = await memory.LookupAsync("task", "common", "q1", Ct);
        await memory.RememberAsync("task", "r20", "row20 common", "a20", "r20", Ct);
        var (within, _) = await memory.LookupAsync("task", "common", "q2", Ct);
        await memory.RememberAsync("task", "r21", "row21 common", "a21", "r21", Ct);
        var (refreshed, _) = await memory.LookupAsync("task", "common", "q3", Ct);

        within!.Similarity.Should().Be(taken!.Similarity); // 21 rows: not yet a tenth more than 20
        refreshed!.Similarity.Should().BeLessThan(taken.Similarity); // 22 rows: a word every row shares counts for less
        (taken.Source, within.Source, refreshed.Source).Should().Be(("r0", "r0", "r0"));
    }

    [Fact]
    public async Task Ties_go_to_the_row_remembered_first_and_remembering_a_key_again_replaces_it()
    {
        var memory = new LexicalMemory();
        await memory.RememberAsync("task", "first", "same words", "one", "first", Ct);
        await memory.RememberAsync("task", "second", "same words", "two", "second", Ct);
        var (tie, _) = await memory.LookupAsync("task", "same words", "q1", Ct);

        await memory.RememberAsync("task", "first", "same words", "one again", "first", Ct);
        var (replaced, _) = await memory.LookupAsync("task", "same words", "q2", Ct);

        (tie!.Source, tie.Answer).Should().Be(("first", "one"));
        (replaced!.Source, replaced.Answer).Should().Be(("first", "one again"));
        memory.Count("task").Should().Be(2);
    }

    [Fact]
    public async Task Forgetting_a_row_keeps_the_remembered_order_of_the_rest_for_ties()
    {
        var memory = new LexicalMemory();
        foreach (var key in new[] { "a", "b", "c", "d" })
        {
            await memory.RememberAsync("task", key, "same words", key, key, Ct);
        }

        memory.Forget("task", "b"); // the last row takes its place
        memory.Forget("task", "a");
        var (tie, _) = await memory.LookupAsync("task", "same words", "q1", Ct);
        await memory.RememberAsync("task", "c", "same words", "c again", "c", Ct); // keeps its place in the order
        await memory.RememberAsync("task", "a", "same words", "a again", "a", Ct); // remembered anew: last
        var ranked = memory.Nearest("task", "same words", 3);

        tie!.Source.Should().Be("c");
        ranked.Select(m => (m.Source, m.Answer)).Should().Equal(("c", "c again"), ("d", "d"), ("a", "a again"));
    }

    [Fact]
    public async Task Forgetting_removes_the_answer_and_other_tasks_are_separate()
    {
        var memory = new LexicalMemory();
        await memory.RememberAsync("a", "t1", "refund please", "refund_policy", "t1", Ct);
        await memory.RememberAsync("a", "t2", "card blocked", "cards_block", "t2", Ct);

        memory.Forget("a", "t1");
        memory.Forget("missing", "t1");
        var (match, _) = await memory.LookupAsync("a", "refund please", "q1", Ct);
        var (other, _) = await memory.LookupAsync("b", "refund please", "q2", Ct);

        (memory.Count("a"), match!.Source, other).Should().Be((1, "t2", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    public async Task Empty_or_one_character_text_is_handled(string text)
    {
        var memory = new LexicalMemory();
        await memory.RememberAsync("task", "t1", text, "answer", "t1", Ct);
        await memory.RememberAsync("task", "t2", "a longer request", "other", "t2", Ct);

        var (match, _) = await memory.LookupAsync("task", text, "q1", Ct);

        match.Should().NotBeNull();
        match!.Similarity.Should().BeInRange(0.0, 1.0 + 1e-9);
    }

    [Fact]
    public void The_n_gram_range_is_validated()
    {
        FluentActions.Invoking(() => new LexicalMemory(minGram: 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new LexicalMemory(minGram: 3, maxGram: 2)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_replayed_feedback_history_fills_it_and_a_wrong_recall_stays_forgotten()
    {
        FeedbackEntry[] history =
        [
            new("t1", "refund please", "abstain", null, null, "wrong", "refund_policy"),
            new("t2", "my card is blocked", "abstain", null, null, "wrong", "cards_block"),
            new("t3", "refund please now", "memory", "refund_policy", new Recall("t1", 0.8, 0.5, true), "wrong", "billing_history"),
        ];
        var memory = new LexicalMemory();

        var energy = await MemoryReplay.From(history).ApplyAsync(memory, "task", "rebuild", Ct);
        var (match, _) = await memory.LookupAsync("task", "refund please", "q1", Ct);

        (energy, memory.Count("task")).Should().Be((0.0, 2));
        (match!.Source, match.Answer).Should().Be(("t3", "billing_history"));
    }

    [Fact]
    public async Task A_resolver_without_models_answers_from_memory_and_abstains_on_a_miss_without_a_call()
    {
        var sink = new ListSink();
        var memory = new LexicalMemory();
        var resolver = new Resolver(memory, sink);
        var task = BareTask(memoryThreshold: 0.5);

        var first = await resolver.ResolveAsync(task, "refund for my last order please", "t1", Ct);
        await resolver.FeedbackAsync(task, "t1", correct: false, correction: "refund_policy", cancellationToken: Ct);
        var second = await resolver.ResolveAsync(task, "please refund my last order", "t2", Ct);
        var third = await resolver.ResolveAsync(task, "the office printer is out of toner", "t3", Ct);

        (first.Mode, first.Output, first.Path.Count, first.Confidence, first.Energy).Should().Be(("abstain", null, 0, null, 0.0));
        first.Recall.Should().BeNull(); // memory was empty
        (second.Mode, second.Output, second.Recall!.Source, second.Recall.Hit).Should().Be(("memory", "refund_policy", "t1", true));
        (third.Mode, third.Recall!.Hit).Should().Be(("abstain", false));
        sink.Calls.Should().BeEmpty();
        sink.Traces["t3"].Outcome!.Mode.Should().Be("abstain");
    }

    [Fact]
    public async Task A_resolver_without_models_forgets_a_recalled_answer_marked_wrong()
    {
        var sink = new ListSink();
        var memory = new LexicalMemory();
        var resolver = new Resolver(memory, sink);
        var task = BareTask(memoryThreshold: 0.5);
        await memory.RememberAsync(task.Name, "seed", "refund please", "refund_policy", "seed", Ct);

        var recalled = await resolver.ResolveAsync(task, "refund please", "t1", Ct);
        await resolver.FeedbackAsync(task, "t1", correct: false, correction: "billing_history", cancellationToken: Ct);
        var after = await resolver.ResolveAsync(task, "refund please", "t2", Ct);

        (recalled.Mode, recalled.Output).Should().Be(("memory", "refund_policy"));
        (after.Output, after.Recall!.Source).Should().Be(("billing_history", "t1"));
        memory.Count(task.Name).Should().Be(1);
    }

    [Fact]
    public async Task Without_a_memory_threshold_a_resolver_without_models_abstains_on_everything()
    {
        var memory = new LexicalMemory();
        await memory.RememberAsync("support", "seed", "refund please", "refund_policy", "seed", Ct);

        var result = await new Resolver(memory).ResolveAsync(BareTask(memoryThreshold: null), "refund please", cancellationToken: Ct);

        (result.Mode, result.Recall).Should().Be(("abstain", null));
    }

    [Fact]
    public void A_resolver_without_models_needs_a_memory()
    {
        FluentActions.Invoking(() => new Resolver((IMemory)null!)).Should().Throw<ArgumentNullException>();
    }

    private static TaskDefinition BareTask(double? memoryThreshold)
    {
        var root = OntologyYaml.Parse("id: root").Root;
        return new TaskDefinition("support", new TextContract(), root, new TaskPolicy { Thresholds = new([1.0], 1.0), MemoryThreshold = memoryThreshold }, PromptLanguage.English);
    }

    [Fact]
    public async Task Nearest_lists_the_most_similar_first_and_agrees_with_lookup()
    {
        var memory = new LexicalMemory();
        memory.Nearest("task", "anything", 3).Should().BeEmpty();
        await memory.RememberAsync("task", "t1", "refund for my last order please", "refund_policy", "t1", Ct);
        await memory.RememberAsync("task", "t2", "my card was blocked at the shop", "cards_block", "t2", Ct);
        await memory.RememberAsync("task", "t3", "please refund my order", "refund_policy", "t3", Ct);
        await memory.RememberAsync("task", "t4", "please refund my order", "refund_duplicate", "t4", Ct);

        var top = memory.Nearest("task", "please refund my order", 3);
        var (single, _) = await memory.LookupAsync("task", "please refund my order", "q", Ct);

        top.Select(m => m.Source).Should().Equal("t3", "t4", "t1"); // the tie keeps remembered order
        top[0].Should().Be(single);
        top.Select(m => m.Similarity).Should().BeInDescendingOrder();
        memory.Nearest("task", "please refund my order", 10).Should().HaveCount(4);
    }

    [Fact]
    public async Task Rows_replaced_and_forgotten_many_times_leave_no_trace_in_lookups()
    {
        var memory = new LexicalMemory();
        await memory.RememberAsync("task", "kept", "my card was blocked at the shop", "cards_block", "s", Ct);
        for (var i = 0; i < 40; i++)
        {
            // Replacing and forgetting leaves gone rows behind until they outnumber the live ones and are dropped.
            await memory.RememberAsync("task", "moving", $"refund order number {i} please", $"answer_{i}", "s", Ct);
            await memory.RememberAsync("task", $"brief_{i}", $"where is parcel {i}", "tracking", "s", Ct);
            memory.Forget("task", $"brief_{i}");
        }

        var all = memory.Nearest("task", "refund order number 39 please", 10);
        var stale = memory.Nearest("task", "refund order number 0 please", 10);

        all.Select(m => m.Source).Should().Equal("moving", "kept");
        all[0].Answer.Should().Be("answer_39");
        all[0].Similarity.Should().BeApproximately(1.0, 1e-12);
        stale.Select(m => m.Source).Should().Equal("moving", "kept"); // the old text is gone with the row it was in
        stale[0].Answer.Should().Be("answer_39");
        memory.Count("task").Should().Be(2);
    }
}

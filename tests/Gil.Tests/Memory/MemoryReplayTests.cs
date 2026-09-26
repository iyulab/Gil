using AwesomeAssertions;
using Gil.Llm;
using Gil.Memory;

namespace Gil.Tests.Memory;

public sealed class MemoryReplayTests
{
    private static readonly FeedbackEntry[] History =
    [
        new("t1", "refund please", "fallback", "refund_policy", null, "correct", null),
        new("t2", "cancel my plan", "fallback", "refund_policy", null, "wrong", "cancel_plan"),
        new("t3", "my card was charged twice", "fallback", "refund_policy", null, "wrong", null),
        new("t4", "refund for last month", "memory", "refund_policy", new Recall("t1", 0.93, 0.9, true), "wrong", "billing_history"),
    ];

    [Fact]
    public void Keeps_confirmed_answers_and_corrections_and_forgets_what_a_wrong_recall_served()
    {
        var replay = MemoryReplay.From(History);

        replay.Remember.Should().Equal(
            new ConfirmedAnswer("t2", "cancel my plan", "cancel_plan"),
            new ConfirmedAnswer("t4", "refund for last month", "billing_history"));
        replay.Forget.Should().Equal("t1");
    }

    [Fact]
    public async Task Applies_to_any_memory_through_its_interface_forgetting_first()
    {
        var memory = new Recording();

        var energy = await MemoryReplay.From(History).ApplyAsync(memory, "task", "rebuild", TestContext.Current.CancellationToken);

        memory.Calls.Should().Equal("forget t1", "remember t2 cancel_plan as rebuild", "remember t4 billing_history as rebuild");
        energy.Should().Be(2);
    }

    [Fact]
    public async Task A_rebuild_charges_its_embeddings_to_its_own_trace_not_to_the_requests_it_replays()
    {
        var sink = new ListSink();
        var memory = new EmbeddingMemory(new EmbeddingRecorder(new OneVector(), new EnergyModel(0, 1, 0, 0), sink));

        await MemoryReplay.From(History).ApplyAsync(memory, "task", "rebuild", TestContext.Current.CancellationToken);
        var (match, _) = await memory.LookupAsync("task", "cancel my plan", "later", TestContext.Current.CancellationToken);

        sink.Calls.Take(2).Select(c => c.TraceId).Should().Equal("rebuild", "rebuild");
        match!.Source.Should().Be("t2", "rows are still keyed by the request they came from");
    }

    private sealed class OneVector : IEmbeddingModel
    {
        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult([.. texts.Select(_ => new float[] { 1, 0 })], "e", 3, 1, "{}"));
    }

    private sealed class Recording : IMemory
    {
        public List<string> Calls { get; } = [];

        public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no lookup expected");

        public Task<double> RememberAsync(string task, string key, string state, string answer, string traceId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"remember {key} {answer} as {traceId}");
            return Task.FromResult(1.0);
        }

        public void Forget(string task, string key) => Calls.Add($"forget {key}");
    }
}

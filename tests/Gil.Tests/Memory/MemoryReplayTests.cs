using AwesomeAssertions;
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

        var energy = await MemoryReplay.From(History).ApplyAsync(memory, "task", TestContext.Current.CancellationToken);

        memory.Calls.Should().Equal("forget t1", "remember t2 cancel_plan", "remember t4 billing_history");
        energy.Should().Be(2);
    }

    private sealed class Recording : IMemory
    {
        public List<string> Calls { get; } = [];

        public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no lookup expected");

        public Task<double> RememberAsync(string task, string traceId, string state, string answer, CancellationToken cancellationToken = default)
        {
            Calls.Add($"remember {traceId} {answer}");
            return Task.FromResult(1.0);
        }

        public void Forget(string task, string traceId) => Calls.Add($"forget {traceId}");
    }
}

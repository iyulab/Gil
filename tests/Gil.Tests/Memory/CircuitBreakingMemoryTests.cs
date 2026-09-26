using AwesomeAssertions;
using Gil.Memory;

namespace Gil.Tests.Memory;

public sealed class CircuitBreakingMemoryTests
{
    [Fact]
    public async Task After_a_failure_calls_are_refused_at_once_until_the_cooldown_ends()
    {
        var inner = new FlakyMemory { Failure = new HttpRequestException("refused") };
        var clock = new ManualClock();
        var memory = new CircuitBreakingMemory(inner, TimeSpan.FromSeconds(30), clock);

        await Lookup(memory).Should().ThrowAsync<HttpRequestException>();
        clock.Advance(TimeSpan.FromSeconds(29));
        (await Lookup(memory).Should().ThrowAsync<MemoryUnavailableException>()).Which.Message.Should().Contain("HttpRequestException: refused");
        var remember = () => memory.RememberAsync("task", "t", "s", "a", "t", TestContext.Current.CancellationToken);
        await remember.Should().ThrowAsync<MemoryUnavailableException>();

        inner.Calls.Should().Be(1, "refused calls never reach the endpoint");
    }

    [Fact]
    public async Task The_first_call_after_the_cooldown_is_a_trial_that_closes_the_circuit_when_it_succeeds()
    {
        var inner = new FlakyMemory { Failure = new HttpRequestException("refused") };
        var clock = new ManualClock();
        var memory = new CircuitBreakingMemory(inner, TimeSpan.FromSeconds(30), clock);
        await Lookup(memory).Should().ThrowAsync<HttpRequestException>();

        clock.Advance(TimeSpan.FromSeconds(30));
        await Lookup(memory).Should().ThrowAsync<HttpRequestException>("the trial failed");
        await Lookup(memory).Should().ThrowAsync<MemoryUnavailableException>("so the circuit opened again");

        clock.Advance(TimeSpan.FromSeconds(30));
        inner.Failure = null;
        (await memory.LookupAsync("task", "s", "t", TestContext.Current.CancellationToken)).Match.Should().NotBeNull();
        (await memory.LookupAsync("task", "s", "t", TestContext.Current.CancellationToken)).Match.Should().NotBeNull();
        inner.Calls.Should().Be(4);
    }

    [Fact]
    public async Task A_cancelled_call_does_not_open_the_circuit_and_forgetting_is_never_refused()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var inner = new FlakyMemory { Failure = new OperationCanceledException(cancel.Token) };
        var memory = new CircuitBreakingMemory(inner, TimeSpan.FromSeconds(30), new ManualClock());

        var cancelled = () => memory.LookupAsync("task", "s", "t", cancel.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        inner.Failure = null;
        (await memory.LookupAsync("task", "s", "t", TestContext.Current.CancellationToken)).Match.Should().NotBeNull();

        inner.Failure = new HttpRequestException("refused");
        await Lookup(memory).Should().ThrowAsync<HttpRequestException>();
        memory.Forget("task", "t");
        inner.Forgotten.Should().Equal("t");
    }

    [Fact]
    public async Task A_refused_lookup_names_the_original_failure_in_the_recall_a_resolver_records()
    {
        var memory = new CircuitBreakingMemory(new FlakyMemory { Failure = new HttpRequestException("refused") }, TimeSpan.FromMinutes(1), new ManualClock());
        await Lookup(memory).Should().ThrowAsync<HttpRequestException>();

        var failed = Recall.Failed(0.9, (await Lookup(memory).Should().ThrowAsync<MemoryUnavailableException>()).Which);

        failed.Error.Should().StartWith("MemoryUnavailableException: memory skipped: it failed within the last 60 s");
    }

    private static Func<Task<(MemoryMatch? Match, double Energy)>> Lookup(CircuitBreakingMemory memory) =>
        () => memory.LookupAsync("task", "s", "t", TestContext.Current.CancellationToken);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FlakyMemory : IMemory
    {
        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public List<string> Forgotten { get; } = [];

        public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Failure is null ? Task.FromResult<(MemoryMatch?, double)>((new MemoryMatch("seed", 0.95, "answer"), 0)) : Task.FromException<(MemoryMatch?, double)>(Failure);
        }

        public Task<double> RememberAsync(string task, string key, string state, string answer, string traceId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Failure is null ? Task.FromResult(0.0) : Task.FromException<double>(Failure);
        }

        public void Forget(string task, string traceId) => Forgotten.Add(traceId);
    }
}

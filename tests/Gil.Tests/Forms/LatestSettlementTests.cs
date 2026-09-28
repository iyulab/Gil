using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

/// <summary>Where settled documents disagree, the later settlement wins — whatever order the documents arrive in.</summary>
public sealed class LatestSettlementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FormDefinition Ticket = new(
        "ticket",
        [
            new FieldDefinition("summary", FieldRole.Observed),
            new FieldDefinition("team", FieldRole.Judged) { MemoryThreshold = 0.3 },
        ],
        PromptLanguage.English);

    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    private static SettledDocument Doc(string id, string summary, string? team, DateTimeOffset settledAt) =>
        new(id, team is null
            ? new Dictionary<string, string> { ["summary"] = summary }
            : new Dictionary<string, string> { ["summary"] = summary, ["team"] = team }, settledAt);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<string?> SimilarAnswer(LexicalMemory memory, string summary) =>
        (await memory.LookupAsync("ticket/team", $"summary: {summary}", "q", Ct)).Match?.Answer;

    [Fact]
    public void Values_settled_equally_often_rank_by_the_latest_settlement_not_by_arrival()
    {
        var older = Doc("b", "vpn drops", "facilities", Monday);
        var newer = Doc("a", "vpn drops", "network", Monday.AddDays(1));
        var known = new Dictionary<string, string> { ["summary"] = "vpn drops" };

        foreach (var order in new[] { new[] { older, newer }, [newer, older] })
        {
            var memory = new FieldMemory();
            foreach (var document in order)
            {
                memory.Put(Ticket, document);
            }

            // Tied one to one under the key and overall; the ordinal order alone would put "facilities" first.
            memory.Rank(Ticket, "team", known, 3).Select(c => c.Value).Should().Equal("network", "facilities");
            memory.Rank(Ticket, "team", new Dictionary<string, string>(), 3).Select(c => c.Value).Should().Equal("network", "facilities");
        }
    }

    [Fact]
    public async Task Documents_of_the_same_case_are_answered_by_the_latest_whatever_the_rebuild_order()
    {
        var documents = new[]
        {
            Doc("d1", "vpn drops every ten minutes", "facilities", Monday),
            Doc("d2", "VPN drops  every ten minutes", "network", Monday.AddDays(2)), // the same case after normalisation
            Doc("d3", "vpn drops every ten minutes", "security", Monday.AddDays(1)),
        };

        foreach (var order in new[] { documents, [.. documents.Reverse()], [documents[1], documents[0], documents[2]] })
        {
            var memory = new LexicalMemory();
            await new FormResolver(new FieldMemory(), memory).RebuildAsync(Ticket, order, Ct);
            (await SimilarAnswer(memory, "vpn drops every ten minutes")).Should().Be("network");
        }
    }

    [Fact]
    public async Task When_the_latest_of_a_case_changes_the_next_latest_answers_again()
    {
        var memory = new LexicalMemory();
        var resolver = new FormResolver(new FieldMemory(), memory);
        await resolver.RebuildAsync(Ticket, [Doc("d1", "vpn drops", "facilities", Monday), Doc("d2", "vpn drops", "network", Monday.AddDays(1))], Ct);

        // The latest document moves to another case: the one it superseded answers again.
        await resolver.RebuildAsync(Ticket, [Doc("d2", "printer jammed", "network", Monday.AddDays(1))], Ct);
        (await SimilarAnswer(memory, "vpn drops")).Should().Be("facilities");

        // Then its value is cleared: it is forgotten, and the other case is left as it was.
        await resolver.RebuildAsync(Ticket, [Doc("d2", "printer jammed", null, Monday.AddDays(1))], Ct);
        memory.Nearest("ticket/team", "summary: printer jammed", 5).Select(m => m.Source).Should().Equal("d1");
    }

    [Fact]
    public async Task A_partial_rebuild_after_live_settling_leaves_the_latest_in_place()
    {
        var memory = new LexicalMemory();
        var clock = new Clock(Monday.AddDays(5));
        var resolver = new FormResolver(new FieldMemory(), memory, timeProvider: clock);
        await resolver.RebuildAsync(Ticket, [Doc("d1", "vpn drops", "facilities", Monday)], Ct);

        var session = resolver.Open(Ticket, "d2");
        await session.ObserveAsync("summary", "vpn drops", Ct);
        await session.SettleAsync("team", Settlement.Correct("network"), Ct);

        // An older saved copy of d1 arrives again, as after a sync: it must not take the case back.
        await resolver.RebuildAsync(Ticket, [Doc("d1", "vpn drops", "facilities", Monday)], Ct);
        (await SimilarAnswer(memory, "vpn drops")).Should().Be("network");
    }

    [Fact]
    public async Task Restoring_keeps_the_saved_time_and_only_accepting_or_correcting_moves_it()
    {
        var clock = new Clock(Monday.AddDays(5));
        var resolver = new FormResolver(new FieldMemory(), timeProvider: clock);

        var reopened = resolver.Open(Ticket, "d1", Monday);
        await reopened.ObserveAsync("summary", "vpn drops", Ct);
        await reopened.SettleAsync("team", Settlement.Restore("facilities"), Ct);
        reopened.Snapshot().SettledAt.Should().Be(Monday);

        await reopened.SettleAsync("team", Settlement.Reject(), Ct);
        await reopened.SettleAsync("team", Settlement.Revert(), Ct);
        reopened.Snapshot().SettledAt.Should().Be(Monday);

        clock.Now = Monday.AddDays(6);
        await reopened.SettleAsync("team", Settlement.Correct("network"), Ct);
        reopened.Snapshot().SettledAt.Should().Be(Monday.AddDays(6));

        resolver.Open(Ticket, "d2").Snapshot().SettledAt.Should().Be(Monday.AddDays(6)); // a new document starts at the present
    }

    [Fact]
    public async Task Threshold_selection_replays_in_settlement_order_with_one_document_per_case()
    {
        var documents = new[]
        {
            Doc("d3", "vpn drops", "network", Monday.AddDays(2)),
            Doc("d1", "vpn drops", "facilities", Monday),
            Doc("d2", "printer jammed", "facilities", Monday.AddDays(1)),
        };

        // Settlement order: d1, d2, d3. d3's lookup finds d1 at similarity 1 with the answer it corrected — wrong, so no
        // threshold down to 1 meets full precision — and d3 then replaces d1 as its case's representative.
        var memory = new LexicalMemory();
        var choice = await ThresholdSelection.SelectAsync(memory, Ticket, "team", documents, 1.0, minimumAnswered: 1, Ct);

        choice.Should().BeNull();
        (await memory.LookupAsync("ticket/team", "summary: vpn drops", "q", Ct)).Match!.Source.Should().Be("d3");
        (await memory.LookupAsync("ticket/team", "summary: vpn drops", "q", Ct)).Match!.Answer.Should().Be("network");
    }
}

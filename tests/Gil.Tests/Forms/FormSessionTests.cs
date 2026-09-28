using System.Globalization;
using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

public sealed class FormSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FormDefinition Ticket = new(
        "ticket",
        [
            new FieldDefinition("reporter", FieldRole.Observed) { UseAsEvidence = false },
            new FieldDefinition("component", FieldRole.Observed),
            new FieldDefinition("summary", FieldRole.Observed),
            new FieldDefinition("severity", FieldRole.Judged) { DependsOn = ["component"] },
            new FieldDefinition("team", FieldRole.Judged) { MemoryThreshold = 0.3 },
            new FieldDefinition("assessment", FieldRole.Judged) { Policy = FieldPolicy.Off },
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(string id, params (string Field, string Value)[] values) =>
        new(id, values.ToDictionary(v => v.Field, v => v.Value), At(id));

    /// <summary>Documents settle in the order of their numbers, a minute apart.</summary>
    private static DateTimeOffset At(string id) => DateTimeOffset.UnixEpoch.AddMinutes(int.Parse(id[1..], CultureInfo.InvariantCulture));

    private static readonly SettledDocument[] History =
    [
        Doc("d1", ("reporter", "Kim"), ("component", "vpn"), ("summary", "vpn drops every ten minutes"), ("severity", "high"), ("team", "network")),
        Doc("d2", ("reporter", "Lee"), ("component", "printer"), ("summary", "printer on floor 3 is jammed"), ("severity", "low"), ("team", "facilities")),
        Doc("d3", ("reporter", "Lee"), ("component", "printer"), ("summary", "toner is empty"), ("severity", "low"), ("team", "facilities")),
    ];

    [Fact]
    public async Task Suggestions_come_from_values_settled_alongside_the_known_ones_without_a_model()
    {
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(Ticket, History, Ct);
        var session = resolver.Open(Ticket, "d4");

        var afterComponent = await session.ObserveAsync("component", "printer", Ct);

        // severity depends on component and team has no declared dependencies, so both are suggested afresh;
        // assessment is Off and never suggested.
        afterComponent.Select(s => s.Field).Should().Equal("severity", "team");
        var severity = afterComponent[0];
        (severity.Source, severity.Policy, severity.Confidence, severity.Energy).Should().Be((FieldSource.SettledFieldMemory, FieldPolicy.Suggest, (double?)null, 0.0));
        severity.Candidates[0].Should().Be(new FieldCandidate("low", 1.0, FieldSource.SettledFieldMemory, "component: printer"));
        (await session.SuggestAsync(Ct)).Select(s => s.Field).Should().Equal("severity", "team");
    }

    [Fact]
    public async Task Only_open_fields_whose_evidence_changed_are_suggested_again()
    {
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(Ticket, History, Ct);
        var session = resolver.Open(Ticket, "d4");

        // summary is not a declared dependency of severity; reporter is evidence for nothing.
        (await session.ObserveAsync("summary", "the vpn is slow", Ct)).Select(s => s.Field).Should().Equal("team");
        (await session.ObserveAsync("reporter", "Kim", Ct)).Should().BeEmpty();

        // Settling severity changes team's evidence; severity itself is no longer open.
        (await session.SettleAsync("severity", Settlement.Accept("high"), Ct)).Select(s => s.Field).Should().Equal("team");

        // A rejected field is not offered again, but reverting reopens it.
        (await session.SettleAsync("team", Settlement.Reject(), Ct)).Should().BeEmpty();
        (await session.SuggestAsync(Ct)).Should().BeEmpty();
        (await session.SettleAsync("team", Settlement.Revert(), Ct)).Select(s => s.Field).Should().Equal("team");
    }

    [Fact]
    public async Task Correcting_an_accepted_value_leaves_only_the_correction_in_memory()
    {
        var memory = new FieldMemory();
        var resolver = new FormResolver(memory);
        var session = resolver.Open(Ticket, "d1");
        await session.ObserveAsync("component", "vpn", Ct);

        await session.SettleAsync("severity", Settlement.Accept("low"), Ct);
        await session.SettleAsync("severity", Settlement.Correct("high"), Ct);

        memory.Count("ticket").Should().Be(1);
        memory.Rank(Ticket, "severity", new Dictionary<string, string> { ["component"] = "vpn" }, 3)
            .Select(c => (c.Value, c.Score)).Should().Equal(("high", 1.0));
    }

    [Fact]
    public async Task A_session_reaches_the_state_a_rebuild_from_its_snapshot_reaches()
    {
        var (liveFields, liveDocuments) = (new FieldMemory(), new LexicalMemory());
        var live = new FormResolver(liveFields, liveDocuments);
        var session = live.Open(Ticket, "d1");
        await session.ObserveAsync("summary", "vpn drops", Ct);
        await session.SettleAsync("team", Settlement.Accept("facilities"), Ct);
        await session.ObserveAsync("component", "vpn", Ct);
        await session.ObserveAsync("summary", "vpn drops every ten minutes", Ct);
        await session.SettleAsync("team", Settlement.Correct("network"), Ct);
        await session.SettleAsync("severity", Settlement.Accept("high"), Ct);

        var (rebuiltFields, rebuiltDocuments) = (new FieldMemory(), new LexicalMemory());
        await new FormResolver(rebuiltFields, rebuiltDocuments).RebuildAsync(Ticket, [session.Snapshot()], Ct);

        session.Snapshot().Values.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["summary"] = "vpn drops every ten minutes", ["component"] = "vpn", ["team"] = "network", ["severity"] = "high",
        });
        foreach (var field in new[] { "severity", "team" })
        {
            var known = new Dictionary<string, string> { ["component"] = "vpn", ["summary"] = "vpn drops" };
            liveFields.Rank(Ticket, field, known, 3).Should().Equal(rebuiltFields.Rank(Ticket, field, known, 3));
        }

        var (liveMatch, _) = await liveDocuments.LookupAsync("ticket/team", "component: vpn\nsummary: vpn drops", "q", Ct);
        var (rebuiltMatch, _) = await rebuiltDocuments.LookupAsync("ticket/team", "component: vpn\nsummary: vpn drops", "q", Ct);
        // The same document under the same key with the same answer; the similarity itself follows the memory's own
        // weighting schedule, which counts how the rows arrived.
        (liveMatch!.Source, liveMatch.Answer).Should().Be((rebuiltMatch!.Source, rebuiltMatch.Answer));
        liveMatch.Answer.Should().Be("network");
    }

    [Fact]
    public async Task Restoring_a_saved_document_after_a_rebuild_does_not_count_it_twice()
    {
        var memory = new FieldMemory();
        var resolver = new FormResolver(memory);
        await resolver.RebuildAsync(Ticket, History, Ct);
        var before = memory.Rank(Ticket, "team", new Dictionary<string, string> { ["component"] = "printer" }, 3);

        var session = resolver.Open(Ticket, "d2");
        foreach (var (field, value) in History[1].Values)
        {
            if (Ticket.Field(field).Role == FieldRole.Observed)
            {
                await session.ObserveAsync(field, value, Ct);
            }
            else
            {
                await session.SettleAsync(field, Settlement.Restore(value), Ct);
            }
        }

        memory.Count("ticket").Should().Be(3);
        memory.Rank(Ticket, "team", new Dictionary<string, string> { ["component"] = "printer" }, 3).Should().Equal(before);
    }

    [Fact]
    public async Task A_similar_document_outranks_a_guess_without_evidence_but_not_a_value_backed_by_a_key()
    {
        var sink = new ListSink();
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory(), sink: sink);
        await resolver.RebuildAsync(Ticket, History, Ct);
        var session = resolver.Open(Ticket, "d4");

        // No key matches: the similar document leads, the overall frequency follows.
        var guessed = await session.ObserveAsync("summary", "printer on floor 2 is jammed", Ct);
        var team = guessed.Single();
        team.Source.Should().Be(FieldSource.SimilarDocument);
        team.Candidates.Select(c => (c.Value, c.Source, c.Evidence)).Should().Equal(
            ("facilities", FieldSource.SimilarDocument, "d2"),
            ("network", FieldSource.SettledFieldMemory, null));

        // Once a key matches, its values lead.
        var keyed = await session.ObserveAsync("component", "vpn", Ct);
        keyed.Single(s => s.Field == "team").Candidates[0].Should().Be(new FieldCandidate("network", 1.0, FieldSource.SettledFieldMemory, "component: vpn"));

        // Every suggestion is traced under form/field with the evidence lines; the reporter is never among them.
        var trace = sink.Traces[team.TraceId];
        (trace.Task, trace.State, trace.Outcome!.Mode, trace.Outcome.Output).Should().Be(("ticket/team", "summary: printer on floor 2 is jammed", "memory", "facilities"));
        trace.Outcome.Recall!.Source.Should().Be("d2");
        await session.ObserveAsync("reporter", "Lee", Ct);
        sink.Traces.Values.Should().NotContain(t => t.State.Contains("reporter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_nothing_settled_a_person_decides()
    {
        var sink = new ListSink();
        var session = new FormResolver(new FieldMemory(), new LexicalMemory(), sink: sink).Open(Ticket, "d1");

        var suggestions = await session.SuggestAsync(Ct);

        suggestions.Should().OnlyContain(s => s.Source == FieldSource.None && s.Candidates.Count == 0);
        sink.Traces.Values.Should().OnlyContain(t => t.Outcome!.Mode == "abstain");
    }

    [Fact]
    public async Task Observed_and_judged_fields_take_their_own_events()
    {
        var session = new FormResolver(new FieldMemory()).Open(Ticket, "d1");

        await FluentActions.Awaiting(() => session.ObserveAsync("team", "network", Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => session.SettleAsync("component", Settlement.Accept("vpn"), Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => session.SettleAsync("owner", Settlement.Accept("x"), Ct)).Should().ThrowAsync<ArgumentException>();
    }
}

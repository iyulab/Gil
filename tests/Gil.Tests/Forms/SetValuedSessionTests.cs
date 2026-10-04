using System.Globalization;
using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

public sealed class SetValuedSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FormDefinition Paper = new(
        "paper",
        [
            new FieldDefinition("area", FieldRole.Observed),
            new FieldDefinition("topics", FieldRole.Judged) { Multiple = true, KeyThreshold = 0.3 },
            new FieldDefinition("lead", FieldRole.Judged),
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(string id, string area, string[] topics, string lead) =>
        new(id, new Dictionary<string, string> { ["area"] = area, ["lead"] = lead }, At(id))
        {
            Sets = new Dictionary<string, IReadOnlyList<string>> { ["topics"] = topics },
        };

    /// <summary>Documents settle in the order of their numbers, a minute apart.</summary>
    private static DateTimeOffset At(string id) => DateTimeOffset.UnixEpoch.AddMinutes(int.Parse(id[1..], CultureInfo.InvariantCulture));

    private static readonly SettledDocument[] History =
    [
        Doc("d1", "ai", ["nlp", "vision"], "kim"),
        Doc("d2", "ai", ["nlp"], "lee"),
        Doc("d3", "bio", ["genomics", "nlp"], "park"),
    ];

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    private sealed class EchoModel(params string[] values) : IFieldModel
    {
        public List<IReadOnlyList<KeyValuePair<string, string>>> Asked { get; } = [];

        public Task<FieldModelResult> SuggestAsync(FormDefinition form, string field, IReadOnlyList<KeyValuePair<string, string>> evidence, string traceId, CancellationToken cancellationToken = default)
        {
            Asked.Add(evidence);
            return Task.FromResult(new FieldModelResult([.. values.Select(v => new FieldCandidate(v, 0.9, FieldSource.Model, null))], 0.9, 0));
        }
    }

    [Fact]
    public async Task A_set_stays_open_once_settled_and_its_chosen_values_back_the_rest()
    {
        var resolver = new FormResolver(new FieldMemory(recencyDecay: 1), timeProvider: new Clock(Now));
        await resolver.RebuildAsync(Paper, History, Ct);
        var session = resolver.Open(Paper, "d4", At("d4"));

        var topics = (await session.ObserveAsync("area", "ai", Ct)).Single(s => s.Field == "topics");
        topics.Candidates.Select(c => (c.Value, c.Trusted)).Should().Equal(("nlp", true), ("vision", false), ("genomics", false));

        var after = await session.SettleAsync("topics", Settlement.Set(["nlp"]), Ct);

        // The set itself is suggested again — the rest, with nlp as evidence — and so is lead, which the set supports.
        after.Select(s => s.Field).Should().Equal("topics", "lead");
        after[0].Candidates.Select(c => (c.Value, c.Trusted)).Should().Equal(("vision", true), ("genomics", true));
        after[0].Answered.Should().BeTrue();

        var saved = session.Snapshot();
        saved.Sets["topics"].Should().Equal("nlp");
        saved.Values.Should().NotContainKey("topics");
        saved.Arrival.Should().Equal("area", "topics");
        saved.SettledAt.Should().Be(Now);
        (await session.SuggestAsync(Ct)).Select(s => s.Field).Should().Equal("topics", "lead");
    }

    [Fact]
    public async Task Settling_a_set_again_replaces_it_and_an_empty_set_clears_it()
    {
        var resolver = new FormResolver(new FieldMemory(recencyDecay: 1));
        var session = resolver.Open(Paper, "d4");

        await session.SettleAsync("topics", Settlement.Set(["vision", "nlp", "nlp"]), Ct);
        session.Snapshot().Sets["topics"].Should().Equal("nlp", "vision"); // once each, ordinally
        await session.SettleAsync("topics", Settlement.Set(["genomics"]), Ct);
        session.Snapshot().Sets["topics"].Should().Equal("genomics");
        await session.SettleAsync("topics", Settlement.Set([]), Ct);
        session.Snapshot().Sets.Should().NotContainKey("topics");
        session.Snapshot().Arrival.Should().BeEmpty();
    }

    [Fact]
    public async Task A_restored_set_keeps_the_saved_time_and_a_reverted_one_is_gone()
    {
        var resolver = new FormResolver(new FieldMemory(), timeProvider: new Clock(Now));
        var session = resolver.Open(Paper, "d1", At("d1"));

        await session.SettleAsync("topics", Settlement.Restore(["nlp", "vision"]), Ct);
        session.Snapshot().SettledAt.Should().Be(At("d1"));
        session.Snapshot().Sets["topics"].Should().Equal("nlp", "vision");

        await session.SettleAsync("topics", Settlement.Revert(), Ct);
        session.Snapshot().Sets.Should().NotContainKey("topics");
    }

    [Fact]
    public async Task A_settlement_must_match_the_kind_of_field()
    {
        var session = new FormResolver(new FieldMemory()).Open(Paper, "d4");

        await ((Func<Task>)(() => session.SettleAsync("topics", Settlement.Accept("nlp"), Ct))).Should()
            .ThrowAsync<ArgumentException>().WithMessage("*'topics' takes several values*");
        await ((Func<Task>)(() => session.SettleAsync("lead", Settlement.Set(["kim"]), Ct))).Should()
            .ThrowAsync<ArgumentException>().WithMessage("*'lead' takes one value*");
    }

    [Fact]
    public async Task A_stateless_suggestion_takes_the_values_chosen_so_far()
    {
        var resolver = new FormResolver(new FieldMemory(recencyDecay: 1));
        await resolver.RebuildAsync(Paper, History, Ct);
        var values = new Dictionary<string, string> { ["area"] = "ai", ["lead"] = "kim" };

        var suggestions = await resolver.SuggestAsync(Paper, "d4", values, new Dictionary<string, IReadOnlyList<string>> { ["topics"] = ["nlp"] }, Ct);

        suggestions.Select(s => s.Field).Should().Equal("topics"); // lead has a value; the set is open with one chosen
        suggestions[0].Candidates.Select(c => c.Value).Should().NotContain("nlp").And.StartWith("vision");

        var misplaced = new Dictionary<string, string> { ["topics"] = "nlp" };
        await ((Func<Task>)(() => resolver.SuggestAsync(Paper, "d4", misplaced, Ct))).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_model_reads_each_chosen_value_as_a_line_and_is_not_offered_them_again()
    {
        var model = new EchoModel("nlp", "robotics");
        var resolver = new FormResolver(new FieldMemory(), model: model);
        var session = resolver.Open(Paper, "d1");

        var topics = (await session.SettleAsync("topics", Settlement.Set(["vision", "nlp"]), Ct)).Single(s => s.Field == "topics");

        model.Asked[^1].Should().Equal(KeyValuePair.Create("topics", "nlp"), KeyValuePair.Create("topics", "vision"));
        topics.Candidates.Select(c => c.Value).Should().Equal("robotics");
    }

    private sealed class ThrowingMemory : IMemory
    {
        public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("looked up");

        public Task<double> RememberAsync(string task, string key, string state, string answer, string traceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0.0);

        public void Forget(string task, string key)
        {
        }
    }

    [Fact]
    public async Task A_set_is_not_looked_up_in_the_document_memory_and_its_trace_records_every_trusted_value()
    {
        var sink = new ListSink();
        var resolver = new FormResolver(new FieldMemory(recencyDecay: 1), documentMemory: new ThrowingMemory(), sink: sink);
        await resolver.RebuildAsync(Paper, History, Ct);
        var session = resolver.Open(Paper, "d4");

        var first = await session.ObserveAsync("area", "ai", Ct);
        var topics = first.Single(s => s.Field == "topics");
        var lead = first.Single(s => s.Field == "lead");
        sink.Traces[topics.TraceId].Outcome!.Recall.Should().BeNull(); // no lookup was made for the set
        sink.Traces[lead.TraceId].Outcome!.Recall!.Hit.Should().BeFalse(); // the single-valued field was looked up (and failed)
        sink.Traces[topics.TraceId].Outcome!.Output.Should().Be("nlp");

        var after = (await session.SettleAsync("topics", Settlement.Set(["nlp"]), Ct)).Single(s => s.Field == "topics");
        sink.Traces[after.TraceId].Outcome!.Output.Should().Be("vision\ngenomics");
    }
}

using AwesomeAssertions;
using Gil.Fallback;
using Gil.Forms;
using Gil.Memory;
using Gil.Ontology;

namespace Gil.Tests.Forms;

public sealed class FieldModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FormDefinition Ticket = new(
        "ticket",
        [
            new FieldDefinition("reporter", FieldRole.Observed) { UseAsEvidence = false },
            new FieldDefinition("component", FieldRole.Observed),
            new FieldDefinition("summary", FieldRole.Observed),
            new FieldDefinition("team", FieldRole.Judged),
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(string id, params (string Field, string Value)[] values) =>
        new(id, values.ToDictionary(v => v.Field, v => v.Value));

    /// <summary>Suggests one fixed value and remembers what it was asked.</summary>
    private sealed class FixedModel(string value, double confidence) : IFieldModel
    {
        public List<IReadOnlyList<KeyValuePair<string, string>>> Asked { get; } = [];

        public Task<FieldModelResult> SuggestAsync(FormDefinition form, string field, IReadOnlyList<KeyValuePair<string, string>> evidence, string traceId, CancellationToken cancellationToken = default)
        {
            Asked.Add(evidence);
            return Task.FromResult(new FieldModelResult([new FieldCandidate(value, confidence, FieldSource.Model, null)], confidence, 7));
        }
    }

    [Fact]
    public async Task The_model_is_asked_only_when_no_memory_has_evidence_and_then_leads_the_frequency_guess()
    {
        var model = new FixedModel("security", 0.8);
        var sink = new ListSink();
        var resolver = new FormResolver(new FieldMemory(), model: model, sink: sink);
        await resolver.RebuildAsync(Ticket, [Doc("d1", ("component", "vpn"), ("team", "network")), Doc("d2", ("component", "vpn"), ("team", "network"))], Ct);
        var session = resolver.Open(Ticket, "d3");

        // A known key: memory answers and the model is not asked.
        var keyed = (await session.ObserveAsync("component", "vpn", Ct)).Single();
        (keyed.Source, keyed.Confidence).Should().Be((FieldSource.SettledFieldMemory, (double?)null));
        model.Asked.Should().BeEmpty();

        // No key matches: the model leads, the overall frequency follows; the reporter never reaches the model.
        await session.ObserveAsync("reporter", "Kim", Ct);
        var modelled = (await session.ObserveAsync("component", "badge reader", Ct)).Single();
        (modelled.Source, modelled.Confidence, modelled.Energy).Should().Be((FieldSource.Model, (double?)0.8, 7.0));
        modelled.Candidates.Select(c => (c.Value, c.Source)).Should().Equal(("security", FieldSource.Model), ("network", FieldSource.SettledFieldMemory));
        model.Asked.Single().Select(e => (e.Key, e.Value)).Should().Equal(("component", "badge reader"));

        // The model does not close the trace here, so the form resolver does, with the model's answer.
        var trace = sink.Traces[modelled.TraceId];
        (trace.Task, trace.Outcome!.Mode, trace.Outcome.Output, trace.Outcome.Confidence).Should().Be(("ticket/team", "model", "security", (double?)0.8));
    }

    [Fact]
    public async Task A_resolver_behind_the_same_sink_records_the_suggestion_itself_so_feedback_reaches_it()
    {
        var sink = new ListSink();
        var answers = new LexicalMemory();
        await answers.RememberAsync("ticket/team", "earlier", "summary: badge reader is broken", "security", "t0", Ct);
        var bare = OntologyYaml.Parse("id: root").Root;
        var teamTask = new TaskDefinition("ticket/team", new TextContract(), bare,
            new TaskPolicy { Thresholds = new([1.0], 1.0), MemoryThreshold = 0.5 }, PromptLanguage.English);
        var model = new ResolverFieldModel(new Resolver(answers, sink), new Dictionary<string, TaskDefinition> { ["team"] = teamTask });
        var session = new FormResolver(new FieldMemory(), model: model, sink: sink).Open(Ticket, "d1");

        var suggestion = (await session.ObserveAsync("summary", "the badge reader is broken", Ct)).Single();

        suggestion.Source.Should().Be(FieldSource.Model);
        suggestion.Candidates[0].Value.Should().Be("security");
        var trace = sink.Traces[suggestion.TraceId];
        (trace.Task, trace.State, trace.Outcome!.Mode).Should().Be(("ticket/team", "summary: the badge reader is broken", "memory"));
        sink.Traces.Should().ContainSingle(); // one trace for the suggestion, closed by the resolver
    }

    [Fact]
    public async Task A_field_without_a_task_gets_no_model_suggestion()
    {
        var model = new ResolverFieldModel(new Resolver(new LexicalMemory()), new Dictionary<string, TaskDefinition>());

        var result = await model.SuggestAsync(Ticket, "team", [KeyValuePair.Create("summary", "x")], "t1", Ct);

        (result.Candidates.Count, result.Confidence, result.Energy).Should().Be((0, (double?)null, 0.0));
    }
}

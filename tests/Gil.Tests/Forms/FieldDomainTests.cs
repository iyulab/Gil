using System.Globalization;
using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

/// <summary>A field with <see cref="FieldDefinition.Candidates"/> never has a value outside them suggested, by any layer.</summary>
public sealed class FieldDomainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Categories = ["counsel", "referral", "crisis"];

    private static FormDefinition Intake(double? memoryThreshold = 0.3, double? keyThreshold = null) => new(
        "intake",
        [
            new FieldDefinition("channel", FieldRole.Observed),
            new FieldDefinition("note", FieldRole.Observed),
            new FieldDefinition("category", FieldRole.Judged)
            {
                Candidates = Categories,
                MemoryThreshold = memoryThreshold,
                KeyThreshold = keyThreshold,
            },
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(int number, string channel, string note, string category) =>
        new($"d{number}", new Dictionary<string, string> { ["channel"] = channel, ["note"] = note, ["category"] = category },
            DateTimeOffset.UnixEpoch.AddMinutes(number));

    private static Dictionary<string, string> Asked(SettledDocument document) =>
        document.Values.Where(v => v.Key != "category").ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void A_field_admits_any_value_when_open_and_only_its_candidates_when_closed()
    {
        var open = new FieldDefinition("category", FieldRole.Judged);
        var closed = open with { Candidates = Categories };

        open.Admits("anything").Should().BeTrue();
        closed.Admits("crisis").Should().BeTrue();
        closed.Admits("Crisis").Should().BeFalse();
        closed.Admits("other").Should().BeFalse();
    }

    [Fact]
    public async Task A_similar_document_settled_outside_the_list_is_neither_suggested_nor_reported()
    {
        var form = Intake();
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory(), similarDocumentCount: 5);
        var outside = Doc(4, "phone", "late night call, feels unsafe at home", "other-not-in-list");
        await resolver.RebuildAsync(form,
            [
                Doc(1, "phone", "asks about opening hours", "counsel"),
                Doc(2, "visit", "needs a lawyer for a housing dispute", "referral"),
                Doc(3, "phone", "feels unsafe at home tonight", "crisis"),
                outside,
            ], Ct);

        var suggestion = (await resolver.SuggestAsync(form, "d5", Asked(outside), Ct)).Single();

        suggestion.Candidates.Select(c => c.Value).Should().NotContain("other-not-in-list").And.OnlyContain(v => Categories.Contains(v));
        suggestion.Candidates.Should().NotContain(c => c.Source == FieldSource.SimilarDocument);
        suggestion.SimilarDocuments.Should().NotBeEmpty().And.NotContain(m => m.Source == "d4");
    }

    [Fact]
    public async Task The_key_layer_is_trusted_on_the_score_of_its_best_value_in_the_list()
    {
        // Under channel=phone, a value since dropped from the list was settled three times and "counsel" once: the dropped
        // value would score about 0.6 and pass the threshold, "counsel" about 0.2 and must not borrow that trust.
        var form = Intake(memoryThreshold: null, keyThreshold: 0.3);
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(form,
            [
                Doc(1, "phone", "a", "retired"),
                Doc(2, "phone", "b", "retired"),
                Doc(3, "phone", "c", "retired"),
                Doc(4, "phone", "d", "counsel"),
            ], Ct);

        var suggestion = (await resolver.SuggestAsync(form, "d5", new Dictionary<string, string> { ["channel"] = "phone", ["note"] = "e" }, Ct)).Single();

        suggestion.Candidates.Select(c => c.Value).Should().Equal("counsel");
        suggestion.Candidates[0].Score.Should().BeLessThan(0.3);
        suggestion.Answered.Should().BeFalse();
    }

    [Fact]
    public async Task A_model_value_outside_the_list_is_dropped()
    {
        var form = Intake(memoryThreshold: null);
        var resolver = new FormResolver(new FieldMemory(), model: new FixedModel("made-up", "crisis"));

        var suggestion = (await resolver.SuggestAsync(form, "d1", new Dictionary<string, string> { ["channel"] = "phone", ["note"] = "x" }, Ct)).Single();

        suggestion.Candidates.Select(c => c.Value).Should().Equal("crisis");
    }

    [Fact]
    public async Task A_replay_finds_a_similar_document_exactly_where_a_suggestion_offers_one()
    {
        // Each document is asked about with the ones before it remembered, as a replay does; where the nearest is settled
        // outside the list, neither offers the layer's value.
        var form = Intake();
        string[] notes = ["feels unsafe at home", "feels unsafe at home tonight", "feels unsafe at home tonight too", "asks about opening hours", "asks about opening hours today", "asks about opening hours today too"];
        string[] values = ["crisis", "other-not-in-list", "crisis", "counsel", "retired", "counsel"];
        var documents = notes.Select((note, i) => Doc(i + 1, "phone", note, values[i])).ToList();

        var offered = 0;
        for (var i = 1; i < documents.Count; i++)
        {
            var resolver = new FormResolver(new FieldMemory(), new LexicalMemory());
            await resolver.RebuildAsync(form, documents.Take(i), Ct);
            var suggestion = (await resolver.SuggestAsync(form, documents[i].DocumentId, Asked(documents[i]), Ct)).Single();
            offered += suggestion.Candidates.Count(c => c.Source == FieldSource.SimilarDocument);
        }

        var replay = await ThresholdSelection.SelectAsync(new LexicalMemory(), form, "category", documents, 0.5, minimumAnswered: 1, Ct);

        replay.Lookups.Should().Be(documents.Count - 1);
        replay.Candidates.Should().Be(offered);
        offered.Should().Be(replay.Lookups - 2); // the documents nearest to d2 and to d5 find nothing
    }

    private sealed class FixedModel(params string[] values) : IFieldModel
    {
        public Task<FieldModelResult> SuggestAsync(FormDefinition form, string field, IReadOnlyList<KeyValuePair<string, string>> evidence, string traceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FieldModelResult([.. values.Select(v => new FieldCandidate(v, 0.5, FieldSource.Model, null))], 0.5, 0));
    }
}

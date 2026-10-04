using System.Globalization;
using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

/// <summary>
/// A field with <see cref="FieldDefinition.CandidatesFrom"/> is offered only values that another field of the same document
/// holds, by every layer and every replay, once that field has a value.
/// </summary>
public sealed class DocumentDomainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FormDefinition Paper(double? mainKeyThreshold = 0.3, double? mainMemoryThreshold = null) => new(
        "paper",
        [
            new FieldDefinition("area", FieldRole.Observed),
            new FieldDefinition("abstract", FieldRole.Observed),
            new FieldDefinition("topics", FieldRole.Judged) { Multiple = true, KeyThreshold = 0.3 },
            new FieldDefinition("main", FieldRole.Judged)
            {
                CandidatesFrom = "topics",
                KeyThreshold = mainKeyThreshold,
                MemoryThreshold = mainMemoryThreshold,
                DependsOn = ["area", "abstract"],
            },
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(int number, string area, string[] topics, string main, string? text = null) =>
        new($"d{number}", new Dictionary<string, string> { ["area"] = area, ["abstract"] = text ?? $"paper {number}", ["main"] = main },
            DateTimeOffset.UnixEpoch.AddMinutes(number))
        {
            Sets = new Dictionary<string, IReadOnlyList<string>> { ["topics"] = topics },
        };

    private static readonly SettledDocument[] History =
    [
        Doc(1, "ai", ["nlp", "vision"], "nlp"),
        Doc(2, "ai", ["nlp"], "nlp"),
        Doc(3, "ai", ["nlp", "speech"], "nlp"),
        Doc(4, "ai", ["vision", "robotics"], "vision"),
    ];

    private static readonly Dictionary<string, string> Ai = new() { ["area"] = "ai", ["abstract"] = "new paper" };

    private static Dictionary<string, IReadOnlyList<string>> Topics(params string[] topics) => new() { ["topics"] = topics };

    private static async Task<FieldSuggestion> MainAsync(FormResolver resolver, FormDefinition form, IReadOnlyDictionary<string, IReadOnlyList<string>> sets) =>
        (await resolver.SuggestAsync(form, "d9", Ai, sets, Ct)).Single(s => s.Field == "main");

    [Fact]
    public void A_form_rejects_candidates_taken_from_an_unknown_field_or_the_field_itself()
    {
        FieldDefinition[] Fields(string from) =>
        [
            new FieldDefinition("topics", FieldRole.Judged) { Multiple = true },
            new FieldDefinition("main", FieldRole.Judged) { CandidatesFrom = from },
        ];

        var unknown = () => new FormDefinition("paper", Fields("subjects"), PromptLanguage.English);
        var itself = () => new FormDefinition("paper", Fields("main"), PromptLanguage.English);

        unknown.Should().Throw<ArgumentException>().WithMessage("*'main' takes its candidates from 'subjects'*");
        itself.Should().Throw<ArgumentException>().WithMessage("*'main' takes its candidates from 'main'*");
    }

    [Fact]
    public void The_domain_narrows_to_the_other_fields_values_once_it_has_any()
    {
        var main = new FieldDefinition("main", FieldRole.Judged) { CandidatesFrom = "topics" };
        var closed = main with { Candidates = ["nlp", "vision"] };
        var category = new FieldDefinition("subcategory", FieldRole.Judged) { CandidatesFrom = "category" };
        var none = new Dictionary<string, string>();

        main.Admits("speech", none).Should().BeTrue();
        main.Admits("speech", none, Topics()).Should().BeTrue();
        main.Admits("speech", none, Topics("nlp", "speech")).Should().BeTrue();
        main.Admits("vision", none, Topics("nlp", "speech")).Should().BeFalse();
        main.Admits("Speech", none, Topics("nlp", "speech")).Should().BeFalse();
        closed.Admits("speech", none, Topics("nlp", "speech")).Should().BeFalse();

        category.Admits("housing", new Dictionary<string, string> { ["category"] = "housing" }).Should().BeTrue();
        category.Admits("debt", new Dictionary<string, string> { ["category"] = "housing" }).Should().BeFalse();
        category.Admits("debt", new Dictionary<string, string> { ["category"] = "" }).Should().BeTrue();
    }

    [Fact]
    public async Task The_key_layer_offers_only_the_documents_topics_and_trusts_on_the_best_of_them()
    {
        var form = Paper();
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(form, History, Ct);

        var open = await MainAsync(resolver, form, Topics());
        var narrowed = await MainAsync(resolver, form, Topics("vision", "robotics"));

        open.Candidates[0].Value.Should().Be("nlp");
        open.Answered.Should().BeTrue();
        // Under area=ai "nlp" scores 0.6 and "vision" 0.2: "vision" leads the narrowed domain without borrowing nlp's trust.
        narrowed.Candidates.Select(c => c.Value).Should().Equal("vision");
        narrowed.Answered.Should().BeFalse();
    }

    [Fact]
    public async Task A_value_settled_outside_its_documents_domain_is_remembered_and_offered_where_it_fits()
    {
        var form = Paper(mainKeyThreshold: null);
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(form, [.. History, Doc(5, "bio", ["genomics"], "proteins")], Ct);

        var bio = new Dictionary<string, string> { ["area"] = "bio", ["abstract"] = "new paper" };
        var outside = (await resolver.SuggestAsync(form, "d9", bio, Topics("genomics"), Ct)).Single(s => s.Field == "main");
        var inside = (await resolver.SuggestAsync(form, "d9", bio, Topics("proteins", "genomics"), Ct)).Single(s => s.Field == "main");

        outside.Candidates.Select(c => c.Value).Should().NotContain("proteins");
        inside.Candidates[0].Value.Should().Be("proteins");
    }

    [Fact]
    public async Task A_model_value_outside_the_documents_domain_is_dropped()
    {
        var form = Paper();
        var resolver = new FormResolver(new FieldMemory(), model: new FixedModel("nlp", "vision"));

        var suggestion = await MainAsync(resolver, form, Topics("vision"));

        suggestion.Candidates.Select(c => c.Value).Should().Equal("vision");
    }

    [Fact]
    public async Task A_similar_document_whose_value_lies_outside_the_documents_domain_offers_nothing()
    {
        var form = Paper(mainKeyThreshold: null, mainMemoryThreshold: 0.3);
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory(), similarDocumentCount: 5);
        await resolver.RebuildAsync(form,
            [
                Doc(1, "ai", ["nlp", "vision"], "nlp", "parsing sentences with a neural network"),
                Doc(2, "ai", ["vision"], "vision", "segmenting images of cells"),
            ], Ct);
        var asked = new Dictionary<string, string> { ["area"] = "ai", ["abstract"] = "parsing sentences with a neural network again" };

        var open = (await resolver.SuggestAsync(form, "d9", asked, Topics(), Ct)).Single(s => s.Field == "main");
        var narrowed = (await resolver.SuggestAsync(form, "d9", asked, Topics("vision"), Ct)).Single(s => s.Field == "main");

        open.Candidates.Should().Contain(c => c.Source == FieldSource.SimilarDocument && c.Value == "nlp" && c.Trusted);
        narrowed.Candidates.Should().NotContain(c => c.Source == FieldSource.SimilarDocument);
        narrowed.SimilarDocuments.Should().NotContain(m => m.Source == "d1");
    }

    [Fact]
    public void A_key_threshold_replay_narrows_as_suggestions_do()
    {
        // The main topic is the area's usual one only when the document has it: a replay that ignored the domain would
        // count "nlp" against every document without it.
        var documents = Enumerable.Range(1, 40)
            .Select(i => i % 4 == 0 ? Doc(i, "ai", ["vision", "robotics"], "vision") : Doc(i, "ai", ["nlp", "vision"], "nlp"))
            .ToList();
        var form = Paper();
        var open = new FormDefinition("paper", [.. form.Fields.Select(f => f with { CandidatesFrom = null })], PromptLanguage.English);

        var narrowed = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), form, "main", documents, 0.95, minimumAnswered: 5);
        var unnarrowed = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), open, "main", documents, 0.95, minimumAnswered: 5);

        narrowed.Chosen.Should().NotBeNull();
        narrowed.Chosen!.Precision.Should().Be(1);
        unnarrowed.Chosen.Should().BeNull();
    }

    private sealed class FixedModel(params string[] values) : IFieldModel
    {
        public Task<FieldModelResult> SuggestAsync(FormDefinition form, string field, IReadOnlyList<KeyValuePair<string, string>> evidence, string traceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FieldModelResult([.. values.Select(v => new FieldCandidate(v, 0.5, FieldSource.Model, null))], 0.5, 0));
    }
}

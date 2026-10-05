using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

/// <summary>
/// While a person types into a judged field, the typed text narrows its candidates to the values that begin with it, and
/// only the key layer answers, held to <see cref="FieldDefinition.TypedKeyThresholds"/> for that many characters.
/// </summary>
public sealed class TypedSuggestionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FormDefinition Ticket(double? keyThreshold = 0.9, IReadOnlyList<double?>? typedThresholds = null, double? memoryThreshold = null) => new(
        "ticket",
        [
            new FieldDefinition("channel", FieldRole.Observed),
            new FieldDefinition("summary", FieldRole.Observed),
            new FieldDefinition("team", FieldRole.Judged)
            {
                KeyThreshold = keyThreshold,
                TypedKeyThresholds = typedThresholds,
                MemoryThreshold = memoryThreshold,
                DependsOn = ["channel"],
            },
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(int number, string channel, string team, string? summary = null) =>
        new($"d{number}", new Dictionary<string, string> { ["channel"] = channel, ["summary"] = summary ?? $"ticket {number}", ["team"] = team },
            DateTimeOffset.UnixEpoch.AddMinutes(number));

    // Under channel=phone: "network" three times, "billing" twice, "backend" once.
    private static readonly SettledDocument[] History =
    [
        Doc(1, "phone", "network"),
        Doc(2, "phone", "network"),
        Doc(3, "phone", "billing"),
        Doc(4, "phone", "network"),
        Doc(5, "phone", "billing"),
        Doc(6, "phone", "backend"),
    ];

    private static readonly Dictionary<string, string> Phone = new() { ["channel"] = "phone", ["summary"] = "new ticket" };
    private static readonly Dictionary<string, IReadOnlyList<string>> NoSets = [];

    private static Dictionary<string, string> Typing(string text) => new() { ["team"] = text };

    [Fact]
    public void A_typed_threshold_follows_the_key_threshold_by_characters_typed()
    {
        var field = new FieldDefinition("team", FieldRole.Judged) { KeyThreshold = 0.9, TypedKeyThresholds = [0.3, null] };

        field.KeyThresholdFor(0).Should().Be(0.9);
        field.KeyThresholdFor(1).Should().Be(0.3);
        field.KeyThresholdFor(2).Should().BeNull();
        field.KeyThresholdFor(3).Should().BeNull();
        (field with { TypedKeyThresholds = null }).KeyThresholdFor(1).Should().BeNull();
    }

    [Fact]
    public void A_field_that_takes_several_values_takes_no_typed_thresholds()
    {
        var form = () => new FormDefinition("paper",
            [new FieldDefinition("topics", FieldRole.Judged) { Multiple = true, TypedKeyThresholds = [0.5] }], PromptLanguage.English);

        form.Should().Throw<ArgumentException>().WithMessage("*'topics'*no typed key thresholds*");
    }

    [Fact]
    public async Task Typed_text_narrows_the_candidates_and_is_held_to_its_own_threshold()
    {
        var form = Ticket(typedThresholds: [0.25]);
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(form, History, Ct);

        var untyped = (await resolver.SuggestAsync(form, "d9", Phone, Ct)).Single();
        var typed = (await resolver.SuggestAsync(form, "d9", Phone, NoSets, Typing("B"), Ct)).Single();

        untyped.Candidates[0].Value.Should().Be("network");
        untyped.Answered.Should().BeFalse(); // network scores 3/7 under channel=phone, below 0.9
        typed.Candidates.Select(c => c.Value).Should().Equal("billing", "backend");
        typed.Candidates[0].Score.Should().BeApproximately(2.0 / 7, 0.02);
        typed.Answered.Should().BeTrue(); // 2/7 reaches the threshold for one typed character
    }

    [Fact]
    public async Task A_value_trusted_with_fewer_characters_stays_trusted_while_the_text_leads_to_it()
    {
        // Only one typed character has a threshold. "billing", trusted at "b", is still the best value at "bi", "bil" and
        // the whole value, with the same score: typing on does not withdraw it.
        var form = Ticket(typedThresholds: [0.25]);
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(form, History, Ct);

        foreach (var text in new[] { "b", "bi", "BIL", "billing" })
        {
            var suggestion = (await resolver.SuggestAsync(form, "d9", Phone, NoSets, Typing(text), Ct)).Single();
            suggestion.Candidates[0].Value.Should().Be("billing");
            suggestion.Answered.Should().BeTrue(because: $"\"{text}\" still leads to the value trusted at \"b\"");
        }
    }

    [Fact]
    public async Task Past_the_typed_thresholds_a_value_not_trusted_before_is_a_guess()
    {
        // At "ba" the best value is "backend", which was never the trusted value with fewer characters typed ("b" trusted
        // "billing"), and two characters have no threshold.
        var form = Ticket(typedThresholds: [0.25]);
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(form, History, Ct);

        var suggestion = (await resolver.SuggestAsync(form, "d9", Phone, NoSets, Typing("ba"), Ct)).Single();

        suggestion.Candidates.Select(c => c.Value).Should().Equal("backend");
        suggestion.Answered.Should().BeFalse();
    }

    [Fact]
    public async Task A_value_trusted_before_typing_stays_trusted_while_it_is_typed()
    {
        var form = Ticket(keyThreshold: 0.35); // network scores about 0.40 under channel=phone; no typed thresholds at all
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(form, History, Ct);

        var untyped = (await resolver.SuggestAsync(form, "d9", Phone, Ct)).Single();
        var typing = (await resolver.SuggestAsync(form, "d9", Phone, NoSets, Typing("netw"), Ct)).Single();
        var elsewhere = (await resolver.SuggestAsync(form, "d9", Phone, NoSets, Typing("b"), Ct)).Single();

        untyped.Answered.Should().BeTrue();
        typing.Candidates[0].Value.Should().Be("network");
        typing.Answered.Should().BeTrue();
        elsewhere.Answered.Should().BeFalse(); // "billing" was never trusted, and one character has no threshold
    }

    [Fact]
    public async Task While_typing_no_model_is_asked_and_a_similar_document_is_only_a_guess()
    {
        var form = Ticket(keyThreshold: null, memoryThreshold: 0.1);
        var model = new CountingModel("billing");
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory(), model, similarDocumentCount: 5);
        await resolver.RebuildAsync(form, [Doc(1, "web", "billing", "card payment failed twice")], Ct);
        var asked = new Dictionary<string, string> { ["channel"] = "email", ["summary"] = "card payment failed twice again" };

        var untyped = (await resolver.SuggestAsync(form, "d9", asked, Ct)).Single();
        var typed = (await resolver.SuggestAsync(form, "d9", asked, NoSets, Typing("bil"), Ct)).Single();
        var mismatched = (await resolver.SuggestAsync(form, "d9", asked, NoSets, Typing("n"), Ct)).Single();

        untyped.Candidates.Should().Contain(c => c.Source == FieldSource.SimilarDocument && c.Trusted);
        typed.Candidates[0].Value.Should().Be("billing");
        typed.Candidates.Should().OnlyContain(c => !c.Trusted);
        typed.SimilarDocuments.Should().Contain(m => m.Source == "d1");
        mismatched.Candidates.Should().BeEmpty();
        mismatched.SimilarDocuments.Should().BeEmpty();
        model.Calls.Should().Be(0); // the untyped suggestion had a similar document trusted, so no model either
    }

    [Fact]
    public async Task Text_is_typed_only_into_an_open_judged_field()
    {
        var form = Ticket();
        var resolver = new FormResolver(new FieldMemory());
        var withValue = new Dictionary<string, string>(Phone) { ["team"] = "network" };

        var observed = () => resolver.SuggestAsync(form, "d9", Phone, NoSets, new Dictionary<string, string> { ["channel"] = "ph" }, Ct);
        var settled = () => resolver.SuggestAsync(form, "d9", withValue, NoSets, Typing("n"), Ct);

        await observed.Should().ThrowAsync<ArgumentException>().WithMessage("*'channel', which is not a judged field*");
        await settled.Should().ThrowAsync<ArgumentException>().WithMessage("*'team', which already has a value*");
    }

    [Fact]
    public async Task A_session_suggests_with_typed_text_and_keeps_the_field_open()
    {
        var form = Ticket(typedThresholds: [0.25]);
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(form, History, Ct);
        var session = resolver.Open(form, "d9");
        await session.ObserveAsync("channel", "phone", Ct);

        var typed = (await session.SuggestAsync(Typing("b"), Ct)).Single();
        var after = (await session.SuggestAsync(Ct)).Single();

        typed.Candidates[0].Value.Should().Be("billing");
        typed.Answered.Should().BeTrue();
        after.Candidates[0].Value.Should().Be("network");
        session.Snapshot().Values.Should().NotContainKey("team");
    }

    [Fact]
    public void Typed_thresholds_are_chosen_on_the_documents_typed_that_far()
    {
        // Under channel=phone "network" is settled every time but one in four. With the key threshold answering
        // "network" rightly, only the documents settled otherwise are typed into — and those must decide the threshold
        // for one character, not every document.
        var documents = Enumerable.Range(1, 80)
            .Select(i => Doc(i, "phone", i % 4 == 0 ? (i % 8 == 0 ? "backend" : "billing") : "network"))
            .ToList();
        var form = Ticket(keyThreshold: 0.5);

        var chosen = ThresholdSelection.SelectTypedKeyThresholds(new FieldMemory(), form, "team", documents, 0.8, minimumAnswered: 5, longest: 2);
        var everything = ThresholdSelection.SelectTypedKeyThresholds(new FieldMemory(), Ticket(keyThreshold: null), "team", documents, 0.8, minimumAnswered: 5, longest: 2);

        chosen.ByLength.Should().HaveCount(2);
        chosen.Thresholds.Should().HaveCount(2);
        chosen.ByLength[0].Lookups.Should().BeLessThan(everything.ByLength[0].Lookups);
        chosen.ByLength[0].Lookups.Should().Be(documents.Count(d => d.Values["team"] != "network") - 0); // "network" answered rightly at no typing
        everything.ByLength[0].Lookups.Should().Be(documents.Count - 1);
    }

    [Fact]
    public void Typed_thresholds_leave_out_the_documents_a_wrong_claim_still_leads_to()
    {
        // "network" is trusted before typing. On a "netops" document it is wrong, yet "n", "ne" and "net" still lead to
        // it: the person sees that same claim, not a new answer, so those lengths are not chosen on that document. From
        // "neto" on the typing has left it behind and the document counts again. "billing" leaves it at "b".
        var documents = Enumerable.Range(1, 80)
            .Select(i => Doc(i, "phone", i % 4 == 0 ? (i % 8 == 0 ? "netops" : "billing") : "network"))
            .ToList();
        var form = Ticket(keyThreshold: 0.5);

        var chosen = ThresholdSelection.SelectTypedKeyThresholds(new FieldMemory(), form, "team", documents, 0.8, minimumAnswered: 5, longest: 4);

        var billing = documents.Count(d => d.Values["team"] == "billing");
        var netops = documents.Count(d => d.Values["team"] == "netops");
        chosen.ByLength[0].Lookups.Should().Be(billing);
        chosen.ByLength[2].Lookups.Should().BeLessThanOrEqualTo(billing);
        chosen.ByLength[3].Lookups.Should().BeGreaterThanOrEqualTo(netops);
    }

    [Fact]
    public void Typed_thresholds_are_not_chosen_for_a_field_that_takes_several_values()
    {
        var form = new FormDefinition("paper", [new FieldDefinition("topics", FieldRole.Judged) { Multiple = true }], PromptLanguage.English);

        var select = () => ThresholdSelection.SelectTypedKeyThresholds(new FieldMemory(), form, "topics", [], 0.8, 5);

        select.Should().Throw<ArgumentException>().WithMessage("*'topics' takes several values*");
    }

    private sealed class CountingModel(params string[] values) : IFieldModel
    {
        public int Calls { get; private set; }

        public Task<FieldModelResult> SuggestAsync(FormDefinition form, string field, IReadOnlyList<KeyValuePair<string, string>> evidence, string traceId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new FieldModelResult([.. values.Select(v => new FieldCandidate(v, 0.9, FieldSource.Model, null))], 0.9, 0));
        }
    }
}

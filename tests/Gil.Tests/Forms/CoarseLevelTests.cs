using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

/// <summary>A judged field's coarse level (<see cref="FieldDefinition.Coarse"/>).</summary>
public sealed class CoarseLevelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FormDefinition Report(CoarseLevel? coarse, double? memoryThreshold = null, double? keyThreshold = null) => new(
        "report",
        [
            new FieldDefinition("part", FieldRole.Observed),
            new FieldDefinition("note", FieldRole.Observed),
            new FieldDefinition("code", FieldRole.Judged) { Coarse = coarse, MemoryThreshold = memoryThreshold, KeyThreshold = keyThreshold },
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(int number, string part, string note, string code) =>
        new($"d{number}", new Dictionary<string, string> { ["part"] = part, ["note"] = note, ["code"] = code },
            DateTimeOffset.UnixEpoch.AddMinutes(number));

    // Flight controls (chapter 27) under three codes, so no single code is backed well; landing gear (32) apart.
    private static readonly SettledDocument[] History =
    [
        Doc(1, "aileron", "aileron stiff on approach, lubricated quadrant", "2710"),
        Doc(2, "aileron", "aileron cable tension low on approach", "2711"),
        Doc(3, "aileron", "aileron stiff in roll, lubricated hinge", "2712"),
        Doc(4, "elevator", "elevator stiff on approach, lubricated hinge", "2730"),
        Doc(5, "gear", "main gear did not retract, replaced actuator", "3230"),
        Doc(6, "gear", "nose gear shimmy on landing, replaced damper", "3250"),
    ];

    private static readonly Dictionary<string, string> Asked = new() { ["part"] = "aileron", ["note"] = "aileron stiff on approach again" };

    private static async Task<FieldSuggestion> SuggestAsync(FormDefinition form, Dictionary<string, string>? asked = null, string? typed = null)
    {
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory());
        await resolver.RebuildAsync(form, History, Ct);
        return typed is null
            ? (await resolver.SuggestAsync(form, "d9", asked ?? Asked, Ct)).Single()
            : await resolver.SuggestAsync(form, "d9", "code", asked ?? Asked, typed: typed, cancellationToken: Ct);
    }

    [Fact]
    public async Task The_class_the_keys_back_is_offered_when_no_layer_answers_the_value()
    {
        // The keys split "aileron" three ways, so no code is trusted; together they back chapter 27.
        var suggestion = await SuggestAsync(Report(new CoarseLevel(2) { KeyThreshold = 0.5 }));

        suggestion.Answered.Should().BeFalse();
        suggestion.Coarse.Should().NotBeNull();
        suggestion.Coarse!.Value.Should().Be("27");
        suggestion.Coarse.Source.Should().Be(FieldSource.SettledFieldMemory);
        suggestion.Coarse.Trusted.Should().BeTrue();
        suggestion.Coarse.Evidence.Should().Be("part: aileron");
        suggestion.Candidates.Should().OnlyContain(c => c.Value.Length == 4); // candidates stay settled values
    }

    [Fact]
    public async Task Where_the_keys_do_not_reach_their_threshold_the_vote_of_similar_documents_can()
    {
        var suggestion = await SuggestAsync(Report(new CoarseLevel(2) { KeyThreshold = 100, MemoryThreshold = 0 }));

        suggestion.Coarse!.Value.Should().Be("27");
        suggestion.Coarse.Source.Should().Be(FieldSource.SimilarDocument);
        suggestion.Coarse.Trusted.Should().BeTrue();
        suggestion.Coarse.Evidence.Should().StartWith("d");
    }

    [Fact]
    public async Task Without_a_threshold_met_the_class_is_a_guess()
    {
        var suggestion = await SuggestAsync(Report(new CoarseLevel(2)));

        suggestion.Coarse!.Value.Should().Be("27");
        suggestion.Coarse.Trusted.Should().BeFalse();
    }

    [Fact]
    public async Task A_vote_among_documents_less_like_the_draft_than_the_floor_is_a_guess()
    {
        var suggestion = await SuggestAsync(Report(new CoarseLevel(2) { MemoryThreshold = 0, MemorySimilarityFloor = 2 }), new() { ["note"] = "aileron stiff on approach again" });

        suggestion.Coarse!.Source.Should().Be(FieldSource.SimilarDocument);
        suggestion.Coarse.Trusted.Should().BeFalse();
    }

    [Fact]
    public async Task No_class_is_offered_when_a_layer_answers_the_value_or_while_typing()
    {
        var answered = await SuggestAsync(Report(new CoarseLevel(2) { KeyThreshold = 0 }, keyThreshold: 0));
        var typing = await SuggestAsync(Report(new CoarseLevel(2) { KeyThreshold = 0 }), typed: "2");
        var none = await SuggestAsync(Report(coarse: null));

        answered.Answered.Should().BeTrue();
        answered.Coarse.Should().BeNull();
        typing.Coarse.Should().BeNull();
        none.Coarse.Should().BeNull();
    }

    [Fact]
    public async Task A_replay_chooses_the_level_s_thresholds_on_the_scores_suggestions_carry()
    {
        // Each document asked about with the ones before it remembered, as the replay asks: the threshold it chooses for
        // the keys' prefix is a score some such suggestion carried for the level.
        var form = Report(new CoarseLevel(2));
        var keyScores = new List<double>();
        for (var i = 1; i < History.Length; i++)
        {
            var resolver = new FormResolver(new FieldMemory(), new LexicalMemory());
            await resolver.RebuildAsync(form, History.Take(i), Ct);
            var asked = History[i].Values.Where(v => v.Key != "code").ToDictionary(v => v.Key, v => v.Value);
            if ((await resolver.SuggestAsync(form, History[i].DocumentId, asked, Ct)).Single().Coarse is { Source: FieldSource.SettledFieldMemory } keys)
            {
                keyScores.Add(keys.Score);
            }
        }

        var layers = await ThresholdSelection.SelectCoarseAsync(new FieldMemory(), new LexicalMemory(), form, "code", History, 0.01, minimumAnswered: 1, Ct);

        layers.Key.Chosen.Should().NotBeNull();
        keyScores.Should().Contain(layers.Key.Chosen!.Threshold);
        layers.Key.Candidates.Should().Be(keyScores.Count);
    }

    [Fact]
    public async Task A_replay_leaves_the_lookups_the_value_layers_answered_out()
    {
        // A key threshold of 0 answers every lookup the keys reach; only the first, before any key, is left.
        var answering = Report(new CoarseLevel(2), keyThreshold: 0);
        var open = Report(new CoarseLevel(2));

        var none = await ThresholdSelection.SelectCoarseAsync(new FieldMemory(), new LexicalMemory(), answering, "code", History, 0.5, 1, Ct);
        var all = await ThresholdSelection.SelectCoarseAsync(new FieldMemory(), new LexicalMemory(), open, "code", History, 0.5, 1, Ct);

        none.Key.Lookups.Should().BeLessThan(all.Key.Lookups);
        all.Key.Lookups.Should().Be(History.Length - 1);
        await FluentActions.Awaiting(() => ThresholdSelection.SelectCoarseAsync(new FieldMemory(), new LexicalMemory(), Report(coarse: null), "code", History, 0.5, 1, Ct))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*coarse level*");
    }

    [Fact]
    public void A_class_is_the_value_s_prefix_and_needs_one_character_and_a_single_valued_judged_field()
    {
        new CoarseLevel(2).Of("2710").Should().Be("27");
        new CoarseLevel(5).Of("2710").Should().Be("2710");

        var empty = () => Report(new CoarseLevel(0));
        var set = () => new FormDefinition("tags", [new FieldDefinition("t", FieldRole.Judged) { Multiple = true, Coarse = new CoarseLevel(1) }], PromptLanguage.English);
        var observed = () => new FormDefinition("o", [new FieldDefinition("o", FieldRole.Observed) { Coarse = new CoarseLevel(1) }, new FieldDefinition("j", FieldRole.Judged)], PromptLanguage.English);

        empty.Should().Throw<ArgumentException>().WithMessage("*at least one*");
        set.Should().Throw<ArgumentException>().WithMessage("*single value*");
        observed.Should().Throw<ArgumentException>().WithMessage("*single value*");
    }
}

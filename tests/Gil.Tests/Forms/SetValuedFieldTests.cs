using System.Globalization;
using AwesomeAssertions;
using Gil.Forms;

namespace Gil.Tests.Forms;

public sealed class SetValuedFieldTests
{
    private static readonly FormDefinition Paper = new(
        "paper",
        [
            new FieldDefinition("area", FieldRole.Observed),
            new FieldDefinition("topics", FieldRole.Judged) { Multiple = true },
            new FieldDefinition("lead", FieldRole.Judged),
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(string id, string? area, string[] topics, string? lead = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (area is not null)
        {
            values["area"] = area;
        }

        if (lead is not null)
        {
            values["lead"] = lead;
        }

        return new SettledDocument(id, values, At(id)) { Sets = new Dictionary<string, IReadOnlyList<string>> { ["topics"] = topics } };
    }

    /// <summary>Documents settle in the order of their numbers, a minute apart.</summary>
    private static DateTimeOffset At(string id) => DateTimeOffset.UnixEpoch.AddMinutes(int.Parse(id[1..], CultureInfo.InvariantCulture));

    private static Dictionary<string, string> Known(string area) => new(StringComparer.Ordinal) { ["area"] = area };

    private static Dictionary<string, IReadOnlyList<string>> Chosen(params string[] topics) => new(StringComparer.Ordinal) { ["topics"] = topics };

    private static FieldMemory ThreePapers()
    {
        var memory = new FieldMemory(recencyDecay: 1);
        memory.Put(Paper, Doc("d1", "ai", ["nlp", "vision"]));
        memory.Put(Paper, Doc("d2", "ai", ["nlp"]));
        memory.Put(Paper, Doc("d3", "bio", ["genomics", "nlp"]));
        return memory;
    }

    [Fact]
    public void Only_a_judged_field_takes_several_values()
    {
        var act = () => new FormDefinition(
            "paper",
            [new FieldDefinition("tags", FieldRole.Observed) { Multiple = true }, new FieldDefinition("lead", FieldRole.Judged)],
            PromptLanguage.English);

        act.Should().Throw<ArgumentException>().WithMessage("*'tags' is observed*");
    }

    [Fact]
    public async Task A_set_takes_no_memory_threshold_and_has_no_similarity_threshold_to_choose()
    {
        var act = () => new FormDefinition(
            "paper",
            [new FieldDefinition("tags", FieldRole.Judged) { Multiple = true, MemoryThreshold = 0.5 }],
            PromptLanguage.English);
        act.Should().Throw<ArgumentException>().WithMessage("*no memory threshold*");

        var select = () => ThresholdSelection.SelectAsync(new Gil.Memory.LexicalMemory(), Paper, "topics", [], 0.9, 5, TestContext.Current.CancellationToken);
        await select.Should().ThrowAsync<ArgumentException>().WithMessage("*SelectKeyThreshold*");
    }

    [Fact]
    public void A_field_is_settled_where_its_kind_belongs()
    {
        var memory = new FieldMemory();
        var inValues = new SettledDocument("d1", new Dictionary<string, string> { ["topics"] = "nlp" }, At("d1"));
        var inSets = new SettledDocument("d2", new Dictionary<string, string>(), At("d2"))
        {
            Sets = new Dictionary<string, IReadOnlyList<string>> { ["lead"] = ["kim"] },
        };

        ((Action)(() => memory.Put(Paper, inValues))).Should().Throw<ArgumentException>().WithMessage("*'topics' takes several values*");
        ((Action)(() => memory.Put(Paper, inSets))).Should().Throw<ArgumentException>().WithMessage("*'lead' takes one value*");
    }

    [Fact]
    public void Each_value_of_a_set_is_settled_under_the_document_keys()
    {
        // Under area = ai: nlp twice and vision once of three settled values — strengths 2 / 4 and 1 / 4. Genomics was never
        // settled under it and follows as the field's other values do, by its share overall (1 of 5).
        ThreePapers().Rank(Paper, "topics", Known("ai"), 3)
            .Select(c => (c.Value, c.Score, c.Evidence)).Should().Equal(("nlp", 0.5, "area: ai"), ("vision", 0.25, "area: ai"), ("genomics", 0.2, null));
    }

    [Fact]
    public void Values_already_chosen_are_evidence_for_the_rest_and_are_not_offered_again()
    {
        // With nlp chosen it is a key too: settled alongside nlp were vision (d1) and genomics (d3), a third each. Vision adds
        // its quarter under area = ai and rests most on nlp.
        var ranked = ThreePapers().Rank(Paper, "topics", Known("ai"), 3, knownSets: Chosen("nlp"));

        ranked.Select(c => c.Value).Should().Equal("vision", "genomics");
        ranked[0].Score.Should().BeApproximately(0.25 + (1.0 / 3), 1e-12);
        ranked[0].Evidence.Should().Be("topics: nlp");
        ranked[1].Score.Should().BeApproximately(1.0 / 3, 1e-12);
    }

    [Fact]
    public void Each_offered_value_of_a_set_is_trusted_on_its_own_score()
    {
        var form = new FormDefinition(
            "paper",
            [new FieldDefinition("area", FieldRole.Observed), new FieldDefinition("topics", FieldRole.Judged) { Multiple = true, KeyThreshold = 0.3 }],
            PromptLanguage.English);
        var memory = new FieldMemory(recencyDecay: 1);
        memory.Put(form, Doc("d1", "ai", ["nlp", "vision"]));
        memory.Put(form, Doc("d2", "ai", ["nlp", "vision"]));
        memory.Put(form, Doc("d3", "ai", ["nlp", "robotics"]));

        // Under area = ai: nlp 3, vision 2, robotics 1 of 6 — 3/7, 2/7 and 1/7. Two reach 0.3; a single-valued field would
        // have trusted every keyed value on the first one's score.
        memory.Rank(form, "topics", Known("ai"), 3)
            .Select(c => (c.Value, c.Trusted)).Should().Equal(("nlp", true), ("vision", false), ("robotics", false));
        memory.Rank(form, "topics", Known("ai"), 3, knownSets: Chosen("nlp"))
            .Select(c => (c.Value, c.Trusted)).Should().Equal(("vision", true), ("robotics", true));
    }

    [Fact]
    public void The_values_of_one_document_weigh_alike_whatever_order_they_are_listed_in()
    {
        foreach (var listed in new[] { new[] { "b", "a" }, ["a", "b"] })
        {
            var memory = new FieldMemory(recencyDecay: 0.5);
            memory.Put(Paper, Doc("d1", "ai", listed));
            memory.Put(Paper, Doc("d2", "ai", ["c"]));

            // One settlement after d1 halves both of its values alike: a = b = 0.5, c = 1, total 2 — strengths over 3.
            memory.Rank(Paper, "topics", Known("ai"), 3)
                .Select(c => (c.Value, Math.Round(c.Score, 12))).Should().Equal(("c", Math.Round(1.0 / 3, 12)), ("a", Math.Round(1.0 / 6, 12)), ("b", Math.Round(1.0 / 6, 12)));
        }
    }

    [Fact]
    public void Each_value_of_a_set_is_a_key_of_its_own_for_another_field()
    {
        var memory = new FieldMemory(recencyDecay: 1);
        memory.Put(Paper, Doc("d1", null, ["nlp", "vision"], lead: "kim"));
        memory.Put(Paper, Doc("d2", null, ["nlp"], lead: "lee"));

        memory.Rank(Paper, "lead", new Dictionary<string, string>(), 2, knownSets: Chosen("vision"))
            .Select(c => (c.Value, c.Score, c.Evidence)).Should().Equal(("kim", 0.5, "topics: vision"), ("lee", 0.5, null));
        memory.Rank(Paper, "lead", new Dictionary<string, string>(), 2, knownSets: Chosen("nlp", "vision"))
            .Select(c => c.Value).Should().Equal("kim", "lee"); // nlp backs both alike, vision only kim
    }

    [Fact]
    public void A_document_settled_again_replaces_every_value_it_settled_and_is_left_out_whole()
    {
        var memory = ThreePapers();
        memory.Put(Paper, Doc("d1", "ai", ["vision"]));

        // Under area = ai now: vision (d1) and nlp (d2), a third each, the later first.
        memory.Rank(Paper, "topics", Known("ai"), 2).Select(c => c.Value).Should().Equal("nlp", "vision");

        // Leaving d1 out leaves out all it settled: only d2's nlp under the key, and overall nlp and genomics from d2 and d3.
        memory.Rank(Paper, "topics", Known("ai"), 3, excluding: "d1")
            .Select(c => (c.Value, c.Score)).Should().Equal(("nlp", 0.5), ("genomics", 1.0 / 3));
    }
}

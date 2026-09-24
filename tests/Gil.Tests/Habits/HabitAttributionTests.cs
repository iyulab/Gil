using AwesomeAssertions;
using Gil.Habits;
using Gil.Ontology;

namespace Gil.Tests.Habits;

public sealed class HabitAttributionTests
{
    private static readonly Node Tree = OntologyYaml.Parse("""
        id: root
        children:
          - id: bank
            label: bank
            description: banking
            options:
              - {id: o-balance, kind: answer, label: balance, description: balance, text: balance}
              - {id: o-transfer, kind: answer, label: transfer, description: transfer, text: transfer}
          - id: travel
            label: travel
            description: trips
            options:
              - {id: o-visa, kind: answer, label: visa, description: visa, text: visa}
          - {id: work, label: work, description: office}
        """).Root;

    private static readonly PathStep[] HabitPath = [Step("root", "bank"), Step("bank", "o-balance")];

    [Fact]
    public void A_correct_habit_reinforces_every_judgment_on_the_path() =>
        Attribute(HabitPath, "habit/answer", "balance", correct: true).Should().BeEquivalentTo(new Attribution(["bank", "o-balance"]));

    [Fact]
    public void A_wrong_leaf_pick_blames_the_leaf_and_credits_the_category() =>
        Attribute(HabitPath, "habit/answer", "balance", correct: false, correction: "transfer")
            .Should().BeEquivalentTo(new Attribution(["bank"], Penalize: "o-balance"));

    [Fact]
    public void A_wrong_category_blames_only_the_category_not_the_option_below_it() =>
        Attribute(HabitPath, "habit/answer", "balance", correct: false, correction: "visa")
            .Should().BeEquivalentTo(new Attribution([], Penalize: "bank"));

    [Theory]
    [InlineData("not in the tree")]
    [InlineData(null)]
    public void A_correction_that_cannot_be_located_blames_only_the_habit_that_answered(string? correction) =>
        Attribute(HabitPath, "habit/answer", "balance", correct: false, correction: correction)
            .Should().BeEquivalentTo(new Attribution([], Penalize: "o-balance"));

    [Fact]
    public void A_correction_is_located_ignoring_surrounding_whitespace() =>
        Attribute(HabitPath, "habit/answer", "balance", correct: false, correction: " transfer\n")
            .Should().BeEquivalentTo(new Attribution(["bank"], Penalize: "o-balance"));

    [Fact]
    public void An_exit_whose_fallback_matches_an_existing_habit_marks_it_missed_not_penalized() =>
        Attribute([Step("root", "bank"), Step("bank", null, "exit")], "partial", "transfer", correct: true)
            .Should().BeEquivalentTo(new Attribution([], Missed: "o-transfer"));

    [Fact]
    public void A_fallback_without_any_matching_habit_changes_nothing() =>
        Attribute([Step("root", "work"), Step("work", null, "skip")], "partial", "schedule", correct: true)
            .Should().BeEquivalentTo(Attribution.None);

    [Fact]
    public void A_wrong_fallback_changes_nothing() =>
        Attribute([Step("root", null, "exit")], "fallback", "visa", correct: false, correction: "balance")
            .Should().BeEquivalentTo(Attribution.None);

    [Fact]
    public void A_deferral_to_the_fallback_is_not_a_judgment_to_credit() =>
        Attribute([Step("root", "bank"), Step("bank", "shadow-1", "defer")], "partial", "balance", correct: true)
            .Should().BeEquivalentTo(new Attribution([], Missed: "o-balance"));

    private static Attribution Attribute(PathStep[] path, string mode, string? output, bool correct, string? correction = null) =>
        HabitAttribution.Attribute(Tree, path, mode, output, correct, correction);

    private static PathStep Step(string node, string? chosen, string outcome = "accept") =>
        new(node, 1, chosen, 0.9, outcome, new Dictionary<string, double>(), 0);
}

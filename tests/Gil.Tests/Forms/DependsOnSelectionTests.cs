using AwesomeAssertions;
using Gil.Forms;

namespace Gil.Tests.Forms;

/// <summary>
/// Choosing the fields a judged field rests on by replaying its key layer: where a few fields decide a field with many
/// values and the others only blur it, resting on those few answers more often at the same precision.
/// </summary>
public sealed class DependsOnSelectionTests
{
    private static readonly string[] Parts = ["valve", "pump", "seal", "hose", "gauge", "filter", "fan", "relay"];
    private static readonly string[] Places = ["left", "right", "aft"];

    // The code follows from the part. The model is drawn from many values, each seen a few times: a key seen rarely lends
    // its few codes most of their strength, so it pushes wrong codes up. The place and the shift are drawn from a few.
    private static FormDefinition Report(IReadOnlyList<string>? dependsOn = null) => new(
        "report",
        [
            new FieldDefinition("part", FieldRole.Observed),
            new FieldDefinition("place", FieldRole.Observed),
            new FieldDefinition("model", FieldRole.Observed),
            new FieldDefinition("shift", FieldRole.Observed),
            new FieldDefinition("code", FieldRole.Judged) { DependsOn = dependsOn },
        ],
        PromptLanguage.English);

    private static List<SettledDocument> Reports(int count, int seed = 5)
    {
        var random = new Random(seed);
        var documents = new List<SettledDocument>();
        for (var i = 0; i < count; i++)
        {
            var part = random.Next(Parts.Length);
            var place = random.Next(Places.Length);
            // A tenth of the codes are miscoded at random, so no key is perfectly pure.
            var code = random.NextDouble() < 0.1 ? random.Next(Parts.Length) : part;
            documents.Add(new SettledDocument(
                $"d{i}",
                new Dictionary<string, string>
                {
                    ["part"] = Parts[part],
                    ["place"] = Places[place],
                    ["model"] = $"m{random.Next(300)}",
                    ["shift"] = $"s{random.Next(3)}",
                    ["code"] = $"c{code}",
                },
                DateTimeOffset.UnixEpoch.AddMinutes(i)));
        }

        return documents;
    }

    [Fact]
    public void The_fields_that_decide_the_field_are_chosen_and_answer_more_often_than_every_field()
    {
        var documents = Reports(1500);

        var choice = ThresholdSelection.SelectDependsOn(() => new FieldMemory(), Report(), "code", documents, 0.8, 30);

        choice.DependsOn.Should().NotBeNull();
        choice.DependsOn![0].Should().Be("part");
        choice.DependsOn.Should().NotContain("model");
        choice.Chosen.Chosen!.Answered.Should().BeGreaterThan(choice.AllFields.Chosen?.Answered ?? 0);
        choice.Chosen.Chosen.Precision.Should().BeGreaterThanOrEqualTo(0.8);
        // Every evidence field alone, in the form's order, then the additions tried.
        choice.Trials.Take(4).Select(t => t.DependsOn.Single()).Should().Equal("part", "place", "model", "shift");
    }

    [Fact]
    public void The_choice_is_the_replay_SelectKeyThreshold_makes_with_those_fields()
    {
        var documents = Reports(1500);
        var choice = ThresholdSelection.SelectDependsOn(() => new FieldMemory(), Report(), "code", documents, 0.8, 30);

        var again = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), Report(choice.DependsOn), "code", documents, 0.8, 30);
        var all = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), Report(), "code", documents, 0.8, 30);

        choice.Chosen.Should().Be(again);
        choice.AllFields.Should().Be(all);
    }

    [Fact]
    public void The_field_s_own_DependsOn_is_ignored()
    {
        var documents = Reports(1500);

        var unset = ThresholdSelection.SelectDependsOn(() => new FieldMemory(), Report(), "code", documents, 0.8, 30);
        var set = ThresholdSelection.SelectDependsOn(() => new FieldMemory(), Report(["model"]), "code", documents, 0.8, 30);

        set.DependsOn.Should().Equal(unset.DependsOn!);
        set.AllFields.Should().Be(unset.AllFields);
    }

    [Fact]
    public void Where_no_narrower_set_answers_more_often_DependsOn_stays_unset()
    {
        // One field decides the code on its own and the other carries the same information: narrowing gains nothing.
        var form = new FormDefinition(
            "pair",
            [
                new FieldDefinition("part", FieldRole.Observed),
                new FieldDefinition("alias", FieldRole.Observed),
                new FieldDefinition("code", FieldRole.Judged),
            ],
            PromptLanguage.English);
        var documents = Enumerable.Range(0, 400).Select(i => new SettledDocument(
            $"d{i}",
            new Dictionary<string, string> { ["part"] = Parts[i % Parts.Length], ["alias"] = $"a{i % Parts.Length}", ["code"] = $"c{i % Parts.Length}" },
            DateTimeOffset.UnixEpoch.AddMinutes(i))).ToList();

        var choice = ThresholdSelection.SelectDependsOn(() => new FieldMemory(), form, "code", documents, 0.8, 30);

        choice.DependsOn.Should().BeNull();
        choice.Chosen.Should().Be(choice.AllFields);
    }

    [Fact]
    public void A_field_that_is_not_evidence_is_never_tried()
    {
        var form = new FormDefinition(
            "report",
            [
                new FieldDefinition("part", FieldRole.Observed),
                new FieldDefinition("reporter", FieldRole.Observed) { UseAsEvidence = false },
                new FieldDefinition("code", FieldRole.Judged),
            ],
            PromptLanguage.English);
        var documents = Reports(200).Select(d => d with
        {
            Values = new Dictionary<string, string> { ["part"] = d.Values["part"], ["reporter"] = "kim", ["code"] = d.Values["code"] },
        }).ToList();

        var choice = ThresholdSelection.SelectDependsOn(() => new FieldMemory(), form, "code", documents, 0.8, 10);

        choice.Trials.SelectMany(t => t.DependsOn).Should().NotContain("reporter");
    }

    [Fact]
    public void Only_a_judged_field_can_be_asked_about()
    {
        var act = () => ThresholdSelection.SelectDependsOn(() => new FieldMemory(), Report(), "part", Reports(10), 0.8, 5);

        act.Should().Throw<ArgumentException>();
    }
}

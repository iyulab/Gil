using System.Globalization;
using AwesomeAssertions;
using Gil.Forms;

namespace Gil.Tests.Forms;

public sealed class SetValuedSelectionTests
{
    private static readonly FormDefinition Paper = new(
        "paper",
        [
            new FieldDefinition("area", FieldRole.Observed),
            new FieldDefinition("topics", FieldRole.Judged) { Multiple = true },
            new FieldDefinition("lead", FieldRole.Judged),
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(int n, string? area, string[] topics, string? lead = null)
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

        return new SettledDocument($"d{n}", values, DateTimeOffset.UnixEpoch.AddMinutes(n))
        {
            Sets = new Dictionary<string, IReadOnlyList<string>> { ["topics"] = topics },
        };
    }

    /// <summary>Areas alternate: ai papers cover nlp and vision, bio papers genomics.</summary>
    private static List<SettledDocument> Alternating(bool reversed = false) =>
        [.. Enumerable.Range(1, 20).Select(n => n % 2 == 0
            ? Doc(n, "ai", reversed ? ["vision", "nlp"] : ["nlp", "vision"])
            : Doc(n, "bio", ["genomics"]))];

    [Fact]
    public void A_set_is_replayed_value_by_value_and_counts_the_values_sought()
    {
        var replay = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), Paper, "topics", Alternating(), 0.9, 5);

        // Every document after the first is asked about: ten ai papers, nine bio ones after d1. An ai paper is asked with
        // nothing chosen (two values sought) and after one is chosen (one more), a bio paper once (one).
        replay.Lookups.Should().Be((10 * 3) + 9);
        replay.Chosen.Should().NotBeNull();
        replay.Chosen!.Precision.Should().Be(1); // under these keys only the settled values were ever offered
    }

    [Fact]
    public void The_order_a_set_is_listed_in_does_not_change_the_replay()
    {
        var listed = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), Paper, "topics", Alternating(), 0.9, 5);
        var reversed = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), Paper, "topics", Alternating(reversed: true), 0.9, 5);

        reversed.Should().Be(listed);
    }

    [Fact]
    public void A_single_valued_field_is_replayed_with_the_values_of_a_set_as_its_keys()
    {
        // Only the topics tell lead apart; the form's order puts topics before lead, so the replay asks with them.
        var documents = Enumerable.Range(1, 20)
            .Select(n => n % 2 == 0 ? Doc(n, null, ["nlp"], "kim") : Doc(n, null, ["genomics"], "park"))
            .ToList();

        var replay = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), Paper, "lead", documents, 0.9, 5);

        replay.Candidates.Should().Be(18); // of 19 lookups, all but d2 found a value under its topic — nlp was new there
        replay.Chosen!.Precision.Should().Be(1);
    }
}

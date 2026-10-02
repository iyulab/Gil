using System.Globalization;
using AwesomeAssertions;
using Gil.Forms;

namespace Gil.Tests.Forms;

public sealed class FieldMemoryTests
{
    private static readonly FormDefinition Ticket = new(
        "ticket",
        [
            new FieldDefinition("reporter", FieldRole.Observed) { UseAsEvidence = false },
            new FieldDefinition("component", FieldRole.Observed),
            new FieldDefinition("summary", FieldRole.Observed),
            new FieldDefinition("severity", FieldRole.Judged) { Candidates = ["low", "medium", "high"] },
            new FieldDefinition("team", FieldRole.Judged),
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(string id, params (string Field, string Value)[] values) =>
        new(id, values.ToDictionary(v => v.Field, v => v.Value), At(id));

    /// <summary>Documents settle in the order of their numbers, a minute apart.</summary>
    private static DateTimeOffset At(string id) => DateTimeOffset.UnixEpoch.AddMinutes(int.Parse(id[1..], CultureInfo.InvariantCulture));

    private static Dictionary<string, string> Known(params (string Field, string Value)[] values) =>
        values.ToDictionary(v => v.Field, v => v.Value);

    [Fact]
    public void A_value_scores_the_sum_over_known_keys_of_its_strength_under_each()
    {
        var memory = new FieldMemory(recencyDecay: 1);
        memory.Put(Ticket, Doc("d1", ("component", "printer"), ("severity", "low"), ("team", "facilities")));
        memory.Put(Ticket, Doc("d2", ("component", "printer"), ("severity", "high"), ("team", "facilities")));
        memory.Put(Ticket, Doc("d3", ("component", "vpn"), ("severity", "high"), ("team", "network")));

        // Keys for severity: component = printer and team = facilities, each settled twice, low once and high once — a
        // strength of 1 / (2 + 1) for each value under each key.
        var ranked = memory.Rank(Ticket, "severity", Known(("component", "Printer "), ("team", "facilities")), 3);

        ranked.Select(c => (c.Value, c.Score)).Should().Equal(("high", 2.0 / 3), ("low", 2.0 / 3)); // a tie: ordinal order
        ranked.Should().OnlyContain(c => c.Source == FieldSource.SettledFieldMemory);
        ranked[0].Evidence.Should().Be("component: printer"); // the key that backs the value most, as normalised
    }

    [Fact]
    public void A_key_seen_more_often_outweighs_one_seen_once_however_pure_both_are()
    {
        var memory = new FieldMemory(recencyDecay: 1);
        memory.Put(Ticket, Doc("d1", ("component", "disk"), ("team", "storage")));
        memory.Put(Ticket, Doc("d2", ("component", "disk"), ("team", "storage")));
        memory.Put(Ticket, Doc("d3", ("component", "disk"), ("team", "storage")));
        memory.Put(Ticket, Doc("d4", ("summary", "slow"), ("team", "network")));

        // Each key only ever saw one team; three settlements make component = disk the stronger evidence than the single
        // one under summary = slow, although that one is the latest.
        memory.Rank(Ticket, "team", Known(("component", "disk"), ("summary", "slow")), 3)
            .Select(c => (c.Value, c.Score, c.Evidence)).Should().Equal(("storage", 0.75, "component: disk"), ("network", 0.5, "summary: slow"));
    }

    [Fact]
    public void Keys_that_agree_are_trusted_where_each_alone_is_not()
    {
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("component", FieldRole.Observed),
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged) { KeyThreshold = 0.9 },
            ],
            PromptLanguage.English);
        var memory = new FieldMemory(recencyDecay: 1);
        memory.Put(form, Doc("d1", ("component", "vpn"), ("team", "network")));
        memory.Put(form, Doc("d2", ("summary", "slow"), ("team", "network")));

        // Each key was settled once (0.5 each); together they reach the threshold, and the score that ranks is the one
        // compared with it.
        memory.Rank(form, "team", Known(("component", "vpn")), 1).Single().Trusted.Should().BeFalse();
        memory.Rank(form, "team", Known(("component", "vpn"), ("summary", "slow")), 1).Single()
            .Should().Be(new FieldCandidate("network", 1.0, FieldSource.SettledFieldMemory, "component: vpn", Trusted: true));
    }

    [Fact]
    public void Only_the_known_fields_are_keys_when_ranking_but_every_other_field_is_a_key_when_learning()
    {
        var memory = new FieldMemory();
        // Learned together: team is a key for severity although it would be settled after it.
        memory.Put(Ticket, Doc("d1", ("component", "vpn"), ("severity", "high"), ("team", "network")));
        memory.Put(Ticket, Doc("d2", ("component", "printer"), ("severity", "low"), ("team", "facilities")));

        memory.Rank(Ticket, "severity", Known(("team", "network")), 1).Single().Value.Should().Be("high");
        memory.Rank(Ticket, "severity", Known(("component", "printer")), 1).Single().Value.Should().Be("low");
    }

    [Fact]
    public void Without_a_known_key_the_most_frequently_settled_values_stand_in()
    {
        var memory = new FieldMemory(recencyDecay: 1);
        memory.Put(Ticket, Doc("d1", ("component", "vpn"), ("severity", "high")));
        memory.Put(Ticket, Doc("d2", ("component", "vpn"), ("severity", "medium")));
        memory.Put(Ticket, Doc("d3", ("component", "disk"), ("severity", "medium")));

        var ranked = memory.Rank(Ticket, "severity", Known(("component", "keyboard")), 3);

        ranked.Select(c => (c.Value, c.Score)).Should().Equal(("medium", 2.0 / 3), ("high", 1.0 / 3));
        ranked.Should().OnlyContain(c => c.Evidence == null);
        memory.Rank(Ticket, "team", Known(("component", "vpn")), 3).Should().BeEmpty(); // never settled
    }

    [Fact]
    public void A_field_that_is_not_evidence_never_becomes_a_key()
    {
        var memory = new FieldMemory(recencyDecay: 1);
        memory.Put(Ticket, Doc("d1", ("reporter", "Kim"), ("severity", "high")));
        memory.Put(Ticket, Doc("d2", ("reporter", "Lee"), ("severity", "low")));
        memory.Put(Ticket, Doc("d3", ("reporter", "Lee"), ("severity", "low")));

        // With the reporter as a key, "Kim" would point to high; without it only the overall frequency is left.
        var ranked = memory.Rank(Ticket, "severity", Known(("reporter", "Kim")), 1);

        ranked.Single().Should().Be(new FieldCandidate("low", 2.0 / 3, FieldSource.SettledFieldMemory, null, Trusted: false));
    }

    [Fact]
    public void Declared_dependencies_limit_the_keys()
    {
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("component", FieldRole.Observed),
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged) { DependsOn = ["component"] },
            ],
            PromptLanguage.English);
        var memory = new FieldMemory(recencyDecay: 1);
        memory.Put(form, Doc("d1", ("component", "vpn"), ("summary", "slow"), ("team", "network")));
        memory.Put(form, Doc("d2", ("component", "disk"), ("summary", "slow"), ("team", "storage")));
        memory.Put(form, Doc("d3", ("component", "disk"), ("summary", "full"), ("team", "storage")));

        // summary = slow would split network/storage evenly, but it is not a declared dependency. The places left are
        // filled from the overall frequency, without evidence.
        memory.Rank(form, "team", Known(("component", "vpn"), ("summary", "slow")), 3)
            .Select(c => (c.Value, c.Score, c.Evidence)).Should().Equal(("network", 0.5, "component: vpn"), ("storage", 2.0 / 3, null));
    }

    [Fact]
    public void Long_free_text_values_are_not_keys()
    {
        var memory = new FieldMemory(maxKeyLength: 10, recencyDecay: 1);
        memory.Put(Ticket, Doc("d1", ("summary", "the screen goes dark after lunch"), ("team", "facilities")));
        memory.Put(Ticket, Doc("d2", ("summary", "no signal"), ("team", "network")));
        memory.Put(Ticket, Doc("d3", ("summary", "no signal"), ("team", "network")));

        memory.Rank(Ticket, "team", Known(("summary", "the screen goes dark after lunch")), 1)
            .Single().Should().Be(new FieldCandidate("network", 2.0 / 3, FieldSource.SettledFieldMemory, null, Trusted: false));
        memory.Rank(Ticket, "team", Known(("summary", "No  Signal")), 1)
            .Single().Should().Be(new FieldCandidate("network", 2.0 / 3, FieldSource.SettledFieldMemory, "summary: no signal", Trusted: false));
    }

    [Fact]
    public void Putting_a_document_again_replaces_its_contribution_so_it_never_counts_twice()
    {
        var memory = new FieldMemory();
        memory.Put(Ticket, Doc("d1", ("component", "vpn"), ("severity", "low")));
        memory.Put(Ticket, Doc("d1", ("component", "vpn"), ("severity", "high"))); // corrected later
        memory.Put(Ticket, Doc("d1", ("component", "vpn"), ("severity", "high"))); // and put again unchanged

        memory.Count("ticket").Should().Be(1);
        memory.Rank(Ticket, "severity", Known(("component", "vpn")), 3)
            .Select(c => (c.Value, c.Score)).Should().Equal(("high", 0.5)); // one settlement: 1 / (1 + 1)

        memory.Remove("ticket", "d1");
        memory.Count("ticket").Should().Be(0);
        memory.Rank(Ticket, "severity", Known(("component", "vpn")), 3).Should().BeEmpty();
    }

    [Fact]
    public void Settling_field_by_field_reaches_the_same_state_as_putting_the_finished_documents_in_any_order()
    {
        var finished = new[]
        {
            Doc("d1", ("component", "vpn"), ("severity", "high"), ("team", "network")),
            Doc("d2", ("component", "printer"), ("severity", "low"), ("team", "facilities")),
            Doc("d3", ("component", "vpn"), ("severity", "medium"), ("team", "network")),
            Doc("d4", ("component", "disk"), ("severity", "high"), ("team", "storage")),
        };

        var live = new FieldMemory();
        foreach (var document in finished)
        {
            // Values arrive one at a time and the document is put after each, as a session does.
            var sofar = new Dictionary<string, string>();
            foreach (var (field, value) in document.Values)
            {
                sofar[field] = value;
                live.Put(Ticket, new SettledDocument(document.DocumentId, new Dictionary<string, string>(sofar), document.SettledAt));
            }
        }

        var rebuilt = new FieldMemory();
        foreach (var document in Enumerable.Reverse(finished))
        {
            rebuilt.Put(Ticket, document);
        }

        foreach (var known in new[] { Known(("component", "vpn")), Known(("team", "storage")), Known(("component", "keyboard")) })
        {
            foreach (var field in new[] { "severity", "team" })
            {
                live.Rank(Ticket, field, known, 3).Should().Equal(rebuilt.Rank(Ticket, field, known, 3));
            }
        }
    }

    [Fact]
    public void Forms_are_kept_apart()
    {
        var other = Ticket with { };
        var memory = new FieldMemory();
        memory.Put(Ticket, Doc("d1", ("component", "vpn"), ("severity", "high")));

        memory.Count("ticket").Should().Be(1);
        memory.Count("elsewhere").Should().Be(0);
        memory.Rank(new FormDefinition("elsewhere", Ticket.Fields, PromptLanguage.English), "severity", Known(("component", "vpn")), 3)
            .Should().BeEmpty();
        memory.Rank(other, "severity", Known(("component", "vpn")), 3).Should().ContainSingle();
    }
}

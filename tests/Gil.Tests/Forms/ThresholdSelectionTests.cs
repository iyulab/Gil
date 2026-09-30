using System.Globalization;
using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

public sealed class ThresholdSelectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FormDefinition Ticket = new(
        "ticket",
        [
            new FieldDefinition("reporter", FieldRole.Observed) { UseAsEvidence = false },
            new FieldDefinition("summary", FieldRole.Observed),
            new FieldDefinition("team", FieldRole.Judged),
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(string id, string summary, string? team) =>
        new(id, team is null
            ? new Dictionary<string, string> { ["reporter"] = "Kim", ["summary"] = summary }
            : new Dictionary<string, string> { ["reporter"] = "Kim", ["summary"] = summary, ["team"] = team }, At(id));

    /// <summary>Documents settle in the order of their numbers, a minute apart.</summary>
    private static DateTimeOffset At(string id) => DateTimeOffset.UnixEpoch.AddMinutes(int.Parse(id[1..], CultureInfo.InvariantCulture));

    /// <summary>Answers lookups from a script and logs every call in order.</summary>
    private sealed class ScriptedMemory(params (double Similarity, string Answer)?[] script) : IMemory
    {
        private int _next;

        public List<string> Log { get; } = [];

        public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default)
        {
            Log.Add($"lookup {task} [{state}]");
            var step = script[_next++];
            return Task.FromResult<(MemoryMatch?, double)>((step is { } s ? new MemoryMatch("earlier", s.Similarity, s.Answer) : null, 0));
        }

        public Task<double> RememberAsync(string task, string key, string state, string answer, string traceId, CancellationToken cancellationToken = default)
        {
            Log.Add($"remember {key}={answer}");
            return Task.FromResult(0.0);
        }

        public void Forget(string task, string key) => Log.Add($"forget {key}");
    }

    [Fact]
    public async Task Each_document_is_looked_up_in_a_memory_of_the_ones_before_it_then_remembered()
    {
        var memory = new ScriptedMemory(null, (0.9, "network"));
        var documents = new[] { Doc("d1", "vpn drops", "network"), Doc("d2", "no value yet", null), Doc("d3", "vpn slow", "network"), Doc("d4", "vpn down", "network") };

        var choice = await ThresholdSelection.SelectAsync(memory, Ticket, "team", documents, 0.9, minimumAnswered: 1, Ct);

        memory.Log.Should().Equal(
            "remember d1=network",
            "lookup ticket/team [summary: vpn slow]", // the reporter is not evidence; d2 has no team and is skipped
            "remember d3=network",
            "lookup ticket/team [summary: vpn down]",
            "remember d4=network");
        choice.Should().Be(new ThresholdChoice(0.9, 1.0, 0.5, 1, 2)); // the empty first lookup counts, unanswered
    }

    [Fact]
    public async Task The_lowest_threshold_that_still_meets_the_target_is_chosen()
    {
        // Similarities and whether the nearest answer matched: 0.9 ✓, 0.8 ✓, 0.7 ✗, 0.6 ✓, 0.5 ✗
        // Precision fitted as non-decreasing in similarity: [0.8, 0.9] 1, [0.6, 0.7] 1/2 (0.6 alone would outdo 0.7), 0.5 0.
        var memory = new ScriptedMemory((0.9, "a"), (0.8, "a"), (0.7, "b"), (0.6, "a"), (0.5, "b"));
        var documents = Enumerable.Range(0, 6).Select(i => Doc($"d{i}", $"s{i}", "a"));

        var strict = await ThresholdSelection.SelectAsync(memory, Ticket, "team", documents, 0.95, minimumAnswered: 1, Ct);
        var loose = await ThresholdSelection.SelectAsync(new ScriptedMemory((0.9, "a"), (0.8, "a"), (0.7, "b"), (0.6, "a"), (0.5, "b")), Ticket, "team", documents, 0.75, minimumAnswered: 1, Ct);

        strict.Should().Be(new ThresholdChoice(0.8, 1.0, 0.4, 2, 5));
        // Together from 0.6 up the answers reach 3/4, but the band at 0.6–0.7 is right only half the time.
        loose.Should().Be(new ThresholdChoice(0.8, 1.0, 0.4, 2, 5));
        var lower = await ThresholdSelection.SelectAsync(new ScriptedMemory((0.9, "a"), (0.8, "a"), (0.7, "b"), (0.6, "a"), (0.5, "b")), Ticket, "team", documents, 0.5, minimumAnswered: 1, Ct);
        lower.Should().Be(new ThresholdChoice(0.6, 0.75, 0.8, 4, 5));
    }

    [Fact]
    public async Task No_threshold_is_chosen_when_the_target_is_out_of_reach_or_rests_on_too_few_answers()
    {
        var documents = Enumerable.Range(0, 3).Select(i => Doc($"d{i}", $"s{i}", "a")).ToList();

        (await ThresholdSelection.SelectAsync(new ScriptedMemory((0.9, "b"), (0.8, "b")), Ticket, "team", documents, 0.5, 1, Ct)).Should().BeNull();
        (await ThresholdSelection.SelectAsync(new ScriptedMemory((0.9, "a"), (0.8, "a")), Ticket, "team", documents, 0.5, 3, Ct)).Should().BeNull();
        await FluentActions.Awaiting(() => ThresholdSelection.SelectAsync(new ScriptedMemory(), Ticket, "summary", documents, 0.9, 1, Ct))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_replay_over_a_lexical_memory_picks_a_threshold_that_separates_near_from_far()
    {
        var documents = new[]
        {
            Doc("d1", "vpn drops every ten minutes", "network"),
            Doc("d2", "printer on floor 3 is jammed", "facilities"),
            Doc("d3", "vpn drops every five minutes", "network"),
            Doc("d4", "printer on floor 2 is jammed", "facilities"),
            Doc("d5", "the coffee machine leaks", "kitchen"), // teams never seen before: these lookups cannot be right
            Doc("d6", "wifi is slow in the lobby", "reception"),
        };

        var choice = await ThresholdSelection.SelectAsync(new LexicalMemory(), Ticket, "team", documents, 1.0, minimumAnswered: 2, Ct);

        choice.Should().NotBeNull();
        (choice!.Answered, choice.Lookups, choice.Precision).Should().Be((2, 5, 1.0)); // only the two near repeats are answered
        choice.Threshold.Should().BeGreaterThan(0.3);
    }
}

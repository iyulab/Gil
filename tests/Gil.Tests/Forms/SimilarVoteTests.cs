using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

/// <summary>The similar document layer's vote (<see cref="FieldDefinition.SimilarDocumentVotes"/>).</summary>
public sealed class SimilarVoteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A threshold of 0 trusts the layer's candidate, so it comes first rather than behind the field's most frequent value.
    private static FormDefinition Intake(int votes, IReadOnlyList<string>? candidates = null) => new(
        "intake",
        [
            new FieldDefinition("note", FieldRole.Observed),
            new FieldDefinition("category", FieldRole.Judged) { MemoryThreshold = 0, SimilarDocumentVotes = votes, Candidates = candidates },
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(int number, string note, string category) =>
        new($"d{number}", new Dictionary<string, string> { ["note"] = note, ["category"] = category },
            DateTimeOffset.UnixEpoch.AddMinutes(number));

    // The nearest document says "billing"; the three after it, a little less similar, say "outage".
    private static readonly SettledDocument[] History =
    [
        Doc(1, "router keeps dropping the connection every evening", "billing"),
        Doc(2, "router keeps dropping the connection at night", "outage"),
        Doc(3, "router keeps dropping connection in the evening", "outage"),
        Doc(4, "the router is dropping the connection every night", "outage"),
        Doc(5, "invoice shows a charge twice this month", "billing"),
    ];

    private static readonly Dictionary<string, string> Asked = new() { ["note"] = "router keeps dropping the connection every evening again" };

    private static async Task<FieldSuggestion> SuggestAsync(FormDefinition form, IMemory? memory = null)
    {
        var resolver = new FormResolver(new FieldMemory(), memory ?? new LexicalMemory(), similarDocumentCount: 5);
        await resolver.RebuildAsync(form, History, Ct);
        return (await resolver.SuggestAsync(form, "d9", Asked, Ct)).Single();
    }

    [Fact]
    public async Task Neighbours_that_agree_outvote_a_nearer_document()
    {
        var suggestion = await SuggestAsync(Intake(votes: 10));

        var similar = suggestion.Candidates.Single(c => c.Source == FieldSource.SimilarDocument);
        similar.Value.Should().Be("outage");
        similar.Evidence.Should().Be(suggestion.SimilarDocuments.First(m => m.Answer == "outage").Source); // its nearest voter
        suggestion.SimilarDocuments[0].Source.Should().Be("d1"); // the nearest is still reported first

        // The margin: the winner's weight less the runner-up's, over all the weight cast.
        var weights = suggestion.SimilarDocuments.GroupBy(m => m.Answer).ToDictionary(g => g.Key, g => g.Sum(m => m.Similarity));
        similar.Score.Should().BeApproximately((weights["outage"] - weights["billing"]) / weights.Values.Sum(), 1e-9);
    }

    [Fact]
    public async Task One_vote_lets_the_nearest_document_decide_on_its_similarity()
    {
        var suggestion = await SuggestAsync(Intake(votes: 1));

        var similar = suggestion.Candidates.Single(c => c.Source == FieldSource.SimilarDocument);
        similar.Value.Should().Be("billing");
        similar.Evidence.Should().Be("d1");
        similar.Score.Should().Be(suggestion.SimilarDocuments[0].Similarity);
    }

    [Fact]
    public async Task Documents_outside_the_domain_do_not_vote()
    {
        // Without "outage" in the list only the two billing documents vote, and they agree.
        var suggestion = await SuggestAsync(Intake(votes: 10, candidates: ["billing", "refund"]));

        var similar = suggestion.Candidates.Single(c => c.Source == FieldSource.SimilarDocument);
        similar.Value.Should().Be("billing");
        similar.Score.Should().Be(1);
        suggestion.SimilarDocuments.Should().OnlyContain(m => m.Answer == "billing");
    }

    [Fact]
    public async Task A_memory_that_ranks_only_its_nearest_keeps_the_similarity_scale()
    {
        var suggestion = await SuggestAsync(Intake(votes: 10), new NearestOnly(new LexicalMemory()));

        var similar = suggestion.Candidates.Single(c => c.Source == FieldSource.SimilarDocument);
        similar.Value.Should().Be("billing");
        similar.Score.Should().Be(suggestion.SimilarDocuments[0].Similarity).And.BeLessThan(1);
    }

    [Fact]
    public async Task A_replay_scores_lookups_as_suggestions_do()
    {
        // The replay asks about each document with the ones before it remembered, as these suggestions do; a threshold is
        // a score some lookup had, so the one it chooses is exactly the score a suggestion for that document carried — on
        // the vote's scale, not the nearest document's similarity.
        var form = Intake(votes: 10);
        var scores = new List<double>();
        for (var i = 1; i < History.Length; i++)
        {
            var resolver = new FormResolver(new FieldMemory(), new LexicalMemory());
            await resolver.RebuildAsync(form, History.Take(i), Ct);
            var asked = History[i].Values.Where(v => v.Key != "category").ToDictionary(v => v.Key, v => v.Value);
            var suggestion = (await resolver.SuggestAsync(form, History[i].DocumentId, asked, Ct)).Single();
            scores.Add(suggestion.Candidates.Single(c => c.Source == FieldSource.SimilarDocument).Score);
        }

        var replay = await ThresholdSelection.SelectAsync(new LexicalMemory(), form, "category", History, 0.01, minimumAnswered: 1, Ct);

        replay.Candidates.Should().Be(scores.Count);
        replay.Chosen.Should().NotBeNull();
        scores.Should().Contain(replay.Chosen!.Threshold);
        scores.Should().Contain(replay.MostPrecise!.Threshold);
    }

    [Fact]
    public void A_field_gives_at_least_one_document_a_vote()
    {
        var create = () => Intake(votes: 0);

        create.Should().Throw<ArgumentException>().WithMessage("*at least one*");
    }

    /// <summary>A memory that leaves <see cref="IMemory.NearestAsync"/> to its default, which ranks only the nearest.</summary>
    private sealed class NearestOnly(IMemory inner) : IMemory
    {
        public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default) =>
            inner.LookupAsync(task, state, traceId, cancellationToken);

        public Task<double> RememberAsync(string task, string key, string state, string answer, string traceId, CancellationToken cancellationToken = default) =>
            inner.RememberAsync(task, key, state, answer, traceId, cancellationToken);

        public void Forget(string task, string key) => inner.Forget(task, key);
    }
}

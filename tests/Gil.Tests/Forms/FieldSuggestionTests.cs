using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

/// <summary>
/// One judged field can be asked about on its own — the field an application shows, or the one a person types into —
/// without the work of suggesting the rest of the form.
/// </summary>
public sealed class FieldSuggestionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FormDefinition Ticket = new(
        "ticket",
        [
            new FieldDefinition("channel", FieldRole.Observed),
            new FieldDefinition("summary", FieldRole.Observed),
            new FieldDefinition("team", FieldRole.Judged) { KeyThreshold = 0.5, TypedKeyThresholds = [0.25], MemoryThreshold = 0.1, DependsOn = ["channel", "summary"] },
            new FieldDefinition("priority", FieldRole.Judged) { MemoryThreshold = 0.1, DependsOn = ["summary"] },
            new FieldDefinition("internal", FieldRole.Judged) { Policy = FieldPolicy.Off },
        ],
        PromptLanguage.English);

    private static SettledDocument Doc(int number, string channel, string summary, string team, string priority) =>
        new($"d{number}", new Dictionary<string, string> { ["channel"] = channel, ["summary"] = summary, ["team"] = team, ["priority"] = priority },
            DateTimeOffset.UnixEpoch.AddMinutes(number));

    private static readonly SettledDocument[] History =
    [
        Doc(1, "phone", "card payment failed", "billing", "high"),
        Doc(2, "phone", "router keeps dropping", "network", "low"),
        Doc(3, "phone", "card payment failed again", "billing", "high"),
        Doc(4, "email", "router lights off", "network", "low"),
    ];

    private static readonly Dictionary<string, string> Asked = new() { ["channel"] = "phone", ["summary"] = "card payment failed twice" };

    private static async Task<(FormResolver Resolver, CountingMemory Memory)> FilledAsync()
    {
        var memory = new CountingMemory(new LexicalMemory());
        var resolver = new FormResolver(new FieldMemory(), memory, similarDocumentCount: 3);
        await resolver.RebuildAsync(Ticket, History, Ct);
        memory.Lookups = 0;
        return (resolver, memory);
    }

    [Fact]
    public async Task One_field_is_suggested_as_the_whole_form_would_and_only_its_similar_documents_are_looked_up()
    {
        var (resolver, memory) = await FilledAsync();

        var all = await resolver.SuggestAsync(Ticket, "d9", Asked, Ct);
        var wholeForm = memory.Lookups;
        memory.Lookups = 0;
        var team = await resolver.SuggestAsync(Ticket, "d9", "team", Asked, cancellationToken: Ct);

        wholeForm.Should().Be(2); // team and priority, not the field that is off
        memory.Lookups.Should().Be(1);
        team.Field.Should().Be("team");
        team.Candidates.Select(c => (c.Value, c.Source, c.Trusted)).Should().Equal(
            all.Single(s => s.Field == "team").Candidates.Select(c => (c.Value, c.Source, c.Trusted)));
        team.Answered.Should().Be(all.Single(s => s.Field == "team").Answered);
    }

    [Fact]
    public async Task Typing_into_a_field_suggests_that_field_alone_with_one_lookup_per_pause()
    {
        var (resolver, memory) = await FilledAsync();

        var typed = await resolver.SuggestAsync(Ticket, "d9", "team", Asked, typed: "b", cancellationToken: Ct);
        var other = await resolver.SuggestAsync(Ticket, "d9", "team", Asked, typed: "n", cancellationToken: Ct);

        memory.Lookups.Should().Be(2);
        typed.Candidates[0].Value.Should().Be("billing");
        typed.Candidates.Should().OnlyContain(c => c.Value.StartsWith('b'));
        other.Candidates.Should().OnlyContain(c => c.Value.StartsWith('n'));
    }

    [Fact]
    public async Task A_field_is_asked_about_only_while_it_is_open()
    {
        var (resolver, _) = await FilledAsync();
        var withTeam = new Dictionary<string, string>(Asked) { ["team"] = "billing" };

        var observed = () => resolver.SuggestAsync(Ticket, "d9", "channel", Asked, cancellationToken: Ct);
        var off = () => resolver.SuggestAsync(Ticket, "d9", "internal", Asked, cancellationToken: Ct);
        var settled = () => resolver.SuggestAsync(Ticket, "d9", "team", withTeam, cancellationToken: Ct);
        var unknown = () => resolver.SuggestAsync(Ticket, "d9", "nothing", Asked, cancellationToken: Ct);

        await observed.Should().ThrowAsync<ArgumentException>().WithMessage("*'channel'*not a judged field*");
        await off.Should().ThrowAsync<ArgumentException>().WithMessage("*'internal'*off*");
        await settled.Should().ThrowAsync<ArgumentException>().WithMessage("*'team'*already has a value*");
        await unknown.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_session_suggests_one_field_typed_into_or_not_and_keeps_it_open()
    {
        var (resolver, memory) = await FilledAsync();
        var session = resolver.Open(Ticket, "d9");
        await session.ObserveAsync("channel", "phone", Ct);
        await session.ObserveAsync("summary", "router keeps dropping again", Ct);
        memory.Lookups = 0;

        var typed = await session.SuggestAsync("team", "b", Ct);
        var untyped = await session.SuggestAsync("team", cancellationToken: Ct);
        await session.SettleAsync("team", Settlement.Accept("network"), Ct);
        var settled = () => session.SuggestAsync("team", "n", Ct);

        memory.Lookups.Should().Be(2);
        typed.Candidates.Should().OnlyContain(c => c.Value.StartsWith('b'));
        untyped.Candidates[0].Value.Should().Be("network");
        await settled.Should().ThrowAsync<ArgumentException>().WithMessage("*'team'*not open*");
    }

    /// <summary>Counts the similar document lookups a suggestion makes.</summary>
    private sealed class CountingMemory(IMemory inner) : IMemory
    {
        public int Lookups { get; set; }

        public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default)
        {
            Lookups++;
            return inner.LookupAsync(task, state, traceId, cancellationToken);
        }

        public Task<(IReadOnlyList<MemoryMatch> Matches, double Energy)> NearestAsync(string task, string state, int count, string traceId, CancellationToken cancellationToken = default)
        {
            Lookups++;
            return inner.NearestAsync(task, state, count, traceId, cancellationToken);
        }

        public Task<double> RememberAsync(string task, string key, string state, string answer, string traceId, CancellationToken cancellationToken = default) =>
            inner.RememberAsync(task, key, state, answer, traceId, cancellationToken);

        public void Forget(string task, string key) => inner.Forget(task, key);
    }
}

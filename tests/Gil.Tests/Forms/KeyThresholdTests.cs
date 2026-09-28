using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

public sealed class KeyThresholdTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Departments = ["sales", "finance", "legal", "support", "research"];

    /// <summary>Each team's requests; the team follows from what the request says, never from the department.</summary>
    private static readonly (string Team, string[] Phrases)[] Teams =
    [
        ("network", ["vpn drops every few minutes", "wifi is very slow", "cannot reach the intranet", "ethernet port is dead"]),
        ("facilities", ["printer is jammed", "the light keeps flickering", "door lock is broken", "heating is off"]),
        ("accounts", ["please reset my password", "my account is locked", "cannot log in to email", "lost my mfa device"]),
        ("hardware", ["laptop screen is cracked", "keyboard keys are stuck", "mouse stopped working", "battery is swollen"]),
        ("software", ["spreadsheet app crashes", "license has expired", "the update failed", "app freezes on start"]),
    ];

    private static FormDefinition Intake(double? keyThreshold, double? memoryThreshold) => new(
        "intake",
        [
            new FieldDefinition("request", FieldRole.Observed),
            new FieldDefinition("department", FieldRole.Observed) { Candidates = Departments },
            new FieldDefinition("team", FieldRole.Judged) { KeyThreshold = keyThreshold, MemoryThreshold = memoryThreshold },
        ],
        PromptLanguage.English);

    /// <summary>Free-text requests whose team the text decides, filed from a department chosen at random.</summary>
    private static List<SettledDocument> Requests(int seed, int count, int first = 1)
    {
        var random = new Random(seed);
        var documents = new List<SettledDocument>();
        for (var i = first; i < first + count; i++)
        {
            var (team, phrases) = Teams[random.Next(Teams.Length)];
            var request = $"{phrases[random.Next(phrases.Length)]} since {random.Next(1, 999)}";
            var values = new Dictionary<string, string>
            {
                ["request"] = request,
                ["department"] = Departments[random.Next(Departments.Length)],
                ["team"] = team,
            };
            documents.Add(new SettledDocument($"r{i}", values, DateTimeOffset.UnixEpoch.AddMinutes(i)));
        }

        return documents;
    }

    private static Dictionary<string, string> Without(SettledDocument document, string field) =>
        document.Values.Where(v => v.Key != field).ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public async Task A_key_that_does_not_decide_the_field_never_outranks_a_similar_document()
    {
        var history = Requests(seed: 7, count: 300);
        var asked = Requests(seed: 8, count: 100, first: 1000);
        var similarity = await ThresholdSelection.SelectAsync(new LexicalMemory(), Intake(null, null), "team", history, 0.8, 10, Ct);
        var key = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), Intake(null, null), "team", history, 0.8, 10);
        similarity.Should().NotBeNull();
        key.Should().BeNull(); // the department alone never reaches the target precision

        foreach (var keyThreshold in new[] { key?.Threshold, null })
        {
            var form = Intake(keyThreshold, similarity!.Threshold);
            var resolver = new FormResolver(new FieldMemory(), new LexicalMemory());
            await resolver.RebuildAsync(form, history, Ct);

            var (answered, right) = (0, 0);
            foreach (var document in asked)
            {
                var team = (await resolver.SuggestAsync(form, document.DocumentId, Without(document, "team"), Ct)).Single();
                if (team.Answered)
                {
                    answered++;
                    right += team.Candidates[0].Value == document.Values["team"] ? 1 : 0;
                }
                else
                {
                    // A guess is still listed, but the department alone never answers.
                    team.Candidates.Should().NotBeEmpty();
                }
            }

            answered.Should().BeGreaterThan(50, $"key threshold {keyThreshold}");
            ((double)right / answered).Should().BeGreaterThanOrEqualTo(0.8, $"key threshold {keyThreshold}");
        }
    }

    [Fact]
    public async Task A_key_that_decides_the_field_is_chosen_to_answer()
    {
        var random = new Random(3);
        string[] components = ["vpn", "printer", "badge", "mail", "laptop", "phone", "desk", "license"];
        string[] teams = ["network", "facilities", "security", "accounts", "hardware", "telecom", "facilities", "software"];
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("component", FieldRole.Observed),
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged),
            ],
            PromptLanguage.English);
        var history = Enumerable.Range(1, 200).Select(i =>
        {
            var c = random.Next(components.Length);
            var values = new Dictionary<string, string>
            {
                ["component"] = components[c],
                ["summary"] = $"issue number {random.Next(100000)} reported on {random.Next(1, 28)} of the month",
                ["team"] = teams[c],
            };
            return new SettledDocument($"t{i}", values, DateTimeOffset.UnixEpoch.AddMinutes(i));
        }).ToList();

        var choice = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), form, "team", history, 0.95, 10);

        choice.Should().NotBeNull();
        (choice!.Precision, choice.Lookups).Should().Be((1.0, 199));
        choice.AnswerRate.Should().BeGreaterThan(0.9); // only a component's first few settlements are too weak to answer

        var keyed = new FormDefinition("ticket", [.. form.Fields.Select(f => f.Name == "team" ? f with { KeyThreshold = choice.Threshold } : f)], form.Language);
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(keyed, history, Ct);
        var team = (await resolver.SuggestAsync(keyed, "t201", new Dictionary<string, string> { ["component"] = "badge", ["summary"] = "door will not open" }, Ct)).Single();
        (team.Answered, team.Source, team.Candidates[0].Value).Should().Be((true, FieldSource.SettledFieldMemory, "security"));
    }

    [Fact]
    public async Task A_key_below_the_threshold_is_a_guess_and_still_keeps_the_model_out()
    {
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("component", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged) { KeyThreshold = 0.6 },
            ],
            PromptLanguage.English);
        var model = new CountingModel();
        var sink = new ListSink();
        var resolver = new FormResolver(new FieldMemory(recencyDecay: 1), model: model, sink: sink);
        SettledDocument Doc(int i, string component, string team) =>
            new($"d{i}", new Dictionary<string, string> { ["component"] = component, ["team"] = team }, DateTimeOffset.UnixEpoch.AddMinutes(i));
        await resolver.RebuildAsync(form, [Doc(1, "vpn", "network"), Doc(2, "vpn", "network"), Doc(3, "printer", "facilities")], Ct);

        // vpn: 2 of 2, strength 2/3 — answers. printer: 1 of 1, strength 1/2 — a guess.
        var strong = (await resolver.SuggestAsync(form, "d4", new Dictionary<string, string> { ["component"] = "vpn" }, Ct)).Single();
        var weak = (await resolver.SuggestAsync(form, "d5", new Dictionary<string, string> { ["component"] = "printer" }, Ct)).Single();

        (strong.Answered, strong.Candidates[0].Trusted).Should().Be((true, true));
        (weak.Answered, weak.Source, weak.Candidates[0]).Should().Be(
            (false, FieldSource.SettledFieldMemory, new FieldCandidate("facilities", 1.0, FieldSource.SettledFieldMemory, "component: printer", Trusted: false)));
        model.Asked.Should().Be(0); // a value the document's own keys back, however weakly, beats a model's guess
        (sink.Traces[weak.TraceId].Outcome!.Mode, sink.Traces[weak.TraceId].Outcome!.Output).Should().Be(("abstain", null));
        (sink.Traces[strong.TraceId].Outcome!.Mode, sink.Traces[strong.TraceId].Outcome!.Output).Should().Be(("field_memory", "network"));
    }

    [Fact]
    public async Task The_nearest_document_below_the_similarity_threshold_is_the_first_guess()
    {
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged) { MemoryThreshold = 0.99 },
            ],
            PromptLanguage.English);
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory());
        await resolver.RebuildAsync(form,
        [
            new SettledDocument("d1", new Dictionary<string, string> { ["summary"] = "printer on floor 3 is jammed", ["team"] = "facilities" }, DateTimeOffset.UnixEpoch.AddMinutes(1)),
            new SettledDocument("d2", new Dictionary<string, string> { ["summary"] = "vpn drops", ["team"] = "network" }, DateTimeOffset.UnixEpoch.AddMinutes(2)),
            new SettledDocument("d3", new Dictionary<string, string> { ["summary"] = "wifi drops", ["team"] = "network" }, DateTimeOffset.UnixEpoch.AddMinutes(3)),
        ], Ct);

        var team = (await resolver.SuggestAsync(form, "d4", new Dictionary<string, string> { ["summary"] = "printer on floor 2 is jammed" }, Ct)).Single();

        team.Answered.Should().BeFalse();
        team.Candidates.Select(c => (c.Value, c.Source, c.Evidence, c.Trusted)).Should().Equal(
            ("facilities", FieldSource.SimilarDocument, "d1", false),
            ("network", FieldSource.SettledFieldMemory, null, false));
    }

    [Fact]
    public async Task Suggesting_from_values_writes_nothing_and_a_saved_document_is_not_its_own_evidence()
    {
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("component", FieldRole.Observed),
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged) { KeyThreshold = 0.3, MemoryThreshold = 0.3 },
                new FieldDefinition("assessment", FieldRole.Judged) { Policy = FieldPolicy.Off },
            ],
            PromptLanguage.English);
        var fields = new FieldMemory();
        var resolver = new FormResolver(fields, new LexicalMemory());
        var saved = new SettledDocument("d1", new Dictionary<string, string> { ["component"] = "badge", ["summary"] = "door will not open", ["team"] = "security" }, DateTimeOffset.UnixEpoch);
        await resolver.RebuildAsync(form, [saved], Ct);

        // Editing the saved document: its own settlement backs nothing, by key or by similarity.
        var own = await resolver.SuggestAsync(form, "d1", new Dictionary<string, string> { ["component"] = "badge", ["summary"] = "door will not open" }, Ct);
        own.Select(s => s.Field).Should().Equal("team"); // assessment is Off
        (own[0].Answered, own[0].Candidates.Count).Should().Be((false, 0));

        // Another document with the same values is backed by it; asking wrote nothing.
        var other = (await resolver.SuggestAsync(form, "d2", new Dictionary<string, string> { ["component"] = "badge", ["summary"] = "door will not open" }, Ct)).Single();
        (other.Answered, other.Candidates[0].Value).Should().Be((true, "security"));
        fields.Count("ticket").Should().Be(1);
        (await resolver.SuggestAsync(form, "d2", new Dictionary<string, string> { ["component"] = "badge", ["team"] = "network" }, Ct)).Should().BeEmpty();
    }

    private sealed class CountingModel : IFieldModel
    {
        public int Asked { get; private set; }

        public Task<FieldModelResult> SuggestAsync(FormDefinition form, string field, IReadOnlyList<KeyValuePair<string, string>> evidence, string traceId, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(new FieldModelResult([new FieldCandidate("guessed", 0.5, FieldSource.Model, null)], 0.5, 1));
        }
    }
}

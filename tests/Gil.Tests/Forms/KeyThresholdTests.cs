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
        var similarity = (await ThresholdSelection.SelectAsync(new LexicalMemory(), Intake(null, null), "team", history, 0.8, 10, Ct)).Chosen;
        var key = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), Intake(null, null), "team", history, 0.8, 10).Chosen;
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

        var choice = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), form, "team", history, 0.95, 10).Chosen;

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
    public void A_weak_key_is_not_trusted_because_duplicates_of_other_documents_answer_well()
    {
        // Twenty request texts, each filed ten times with the same team; the source is unrelated to the team. Once a
        // text has been seen, its own key answers every repeat correctly. A text seen for the first time has only its
        // source as a known key, whose most frequent team is right about one time in four — those answers must not
        // ride on the repeats' precision.
        var random = new Random(11);
        string[] sources = ["email", "phone", "chat", "portal"];
        string[] teams = ["network", "facilities", "accounts", "hardware", "software"];
        var texts = Enumerable.Range(0, 20).Select(t => (Text: $"request template {t}", Team: teams[t % teams.Length])).ToList();
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("request", FieldRole.Observed),
                new FieldDefinition("source", FieldRole.Observed) { Candidates = sources },
                new FieldDefinition("team", FieldRole.Judged),
            ],
            PromptLanguage.English);
        var history = texts
            .SelectMany(t => Enumerable.Repeat(t, 10))
            .OrderBy(_ => random.Next())
            .Select((t, i) => new SettledDocument(
                $"t{i + 1}",
                new Dictionary<string, string> { ["request"] = t.Text, ["source"] = sources[random.Next(sources.Length)], ["team"] = t.Team },
                DateTimeOffset.UnixEpoch.AddMinutes(i + 1)))
            .ToList();

        var choice = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), form, "team", history, 0.9, 10).Chosen;

        choice.Should().NotBeNull(); // repeats still answer
        var keyed = new FormDefinition("ticket", [.. form.Fields.Select(f => f.Name == "team" ? f with { KeyThreshold = choice!.Threshold } : f)], form.Language);
        var fields = new FieldMemory();
        foreach (var document in history)
        {
            fields.Put(keyed, document);
        }

        foreach (var source in sources)
        {
            var weak = fields.Rank(keyed, "team", new Dictionary<string, string> { ["request"] = "a request never seen", ["source"] = source }, 1)[0];
            weak.Trusted.Should().BeFalse($"the source '{source}' alone backs {weak.Value} about one time in four (threshold {choice!.Threshold})");
        }

        var repeat = fields.Rank(keyed, "team", new Dictionary<string, string> { ["request"] = "request template 3", ["source"] = "phone" }, 1)[0];
        (repeat.Value, repeat.Trusted).Should().Be(("hardware", true));
    }

    [Fact]
    public async Task The_similarity_threshold_is_chosen_on_the_lookups_no_key_answers()
    {
        // Most requests name a component that decides the team, and their text names it too, so a similar request is
        // right as well. The rest come from a catch-all component whose team varies; their texts are just as similar to
        // each other, but the nearest one's team is right about one time in five. Chosen on every lookup, a similarity
        // threshold rides on the first kind — which the key already answers — and then answers the second kind wrongly.
        // That is a layer the nearest document decides alone; with its neighbours voting, as by default, it would not.
        var random = new Random(5);
        string[] components = ["vpn", "printer", "badge", "laptop", "mail"];
        string[] teams = ["network", "facilities", "security", "hardware", "accounts"];
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("component", FieldRole.Observed),
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged) { SimilarDocumentVotes = 1 },
            ],
            PromptLanguage.English);
        SettledDocument Request(int i)
        {
            var (component, team) = random.NextDouble() < 0.85
                ? (components[i % components.Length], teams[i % teams.Length])
                : ("other", teams[random.Next(teams.Length)]);
            var values = new Dictionary<string, string>
            {
                ["component"] = component,
                ["summary"] = $"{component} routine problem report {random.Next(10000, 99999)}",
                ["team"] = team,
            };
            return new SettledDocument($"t{i}", values, DateTimeOffset.UnixEpoch.AddMinutes(i));
        }

        var history = Enumerable.Range(1, 400).Select(Request).ToList();
        var asked = Enumerable.Range(401, 400).Select(Request).Where(d => d.Values["component"] == "other").ToList();

        var separate = (
            Key: ThresholdSelection.SelectKeyThreshold(new FieldMemory(), form, "team", history, 0.8, 10).Chosen,
            Memory: (await ThresholdSelection.SelectAsync(new LexicalMemory(), form, "team", history, 0.8, 10, Ct)).Chosen);
        var layered = await ThresholdSelection.SelectLayersAsync(new FieldMemory(), new LexicalMemory(), form, "team", history, 0.8, 10, Ct);

        separate.Key.Should().NotBeNull(); // the named components decide the team
        separate.Memory.Should().NotBeNull(); // every lookup together clears the target
        layered.Key.Chosen.Should().Be(separate.Key); // the key layer comes first, on every lookup, either way
        layered.Memory.Chosen.Should().BeNull(); // what the key leaves never does
        layered.Memory.MostPrecise!.Precision.Should().BeLessThan(0.8); // and the replay says by how much

        async Task<(int Answered, int Right)> SimilarAnswers(double? keyThreshold, double? memoryThreshold)
        {
            var configured = new FormDefinition("ticket", [.. form.Fields.Select(f => f.Name == "team" ? f with { KeyThreshold = keyThreshold, MemoryThreshold = memoryThreshold } : f)], form.Language);
            var resolver = new FormResolver(new FieldMemory(), new LexicalMemory());
            await resolver.RebuildAsync(configured, history, Ct);
            var (answered, right) = (0, 0);
            foreach (var document in asked)
            {
                var team = (await resolver.SuggestAsync(configured, document.DocumentId, Without(document, "team"), Ct)).Single();
                if (team.Answered && team.Source == FieldSource.SimilarDocument)
                {
                    answered++;
                    right += team.Candidates[0].Value == document.Values["team"] ? 1 : 0;
                }
            }

            return (answered, right);
        }

        var before = await SimilarAnswers(separate.Key?.Threshold, separate.Memory!.Threshold);
        before.Answered.Should().BeGreaterThan(10);
        ((double)before.Right / before.Answered).Should().BeLessThan(0.5); // the promise was 0.8
        (await SimilarAnswers(layered.Key.Chosen?.Threshold, layered.Memory.Chosen?.Threshold)).Answered.Should().Be(0);

        // Ten neighbours of a catch-all request disagree, so their vote is not trusted even on a threshold chosen on every lookup.
        form = new FormDefinition("ticket", [.. form.Fields.Select(f => f with { SimilarDocumentVotes = 10 })], form.Language);
        var voted = (await ThresholdSelection.SelectAsync(new LexicalMemory(), form, "team", history, 0.8, 10, Ct)).Chosen;
        voted.Should().NotBeNull();
        (await SimilarAnswers(separate.Key?.Threshold, voted!.Threshold)).Answered.Should().BeLessThan(before.Answered / 5);
    }

    [Fact]
    public async Task A_document_settled_again_is_not_its_own_evidence_in_the_replay()
    {
        // Every request is saved twice under its own id, unchanged, and its team is drawn at random: nothing but the
        // request itself predicts it. A suggestion for a saved document passes over its saved version, so neither
        // layer can answer these well, and the replay must not find otherwise by asking the second save about the first.
        var random = new Random(5);
        string[] teams = ["network", "facilities", "security", "accounts", "hardware"];
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged),
            ],
            PromptLanguage.English);
        var history = Enumerable.Range(1, 100).SelectMany(i =>
        {
            var values = new Dictionary<string, string> { ["summary"] = $"request {i} about item {i * 7}", ["team"] = teams[random.Next(teams.Length)] };
            return new[]
            {
                new SettledDocument($"r{i}", values, DateTimeOffset.UnixEpoch.AddMinutes(2 * i)),
                new SettledDocument($"r{i}", values, DateTimeOffset.UnixEpoch.AddMinutes((2 * i) + 1)),
            };
        }).ToList();

        var key = ThresholdSelection.SelectKeyThreshold(new FieldMemory(), form, "team", history, 0.8, 10);
        var similar = await ThresholdSelection.SelectAsync(new LexicalMemory(), form, "team", history, 0.8, 10, Ct);

        key.Chosen.Should().BeNull(); // no request's summary is seen twice under different ids
        similar.Chosen.Should().BeNull();
        similar.MostPrecise!.Precision.Should().BeLessThan(0.5); // the nearest other request, not the saved version
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
            (false, FieldSource.SettledFieldMemory, new FieldCandidate("facilities", 0.5, FieldSource.SettledFieldMemory, "component: printer", Trusted: false)));
        model.Asked.Should().Be(0); // a value the document's own keys back, however weakly, beats a model's guess
        (sink.Traces[weak.TraceId].Outcome!.Mode, sink.Traces[weak.TraceId].Outcome!.Output).Should().Be(("abstain", null));
        (sink.Traces[strong.TraceId].Outcome!.Mode, sink.Traces[strong.TraceId].Outcome!.Output).Should().Be(("field_memory", "network"));
    }

    [Fact]
    public async Task The_nearest_document_below_the_similarity_threshold_is_the_last_guess()
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

        // Below its threshold, the nearest document is a weaker guess than the field's most frequent value.
        team.Answered.Should().BeFalse();
        team.Candidates.Select(c => (c.Value, c.Source, c.Evidence, c.Trusted)).Should().Equal(
            ("network", FieldSource.SettledFieldMemory, null, false),
            ("facilities", FieldSource.SimilarDocument, "d1", false));
    }

    [Fact]
    public async Task Guesses_come_as_a_weaker_key_then_the_most_frequent_value_then_the_nearest_document()
    {
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("site", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged) { DependsOn = ["summary", "site"], KeyThreshold = 0.99, MemoryThreshold = 0.99 },
            ],
            PromptLanguage.English);
        SettledDocument Doc(int i, string summary, string site, string team) =>
            new($"d{i}", new Dictionary<string, string> { ["summary"] = summary, ["site"] = site, ["team"] = team }, DateTimeOffset.UnixEpoch.AddMinutes(i));
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory());
        await resolver.RebuildAsync(form,
        [
            Doc(1, "printer on floor 3 is jammed", "north", "facilities"),
            Doc(2, "vpn drops", "south", "network"),
            Doc(3, "wifi drops", "south", "network"),
            Doc(4, "badge reader is dead", "east", "security"),
        ], Ct);

        var team = (await resolver.SuggestAsync(form, "d9", new Dictionary<string, string> { ["summary"] = "printer on floor 2 is jammed", ["site"] = "east" }, Ct)).Single();

        team.Answered.Should().BeFalse();
        team.Candidates.Select(c => (c.Value, c.Source, c.Trusted)).Should().Equal(
            ("security", FieldSource.SettledFieldMemory, false), // under the weak key site: east
            ("network", FieldSource.SettledFieldMemory, false), // the most frequent value
            ("facilities", FieldSource.SimilarDocument, false)); // the nearest document, below its threshold
    }

    [Fact]
    public async Task A_field_without_a_similarity_threshold_still_shows_its_similar_documents_and_guesses_last()
    {
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged),
            ],
            PromptLanguage.English);
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory(), similarDocumentCount: 2);
        await resolver.RebuildAsync(form,
        [
            new SettledDocument("d1", new Dictionary<string, string> { ["summary"] = "printer on floor 3 is jammed", ["team"] = "facilities" }, DateTimeOffset.UnixEpoch.AddMinutes(1)),
            new SettledDocument("d2", new Dictionary<string, string> { ["summary"] = "vpn drops", ["team"] = "network" }, DateTimeOffset.UnixEpoch.AddMinutes(2)),
            new SettledDocument("d3", new Dictionary<string, string> { ["summary"] = "wifi drops", ["team"] = "network" }, DateTimeOffset.UnixEpoch.AddMinutes(3)),
        ], Ct);

        // A near-identical document makes no promise without a threshold, but is still the evidence a person can see.
        var team = (await resolver.SuggestAsync(form, "d4", new Dictionary<string, string> { ["summary"] = "printer on floor 3 is jammed again" }, Ct)).Single();

        team.Answered.Should().BeFalse();
        team.Candidates.Select(c => (c.Value, c.Source, c.Trusted)).Should().Equal(
            ("network", FieldSource.SettledFieldMemory, false),
            ("facilities", FieldSource.SimilarDocument, false));
        team.SimilarDocuments.Select(m => m.Source).Should().StartWith("d1");
    }

    [Fact]
    public async Task A_suggestion_reports_the_similar_documents_its_candidate_rests_on()
    {
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged) { MemoryThreshold = 0.3 },
            ],
            PromptLanguage.English);
        SettledDocument Doc(int i, string summary, string team) =>
            new($"d{i}", new Dictionary<string, string> { ["summary"] = summary, ["team"] = team }, DateTimeOffset.UnixEpoch.AddMinutes(i));
        var history = new[]
        {
            Doc(1, "printer on floor 3 is jammed", "facilities"),
            Doc(2, "printer on floor 4 is jammed again", "hardware"),
            Doc(3, "vpn drops every ten minutes", "network"),
            Doc(4, "the printer on floor 5 is jammed", "facilities"),
        };
        var asked = new Dictionary<string, string> { ["summary"] = "printer on floor 2 is jammed" };

        var plain = new FormResolver(new FieldMemory(), new LexicalMemory());
        await plain.RebuildAsync(form, history, Ct);
        (await plain.SuggestAsync(form, "d9", asked, Ct)).Single().SimilarDocuments.Should().BeEmpty(); // not asked for

        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory(), similarDocumentCount: 3);
        await resolver.RebuildAsync(form, history, Ct);
        var team = (await resolver.SuggestAsync(form, "d9", asked, Ct)).Single();

        team.SimilarDocuments.Should().HaveCount(3);
        team.SimilarDocuments[0].Source.Should().Be(team.Candidates[0].Evidence); // the candidate's own document first
        team.SimilarDocuments.Select(m => m.Similarity).Should().BeInDescendingOrder();
        team.SimilarDocuments.Select(m => m.Answer).Should().Contain("hardware"); // the neighbours need not agree
    }

    [Fact]
    public async Task A_saved_document_is_passed_over_for_the_next_similar_one()
    {
        var form = new FormDefinition(
            "ticket",
            [
                new FieldDefinition("summary", FieldRole.Observed),
                new FieldDefinition("team", FieldRole.Judged) { MemoryThreshold = 0.5 },
            ],
            PromptLanguage.English);
        var resolver = new FormResolver(new FieldMemory(), new LexicalMemory(), similarDocumentCount: 2);
        await resolver.RebuildAsync(form,
        [
            new SettledDocument("d1", new Dictionary<string, string> { ["summary"] = "printer on floor 3 is jammed", ["team"] = "facilities" }, DateTimeOffset.UnixEpoch.AddMinutes(1)),
            new SettledDocument("d2", new Dictionary<string, string> { ["summary"] = "printer on floor 4 is jammed", ["team"] = "facilities" }, DateTimeOffset.UnixEpoch.AddMinutes(2)),
        ], Ct);

        // Editing d1: its own saved version is the most similar document, and is passed over.
        var team = (await resolver.SuggestAsync(form, "d1", new Dictionary<string, string> { ["summary"] = "printer on floor 3 is jammed" }, Ct)).Single();

        (team.Answered, team.Source, team.Candidates[0].Evidence).Should().Be((true, FieldSource.SimilarDocument, "d2"));
        team.SimilarDocuments.Select(m => m.Source).Should().Equal("d2");
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

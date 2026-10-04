using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Gil.Forms;
using Gil.Llm;
using Gil.Memory;

namespace Gil.Tests.Forms;

/// <summary>
/// A form's judged fields are remembered by evidence text, one task each: a text is embedded once however many fields read
/// it, and a rebuild embeds its documents' texts in batches — leaving memory as remembering them one by one would.
/// </summary>
public sealed class SharedEvidenceEmbeddingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    // Judged fields that are not evidence for one another: every field reads the observed fields only.
    private static readonly FormDefinition Separate = Form(useJudgedAsEvidence: false);

    // The default: a judged field's settled value is evidence for the others, so each field reads a different text.
    private static readonly FormDefinition Together = Form(useJudgedAsEvidence: true);

    private static FormDefinition Form(bool useJudgedAsEvidence) => new(
        "session",
        [
            new FieldDefinition("narrative", FieldRole.Observed),
            new FieldDefinition("grade", FieldRole.Observed),
            new FieldDefinition("topic0", FieldRole.Judged) { UseAsEvidence = useJudgedAsEvidence },
            new FieldDefinition("topic1", FieldRole.Judged) { UseAsEvidence = useJudgedAsEvidence },
            new FieldDefinition("topic2", FieldRole.Judged) { UseAsEvidence = useJudgedAsEvidence },
        ],
        PromptLanguage.English);

    private static List<SettledDocument> Documents(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new SettledDocument(
            $"d{i}",
            new Dictionary<string, string>
            {
                ["narrative"] = $"session note {i % 37}",
                ["grade"] = $"{i % 3}",
                ["topic0"] = $"a{i % 4}",
                ["topic1"] = $"b{i % 5}",
                ["topic2"] = $"c{i % 2}",
            },
            Monday.AddMinutes(i)))];

    [Fact]
    public async Task A_rebuild_embeds_each_distinct_evidence_text_once_in_batches()
    {
        var model = new Hashed();
        var resolver = new FormResolver(new FieldMemory(), new EmbeddingMemory(new EmbeddingRecorder(model, new EnergyModel(1, 0, 0, 0))));

        await resolver.RebuildAsync(Separate, Documents(200), Ct);

        // 200 documents × 3 fields read 111 distinct texts (37 notes × 3 grades), embedded once each — in three calls of at
        // most 64 rather than 600 calls of one: the first 85 documents bring 85 texts, the next 85 bring the other 26.
        model.Texts.Should().HaveCount(111).And.OnlyHaveUniqueItems();
        model.Batches.Should().Equal(64, 21, 26);
    }

    [Fact]
    public async Task Fields_that_read_different_texts_still_embed_them_together()
    {
        var model = new Hashed();
        var resolver = new FormResolver(new FieldMemory(), new EmbeddingMemory(new EmbeddingRecorder(model, new EnergyModel(1, 0, 0, 0))));

        await resolver.RebuildAsync(Together, Documents(100), Ct);

        model.Texts.Should().OnlyHaveUniqueItems();
        model.Batches.Should().OnlyContain(b => b > 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Memory_after_a_batched_rebuild_is_what_remembering_one_by_one_leaves(bool useJudgedAsEvidence)
    {
        var form = Form(useJudgedAsEvidence);
        var documents = Documents(150);
        var batched = new EmbeddingMemory(new EmbeddingRecorder(new Hashed(), new EnergyModel(1, 0, 0, 0)));
        var singly = new EmbeddingMemory(new EmbeddingRecorder(new Hashed(), new EnergyModel(1, 0, 0, 0)));

        await new FormResolver(new FieldMemory(), batched).RebuildAsync(form, documents, Ct);
        await new FormResolver(new FieldMemory(), new OneByOne(singly)).RebuildAsync(form, documents, Ct);

        foreach (var field in new[] { "topic0", "topic1", "topic2" })
        {
            var task = FormResolver.TaskName(form, field);
            batched.Count(task).Should().Be(singly.Count(task));
            foreach (var probe in documents.Take(40))
            {
                var state = FormResolver.Evidence(form, field, probe.Values);
                var (want, _) = await singly.NearestAsync(task, state, 5, "q", Ct);
                var (got, _) = await batched.NearestAsync(task, state, 5, "q", Ct);
                got.Should().BeEquivalentTo(want, o => o.WithStrictOrdering());
            }
        }
    }

    [Fact]
    public async Task Suggesting_several_fields_from_the_same_evidence_embeds_it_once()
    {
        var model = new Hashed();
        var resolver = new FormResolver(
            new FieldMemory(), new EmbeddingMemory(new EmbeddingRecorder(model, new EnergyModel(1, 0, 0, 0))), similarDocumentCount: 3);
        await resolver.RebuildAsync(Separate, Documents(20), Ct);
        var before = model.Texts.Count;

        var suggestions = await resolver.SuggestAsync(
            Separate, "new", new Dictionary<string, string> { ["narrative"] = "a note never seen", ["grade"] = "1" }, Ct);

        suggestions.Should().HaveCount(3);
        model.Texts.Skip(before).Should().Equal("narrative: a note never seen\ngrade: 1");
    }

    [Fact]
    public async Task A_circuit_breaker_passes_the_rebuild_s_batches_through()
    {
        var model = new Hashed();
        var memory = new CircuitBreakingMemory(new EmbeddingMemory(new EmbeddingRecorder(model, new EnergyModel(1, 0, 0, 0))), TimeSpan.FromSeconds(30));

        await new FormResolver(new FieldMemory(), memory).RebuildAsync(Separate, Documents(200), Ct);

        model.Batches.Should().Equal(64, 21, 26);
    }

    /// <summary>A vector from the text's hash: the same text always embeds the same, alone or in a batch.</summary>
    private sealed class Hashed : IEmbeddingModel
    {
        public List<string> Texts { get; } = [];

        public List<int> Batches { get; } = [];

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Texts.AddRange(texts);
            Batches.Add(texts.Count);
            var vectors = texts.Select(t => SHA256.HashData(Encoding.UTF8.GetBytes(t)).Take(8).Select(b => b / 255f - 0.5f).ToArray()).ToList();
            return Task.FromResult(new EmbeddingResult(vectors, "e", texts.Count, 1, "{}"));
        }
    }

    /// <summary>The memory without its batches: what a rebuild leaves when every write embeds on its own.</summary>
    private sealed class OneByOne(IMemory inner) : IMemory
    {
        public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default) =>
            inner.LookupAsync(task, state, traceId, cancellationToken);

        public Task<double> RememberAsync(string task, string key, string state, string answer, string traceId, CancellationToken cancellationToken = default) =>
            inner.RememberAsync(task, key, state, answer, traceId, cancellationToken);

        public void Forget(string task, string key) => inner.Forget(task, key);
    }
}

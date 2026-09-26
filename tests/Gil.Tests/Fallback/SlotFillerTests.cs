using AwesomeAssertions;
using Gil.Fallback;
using Gil.Llm;

namespace Gil.Tests.Fallback;

public sealed class SlotFillerTests
{
    private static readonly Habit Received = new()
    {
        Id = "received",
        Kind = HabitKind.Template,
        Label = "received",
        Description = "acknowledge",
        Template = "{item} received by {team}.",
        Slots = [new Slot("item", "what was sent"), new Slot("team", "fixed", Fixed: "HR")],
    };

    [Fact]
    public async Task Only_the_open_blanks_are_generated()
    {
        var model = new ScriptedModel("""{"item": "the form"}""");

        var filled = await new SlotFiller(new CallRecorder(model, new EnergyModel(1, 0, 0, 0))).FillAsync("I sent the form", Received, PromptLanguage.Korean, "t", TestContext.Current.CancellationToken);

        (filled.Output, filled.FailedReason).Should().Be(("the form received by HR.", null));
        model.Requests.Should().ContainSingle().Which.Messages[1].Content.Should().Contain("\"item\"").And.NotContain("\"team\"");
    }

    [Fact]
    public async Task A_missing_blank_is_asked_for_again_and_then_given_up()
    {
        var model = new ScriptedModel("""{"other": "x"}""");

        var filled = await new SlotFiller(new CallRecorder(model, new EnergyModel(1, 0, 0, 0))).FillAsync("I sent the form", Received, PromptLanguage.Korean, "t", TestContext.Current.CancellationToken);

        filled.Output.Should().BeNull();
        filled.FailedReason.Should().Contain("item");
        model.Requests.Should().HaveCount(2);
        model.Requests[1].Messages[1].Content.Should().Contain("빠진 빈칸: [item]");
    }

    private sealed class ScriptedModel(params string[] answers) : IChatModel
    {
        private int _next;

        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var answer = answers[Math.Min(_next++, answers.Length - 1)];
            return Task.FromResult(new ChatResult
            {
                Model = "m",
                Content = answer,
                PromptTokens = 10,
                CachedTokens = 0,
                CompletionTokens = 1,
                LatencyMs = 1,
                RawResponse = "{}",
            });
        }
    }
}

using System.Text.Json.Nodes;
using AwesomeAssertions;
using Gil.Fallback;
using Gil.IronHive;
using Gil.Judge;
using Gil.Llm;
using Gil.Ontology;
using Gil.Telemetry;
using Gil.Traverse;

namespace Gil.Tests;

public sealed class ResolverLiveTests
{
    [Fact]
    public async Task Resolves_requests_end_to_end_against_a_live_server()
    {
        // Opt-in: GIL_LIVE_BASE_URL, GIL_LIVE_API_KEY, GIL_LIVE_MODEL; set GIL_LIVE_DISABLE_THINKING=1 for servers
        // whose chat template reasons by default (without it a one-token judgment returns no label).
        var baseUrl = Environment.GetEnvironmentVariable("GIL_LIVE_BASE_URL");
        if (baseUrl is null)
        {
            Assert.Skip("GIL_LIVE_BASE_URL is not set");
        }

        var extra = Environment.GetEnvironmentVariable("GIL_LIVE_DISABLE_THINKING") == "1"
            ? new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } }
            : null;
        using var wire = new SentBodies(new SocketsHttpHandler());
        using var model = IronHiveChatModel.OpenAICompatible(new OpenAICompatibleOptions
        {
            BaseUrl = new Uri(baseUrl.TrimEnd('/') + "/"),
            ApiKey = Environment.GetEnvironmentVariable("GIL_LIVE_API_KEY") ?? "",
            Model = Environment.GetEnvironmentVariable("GIL_LIVE_MODEL") ?? "",
            Timeout = TimeSpan.FromMinutes(3),
            ExtraBody = extra,
        }, wire);
        var directory = Directory.CreateTempSubdirectory("gil-live-").FullName;
        using var store = new SqliteTelemetryStore(Path.Combine(directory, "live.sqlite"));
        var recorder = new CallRecorder(model, new EnergyModel(0, 1, 0.1, 4), store);
        var resolver = new Resolver(
            new GreedyTraverser(new SingleTokenJudge(recorder, new SingleTokenJudgeOptions { OrderSeed = 1 })),
            new FallbackGenerator(recorder),
            new SlotFiller(recorder),
            store);
        var tree = OntologyYaml.Parse("""
            id: root
            children:
              - id: alarm
                label: alarm
                description: setting, checking or removing alarms
                options:
                  - id: set
                    kind: template
                    label: set alarm
                    description: wake me up at seven
                    template: "alarm_set at {time}"
                    slots: [{name: time, instruction: the wake-up time}]
                  - {id: remove, kind: answer, label: remove alarm, description: cancel my morning alarm, text: alarm_remove}
              - id: weather
                label: weather
                description: forecasts and current conditions
                options:
                  - {id: query, kind: answer, label: weather query, description: will it rain tomorrow, text: weather_query}
            """).Root;
        var task = new TaskDefinition("live", new TreeAnswerContract(tree), tree, new TaskPolicy { Thresholds = new([0.8], 0.7), FallbackScope = FallbackScope.Path }, PromptLanguage.Korean);

        var alarm = await resolver.ResolveAsync(task, "please wake me up at 6 tomorrow", cancellationToken: TestContext.Current.CancellationToken);
        var weather = await resolver.ResolveAsync(task, "is it going to rain this afternoon?", cancellationToken: TestContext.Current.CancellationToken);

        // The alarm is a template habit: its blank is filled by a live slot-filling call.
        alarm.Mode.Should().Be("habit/template");
        alarm.Output.Should().StartWith("alarm_set at ");
        weather.Output.Should().Be("weather_query");
        alarm.Path.Should().NotBeEmpty();
        // Every role — judgments and slot filling alike — carries the setting made once on the endpoint.
        wire.Bodies.Should().NotBeEmpty().And.AllSatisfy(body =>
            JsonNode.Parse(body)!["chat_template_kwargs"]?.ToJsonString().Should().Be(extra?["chat_template_kwargs"]?.ToJsonString()));
    }

    private sealed class SentBodies(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

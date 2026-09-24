using System.Text.Json.Nodes;
using AwesomeAssertions;
using Gil.Fallback;
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
        using var http = new HttpClient();
        var model = new OpenAICompatibleChatModel(http, new OpenAICompatibleOptions
        {
            BaseUrl = new Uri(baseUrl.TrimEnd('/') + "/"),
            ApiKey = Environment.GetEnvironmentVariable("GIL_LIVE_API_KEY") ?? "",
            Model = Environment.GetEnvironmentVariable("GIL_LIVE_MODEL") ?? "",
            Timeout = TimeSpan.FromMinutes(3),
        });
        var directory = Directory.CreateTempSubdirectory("gil-live-").FullName;
        using var store = new SqliteTelemetryStore(Path.Combine(directory, "live.sqlite"));
        var recorder = new CallRecorder(model, new EnergyModel(0, 1, 0.1, 4), store);
        var resolver = new Resolver(
            new GreedyTraverser(new SingleTokenJudge(recorder, new SingleTokenJudgeOptions { OrderSeed = 1, ExtraBody = extra })),
            new FallbackGenerator(recorder, extraBody: extra),
            new SlotFiller(recorder),
            store);
        var tree = OntologyYaml.Parse("""
            id: root
            children:
              - id: alarm
                label: alarm
                description: setting, checking or removing alarms
                options:
                  - {id: set, kind: answer, label: set alarm, description: wake me up at seven, text: alarm_set}
                  - {id: remove, kind: answer, label: remove alarm, description: cancel my morning alarm, text: alarm_remove}
              - id: weather
                label: weather
                description: forecasts and current conditions
                options:
                  - {id: query, kind: answer, label: weather query, description: will it rain tomorrow, text: weather_query}
            """).Root;
        var task = new TaskDefinition("live", new TreeAnswerContract(tree), tree, new TaskPolicy { Thresholds = new([0.8], 0.7), FallbackScope = FallbackScope.Path });

        var alarm = await resolver.ResolveAsync(task, "please wake me up at 6 tomorrow", cancellationToken: TestContext.Current.CancellationToken);
        var weather = await resolver.ResolveAsync(task, "is it going to rain this afternoon?", cancellationToken: TestContext.Current.CancellationToken);

        alarm.Output.Should().Be("alarm_set");
        weather.Output.Should().Be("weather_query");
        alarm.Path.Should().NotBeEmpty();
    }
}

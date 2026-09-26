using System.Diagnostics;
using System.Text.Json.Nodes;
using IronHive.Abstractions.Messages;
using IronHive.Abstractions.Messages.Content;
using IronHive.Providers.OpenAI;
using IronHive.Providers.OpenAI.Compatible;
using IronHive.Providers.OpenAI.Compatible.ChatCompletion;

namespace Gil.IronHive;

/// <summary>
/// Chat completions through an IronHive <see cref="IMessageGenerator"/>, reading what judgments and cost accounting
/// need: first-token log-probabilities, cached prompt tokens and, when the server reports them, its processing times.
/// </summary>
/// <remarks>
/// Leading <c>system</c> messages become the request's system prompt; the rest must be <c>user</c> or
/// <c>assistant</c>. <see cref="ChatResult.RawResponse"/> is the response body as received when the call went
/// through a client built by <see cref="OpenAICompatible"/>; with any other generator it is what IronHive reports
/// (model, usage and the provider's unmapped fields), since the body itself does not reach this adapter.
/// </remarks>
public sealed class IronHiveChatModel : IChatModel, IDisposable
{
    private readonly IMessageGenerator _generator;
    private readonly string _model;
    private readonly HttpClient? _ownedHttp;
    private readonly bool _ownsGenerator;
    private readonly JsonObject? _extraBody;

    /// <param name="generator">The provider to call; the caller keeps ownership.</param>
    /// <param name="model">The model to ask for.</param>
    /// <param name="extraBody">Provider fields sent with every request (for example a server's chat-template
    /// switches); a request's own <see cref="ChatRequest.ExtraBody"/> replaces a field of the same name.</param>
    public IronHiveChatModel(IMessageGenerator generator, string model, JsonObject? extraBody = null)
        : this(generator, model, extraBody, ownsGenerator: false, ownedHttp: null)
    {
    }

    private IronHiveChatModel(IMessageGenerator generator, string model, JsonObject? extraBody, bool ownsGenerator, HttpClient? ownedHttp)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrEmpty(model);
        (_generator, _model, _ownsGenerator, _ownedHttp) = (generator, model, ownsGenerator, ownedHttp);
        _extraBody = (JsonObject?)extraBody?.DeepClone();
    }

    /// <summary>
    /// A model on an OpenAI-compatible chat-completions server (OpenAI, and self-hosted servers such as llama.cpp and
    /// vLLM), sent with the shared retry rules of <see cref="OpenAICompatibleOptions"/>.
    /// </summary>
    /// <param name="options">Endpoint, model, retries and the fields every request carries.</param>
    /// <param name="transport">The handler that sends; a socket handler by default.</param>
    /// <param name="delay">Backoff between retries; the real clock by default.</param>
    public static IronHiveChatModel OpenAICompatible(
        OpenAICompatibleOptions options, HttpMessageHandler? transport = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        // IronHive uses an injected client as given and leaves it to its owner, which is this model.
        var http = OpenAICompatibleHttp.CreateClient(options, transport, delay);
        var generator = new ChatCompletionMessageGenerator(new OpenAIConfig
        {
            BaseUrl = new Uri(options.BaseUrl, "v1/").ToString(),
            ApiKey = options.ApiKey,
            HttpClient = http,
        })
        {
            // The long-standing name, which self-hosted servers accept; a server that does not know the name it gets drops the limit silently.
            TokenLimitParameter = TokenLimitParameter.MaxTokens,
        };
        return new IronHiveChatModel(generator, options.Model, options.ExtraBody, ownsGenerator: true, ownedHttp: http);
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generation = ToGeneration(request);
        var capture = ResponseCapture.Begin();
        var started = Stopwatch.GetTimestamp();
        var response = await _generator.GenerateMessageAsync(generation, cancellationToken).ConfigureAwait(false);
        var latencyMs = capture.LatencyMs ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var first = response.LogProbabilities is [var head, ..] ? head : null;
        var timings = response.ExtraBody?["timings"] as JsonObject;
        return new ChatResult
        {
            Model = response.Model ?? _model,
            Content = string.Concat(response.Message?.Content.OfType<TextMessageContent>().Select(c => c.Value) ?? []),
            PromptTokens = response.TokenUsage?.InputTokens ?? 0,
            CachedTokens = response.TokenUsage?.CachedInputTokens ?? 0,
            CompletionTokens = response.TokenUsage?.OutputTokens ?? 0,
            LatencyMs = latencyMs,
            FirstToken = first?.Token,
            TopLogprobs = first is null ? [] : [.. first.Alternatives.Select(a => new TokenLogprob(a.Token, a.LogProbability))],
            GpuPromptMs = Number(timings, "prompt_ms"),
            GpuPredictedMs = Number(timings, "predicted_ms"),
            RawResponse = capture.Body ?? Reported(response),
        };
    }

    public void Dispose()
    {
        if (_ownsGenerator)
        {
            _generator.Dispose();
        }

        _ownedHttp?.Dispose();
    }

    private MessageGenerationRequest ToGeneration(ChatRequest request)
    {
        var leading = request.Messages.TakeWhile(m => m.Role == "system").ToList();
        var messages = request.Messages.Skip(leading.Count).Select(m => new Message
        {
            Role = m.Role switch
            {
                "user" => MessageRole.User,
                "assistant" => MessageRole.Assistant,
                _ => throw new ArgumentException($"Unsupported message role '{m.Role}': system messages must come first.", nameof(request)),
            },
            Content = [new TextMessageContent { Value = m.Content }],
        }).ToList();
        if (leading.Count > 1)
        {
            throw new ArgumentException("At most one system message is supported.", nameof(request));
        }

        return new MessageGenerationRequest
        {
            Model = _model,
            System = leading.FirstOrDefault()?.Content,
            Messages = messages,
            MaxTokens = request.MaxTokens,
            Temperature = (float)request.Temperature,
            LogProbabilities = request.TopLogprobs is int top ? new LogProbabilityOptions { TopAlternatives = top } : null,
            ExtraBody = Merge(_extraBody, request.ExtraBody),
        };
    }

    /// <summary>The model's fields, with the request's replacing any of the same name; a fresh object either way.</summary>
    private static JsonObject? Merge(JsonObject? defaults, JsonObject? request)
    {
        if (defaults is null)
        {
            return (JsonObject?)request?.DeepClone();
        }

        var merged = (JsonObject)defaults.DeepClone();
        foreach (var (name, value) in request ?? [])
        {
            merged[name] = value?.DeepClone();
        }

        return merged;
    }

    private static double? Number(JsonObject? parent, string name) =>
        parent?[name] is JsonValue value && value.TryGetValue(out double number) ? number : null;

    private static string Reported(MessageResponse response) => new JsonObject
    {
        ["model"] = response.Model,
        ["usage"] = new JsonObject
        {
            ["prompt_tokens"] = response.TokenUsage?.InputTokens,
            ["cached_tokens"] = response.TokenUsage?.CachedInputTokens,
            ["completion_tokens"] = response.TokenUsage?.OutputTokens,
        },
        ["extra"] = response.ExtraBody?.DeepClone(),
    }.ToJsonString();
}

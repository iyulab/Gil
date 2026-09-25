using System.Diagnostics;
using System.Net;

namespace Gil.Llm;

/// <summary>Connection and retry settings for an OpenAI-compatible endpoint (chat completions or embeddings).</summary>
public sealed record OpenAICompatibleOptions
{
    /// <summary>The server root, without <c>/v1</c>.</summary>
    public required Uri BaseUrl { get; init; }

    public required string ApiKey { get; init; }
    public required string Model { get; init; }

    /// <summary>
    /// Attempts for failures the server did not process: refused connections, 429 and 5xx. A request that timed out
    /// may already be queued on a busy shared server, so it is retried only <see cref="ReadTimeoutRetries"/> times —
    /// resending eagerly would queue the same work twice and deepen the backlog.
    /// </summary>
    public int MaxAttempts { get; init; } = 4;

    public int ReadTimeoutRetries { get; init; } = 1;
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Per attempt; longer than a busy shared server's queueing time.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long to wait for a connection to open.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Builds the HTTP clients every OpenAI-compatible call goes through.</summary>
internal static class OpenAICompatibleHttp
{
    /// <param name="options">Endpoint and retries.</param>
    /// <param name="transport">The handler that sends; a socket handler by default. Tests pass a scripted one.</param>
    /// <param name="delay">Backoff between retries; the real clock by default.</param>
    public static HttpClient CreateClient(OpenAICompatibleOptions options, HttpMessageHandler? transport, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(options);
        var retry = new SharedServerRetryHandler(options, delay)
        {
            InnerHandler = transport ?? new SocketsHttpHandler { ConnectTimeout = options.ConnectTimeout },
        };

        // Deadlines are per attempt, in the handler; a client-wide timeout would cut across retries.
        return new HttpClient(retry) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }
}

/// <summary>
/// The retry rules every call shares: failures the server did not process (no response, 429, 5xx) are retried up to
/// <see cref="OpenAICompatibleOptions.MaxAttempts"/>; a request that ran past its deadline may already be queued, so
/// it is resent only <see cref="OpenAICompatibleOptions.ReadTimeoutRetries"/> times; client errors are not retried.
/// The body of the response that is finally returned is handed to the <see cref="ResponseCapture"/> of the calling flow.
/// </summary>
internal sealed class SharedServerRetryHandler(OpenAICompatibleOptions options, Func<TimeSpan, CancellationToken, Task>? delay) : DelegatingHandler
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            // Resent as is on every attempt.
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }

        var timeouts = 0;
        for (var attempt = 1; ; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.Timeout);
            try
            {
                var response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
                await response.Content.LoadIntoBufferAsync(deadline.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || !IsTransient(response.StatusCode) || attempt >= options.MaxAttempts)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                        ResponseCapture.Record(body, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }

                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException error) when (error.StatusCode is null && attempt < options.MaxAttempts)
            {
                // No response at all: the request never reached the server's queue.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeouts < options.ReadTimeoutRetries)
            {
                // Our own deadline, not the caller's: the server may be working on it already.
                timeouts++;
            }

            await _delay(options.Backoff * Math.Pow(2, attempt - 1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests || (int)status >= 500;
}

/// <summary>
/// Carries the response body as received, and the time its attempt took, from the HTTP handler back to the model call
/// that caused it — through clients (such as IronHive's) that parse the body and do not hand it on.
/// </summary>
internal static class ResponseCapture
{
    private static readonly AsyncLocal<Slot?> Current = new();

    /// <summary>Opens a slot for the calling flow; the handlers below it fill it in.</summary>
    public static Slot Begin() => Current.Value = new Slot();

    public static void Record(string body, double latencyMs)
    {
        if (Current.Value is { } slot)
        {
            (slot.Body, slot.LatencyMs) = (body, latencyMs);
        }
    }

    public sealed class Slot
    {
        public string? Body { get; set; }
        public double? LatencyMs { get; set; }
    }
}

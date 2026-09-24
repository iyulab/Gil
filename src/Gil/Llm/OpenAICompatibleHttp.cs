using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Gil.Llm;

/// <summary>
/// POSTs to an OpenAI-compatible endpoint with the retry rules every call shares: failures the server did not
/// process (no response, 429, 5xx) are retried up to <see cref="OpenAICompatibleOptions.MaxAttempts"/>; a request that
/// ran past its deadline may already be queued, so it is resent only <see cref="OpenAICompatibleOptions.ReadTimeoutRetries"/>
/// times; client errors are not retried.
/// </summary>
internal sealed class OpenAICompatibleHttp(HttpClient http, OpenAICompatibleOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public OpenAICompatibleOptions Options => options;

    public async Task<(string Body, double LatencyMs)> PostAsync(string path, string body, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(options.BaseUrl, path);
        var timeouts = 0;
        for (var attempt = 1; ; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.Timeout);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                using var response = await http.SendAsync(message, deadline.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return (text, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }

                if (!IsTransient(response.StatusCode) || attempt >= options.MaxAttempts)
                {
                    throw new HttpRequestException($"{path} failed: {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
                }
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

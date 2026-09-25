namespace Gil.Memory;

/// <summary>Thrown instead of calling a memory that failed moments ago.</summary>
public sealed class MemoryUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// A circuit breaker in front of any memory. Once a lookup or a write fails, calls are refused at once for
/// <c>cooldown</c> instead of paying the endpoint's retries again; the first call after it is let through as a trial,
/// and closes the circuit if it succeeds.
/// </summary>
/// <remarks>
/// Refused calls throw <see cref="MemoryUnavailableException"/>, so a task whose <see cref="TaskPolicy.MemoryFailure"/>
/// is <see cref="MemoryFailure.Miss"/> treats them like the failure itself: a miss, recorded in the trace's recall.
/// Without the breaker, a dead memory endpoint adds its whole retry budget to every request before the miss.
/// </remarks>
/// <param name="inner">The memory to protect.</param>
/// <param name="cooldown">How long calls are refused after a failure.</param>
/// <param name="time">The clock; the system clock by default.</param>
public sealed class CircuitBreakingMemory(IMemory inner, TimeSpan cooldown, TimeProvider? time = null) : IMemory
{
    private readonly IMemory _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TimeSpan _cooldown = cooldown >= TimeSpan.Zero ? cooldown : throw new ArgumentOutOfRangeException(nameof(cooldown), cooldown, "The cooldown cannot be negative.");
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private (DateTimeOffset Until, Exception Cause)? _open;
    private bool _trialRunning;

    public async Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default)
    {
        var trial = Enter();
        try
        {
            var result = await _inner.LookupAsync(task, state, traceId, cancellationToken).ConfigureAwait(false);
            Succeeded();
            return result;
        }
        catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            Failed(error);
            throw;
        }
        finally
        {
            EndTrial(trial);
        }
    }

    public async Task<double> RememberAsync(string task, string traceId, string state, string answer, CancellationToken cancellationToken = default)
    {
        var trial = Enter();
        try
        {
            var energy = await _inner.RememberAsync(task, traceId, state, answer, cancellationToken).ConfigureAwait(false);
            Succeeded();
            return energy;
        }
        catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            Failed(error);
            throw;
        }
        finally
        {
            EndTrial(trial);
        }
    }

    /// <summary>Passed through: forgetting a wrong answer is never refused.</summary>
    public void Forget(string task, string traceId) => _inner.Forget(task, traceId);

    /// <summary>Lets the call through, or throws while the circuit is open. True when the call is the trial.</summary>
    private bool Enter()
    {
        lock (_gate)
        {
            if (_open is not { } open)
            {
                return false;
            }

            var (until, cause) = open;

            if (_time.GetUtcNow() < until || _trialRunning)
            {
                throw new MemoryUnavailableException($"memory skipped: it failed within the last {_cooldown.TotalSeconds:0.#} s ({cause.GetType().Name}: {cause.Message})", cause);
            }

            _trialRunning = true; // one trial at a time; the others are refused until it settles
            return true;
        }
    }

    private void Succeeded()
    {
        lock (_gate)
        {
            _open = null;
        }
    }

    private void Failed(Exception error)
    {
        lock (_gate)
        {
            _open = (_time.GetUtcNow() + _cooldown, error);
        }
    }

    private void EndTrial(bool trial)
    {
        if (!trial)
        {
            return;
        }

        lock (_gate)
        {
            _trialRunning = false;
        }
    }
}

namespace Gil;

/// <summary>
/// What a task's log says about it: observed accuracy per mode, how many requests habits and memory handled, and the
/// cost per request over time. Accuracy is reported, not promised — it is measured on the requests that got feedback.
/// </summary>
/// <param name="Task">The task.</param>
/// <param name="Requests">Closed requests.</param>
/// <param name="Judged">Requests with a verdict.</param>
/// <param name="Modes">Per mode, most frequent first.</param>
/// <param name="HabitRate">Share of requests a habit answered (any <c>habit/</c> mode).</param>
/// <param name="MemoryRate">Share of requests memory answered.</param>
/// <param name="Cost">Mean cost per request in consecutive windows, oldest first — the trend.</param>
public sealed record TaskStats(
    string Task,
    int Requests,
    int Judged,
    IReadOnlyList<ModeStats> Modes,
    double HabitRate,
    double MemoryRate,
    IReadOnlyList<CostWindow> Cost);

/// <summary>One mode's requests and its observed accuracy with a 95% Wilson interval; null accuracy without verdicts.</summary>
public sealed record ModeStats(string Mode, int Requests, int Judged, int Correct, double? Accuracy, double? Low, double? High);

/// <summary>A run of consecutive requests, numbered from 0 in the order they arrived, and their mean cost.</summary>
public sealed record CostWindow(int First, int Requests, double MeanCost);

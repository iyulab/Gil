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
/// <param name="MemoryFailures">Requests whose memory lookup failed and went on as a miss — an operational alarm:
/// while memory is down, every request pays for the tree.</param>
/// <param name="Cost">Mean cost per request in consecutive windows, oldest first — the trend.</param>
/// <param name="Misroutes">Requests the tree sent to the wrong category; null unless the tree was given.</param>
public sealed record TaskStats(
    string Task,
    int Requests,
    int Judged,
    IReadOnlyList<ModeStats> Modes,
    double HabitRate,
    double MemoryRate,
    int MemoryFailures,
    IReadOnlyList<CostWindow> Cost,
    MisrouteStats? Misroutes = null);

/// <summary>
/// How often the tree routed a request into a category its confirmed answer is not in. A request is routed to the last
/// node on its path; it is misrouted when the confirmed answer — the output when feedback said correct, the correction
/// when it said wrong — is an answer in the tree but not below that node. Requests memory answered, and those whose
/// answer the tree does not hold, are not counted. Stopping at the root never counts: the tree committed to nothing.
/// </summary>
/// <param name="Judged">Requests whose confirmed answer is in the tree and that have a path.</param>
/// <param name="Misrouted">Of those, the ones whose answer is outside the category they were routed to.</param>
/// <param name="Rate">Misrouted ÷ judged; null without judged requests.</param>
/// <param name="Low">Lower end of the rate's 95% Wilson interval.</param>
/// <param name="High">Upper end of the rate's 95% Wilson interval.</param>
public sealed record MisrouteStats(int Judged, int Misrouted, double? Rate, double? Low, double? High);

/// <summary>One mode's requests and its observed accuracy with a 95% Wilson interval; null accuracy without verdicts.</summary>
public sealed record ModeStats(string Mode, int Requests, int Judged, int Correct, double? Accuracy, double? Low, double? High);

/// <summary>A run of consecutive requests, numbered from 0 in the order they arrived, and their mean cost.</summary>
public sealed record CostWindow(int First, int Requests, double MeanCost);

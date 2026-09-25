namespace Gil;

/// <summary>When each habit was last used, counted in requests rather than wall-clock time.</summary>
/// <param name="LastIndex">Per habit id, the position among the task's requests where it last answered.</param>
/// <param name="Total">How many requests the task has had.</param>
public sealed record HabitUsage(IReadOnlyDictionary<string, int> LastIndex, int Total);

/// <summary>Where habit usage is read from: a task's answered requests, in arrival order.</summary>
public interface IHabitUsageSource
{
    HabitUsage Usage(string task);
}

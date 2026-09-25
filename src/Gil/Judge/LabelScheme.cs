namespace Gil.Judge;

/// <summary>
/// The symbols candidates are labelled with. Each must be a single token, which is why digits stop at nine: "10"
/// starts with the token "1" and the two candidates' probabilities would mix. The first symbol of the pool
/// belongs to "none of these", keeping it in the same symbol family as the candidates.
/// </summary>
public static class LabelScheme
{
    public const string Digits = "digits";
    public const string Letters = "letters";

    private static readonly Dictionary<string, string> Pools = new()
    {
        [Digits] = "0123456789",
        [Letters] = "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
    };

    /// <summary>The default label of "none of these": the pool's first symbol.</summary>
    public static string NoneLabel(string scheme) => Pool(scheme)[..1];

    public static bool IsKnown(string scheme) => Pools.ContainsKey(scheme);

    /// <summary>How many candidates a node can hold under this scheme.</summary>
    public static int Capacity(string scheme) => Pool(scheme).Length - 1;

    /// <summary>Labels for <paramref name="count"/> candidates, skipping the one "none of these" uses.</summary>
    public static IReadOnlyList<string> Labels(string scheme, int count, string? noneLabel = null)
    {
        var none = noneLabel ?? NoneLabel(scheme);
        var available = Pool(scheme).Select(c => c.ToString()).Where(s => s != none).ToList();
        if (count > available.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, $"the {scheme} scheme labels at most {available.Count} candidates — split the node or use another scheme");
        }

        return available.Take(count).ToList();
    }

    private static string Pool(string scheme) =>
        Pools.TryGetValue(scheme, out var pool) ? pool : throw new ArgumentException($"unknown label scheme {scheme}", nameof(scheme));
}

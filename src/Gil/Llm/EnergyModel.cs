namespace Gil.Llm;

/// <summary>
/// The cost of one call in the backend's own unit — GPU milliseconds for a self-hosted server, currency for an API:
/// <c>fixed + perFreshPromptToken·fresh + perCachedToken·cached + perOutputToken·output</c>.
/// </summary>
/// <remarks>
/// For a self-hosted server, fit the coefficients to the reported processing times; the intercept is the per-call
/// fixed cost, which unitless token weightings leave out and which is exactly what separates "fewer long calls" from
/// "more short calls". For an API, the published prices are the coefficients and the intercept is zero.
/// </remarks>
public sealed record EnergyModel(double Fixed, double PerFreshPromptToken, double PerCachedToken, double PerOutputToken)
{
    public double Of(int promptTokens, int cachedTokens, int completionTokens)
    {
        var fresh = Math.Max(promptTokens - cachedTokens, 0);
        return Fixed + (PerFreshPromptToken * fresh) + (PerCachedToken * cachedTokens) + (PerOutputToken * completionTokens);
    }
}

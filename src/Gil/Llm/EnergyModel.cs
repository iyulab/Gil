namespace Gil.Llm;

/// <summary>
/// The cost of one call in the backend's own unit — GPU milliseconds for a self-hosted server, currency for an API:
/// <c>fixed + perFreshPromptToken·fresh + perCachedToken·cached + perOutputToken·output</c>.
/// </summary>
/// <remarks>
/// For a self-hosted server, fit the coefficients to the reported processing times with <see cref="Fit"/>; the
/// intercept is the per-call fixed cost, which unitless token weightings leave out and which is exactly what separates
/// "fewer long calls" from "more short calls". For an API, the published prices are the coefficients and the
/// intercept is zero.
/// </remarks>
public sealed record EnergyModel(double Fixed, double PerFreshPromptToken, double PerCachedToken, double PerOutputToken)
{
    public double Of(int promptTokens, int cachedTokens, int completionTokens)
    {
        var fresh = Math.Max(promptTokens - cachedTokens, 0);
        return Fixed + (PerFreshPromptToken * fresh) + (PerCachedToken * cachedTokens) + (PerOutputToken * completionTokens);
    }

    /// <summary>
    /// Least-squares coefficients for recorded calls and what each actually cost — for a self-hosted server, the
    /// processing time it reported (<c>SqliteTelemetryStore.ServerTimeSamples</c>). A term the samples never exercise
    /// (for example cached tokens on a server that reported none) gets 0. Null when there are fewer samples than
    /// terms to fit, or the samples cannot separate them (every call the same size).
    /// </summary>
    public static EnergyModel? Fit(IReadOnlyList<CallCostSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var rows = samples.Select(s => new[] { 1.0, Math.Max(s.PromptTokens - s.CachedTokens, 0), s.CachedTokens, s.CompletionTokens }).ToList();
        int[] terms = [0, .. Enumerable.Range(1, 3).Where(j => rows.Any(r => r[j] != 0))];
        if (samples.Count < terms.Length)
        {
            return null;
        }

        // Normal equations over the exercised terms: (XᵀX)·β = Xᵀy.
        var n = terms.Length;
        var a = new double[n, n + 1];
        for (var i = 0; i < rows.Count; i++)
        {
            for (var p = 0; p < n; p++)
            {
                for (var q = 0; q < n; q++)
                {
                    a[p, q] += rows[i][terms[p]] * rows[i][terms[q]];
                }

                a[p, n] += rows[i][terms[p]] * samples[i].Cost;
            }
        }

        if (Solve(a, n) is not { } beta)
        {
            return null;
        }

        var coefficients = new double[4];
        for (var p = 0; p < n; p++)
        {
            coefficients[terms[p]] = beta[p];
        }

        return new EnergyModel(coefficients[0], coefficients[1], coefficients[2], coefficients[3]);
    }

    /// <summary>Gaussian elimination with partial pivoting on an augmented n×(n+1) matrix; null when singular.</summary>
    private static double[]? Solve(double[,] a, int n)
    {
        for (var col = 0; col < n; col++)
        {
            var pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(a[r, col]));
            var scale = Enumerable.Range(0, n).Max(r => Math.Abs(a[r, col]));
            if (Math.Abs(a[pivot, col]) <= 1e-12 * Math.Max(scale, 1))
            {
                return null;
            }

            for (var k = 0; k <= n; k++)
            {
                (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            }

            for (var r = 0; r < n; r++)
            {
                if (r == col)
                {
                    continue;
                }

                var factor = a[r, col] / a[col, col];
                for (var k = col; k <= n; k++)
                {
                    a[r, k] -= factor * a[col, k];
                }
            }
        }

        return [.. Enumerable.Range(0, n).Select(r => a[r, n] / a[r, r])];
    }
}

/// <summary>One recorded call: its token counts and what it cost in the unit being fitted.</summary>
public sealed record CallCostSample(int PromptTokens, int CachedTokens, int CompletionTokens, double Cost);

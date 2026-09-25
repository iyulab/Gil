namespace Gil.Tests;

/// <summary>
/// Where a cross-implementation fixture is read from. The environment variable, when set, points at a local file (the full
/// check against recorded runs); otherwise the synthetic fixture committed under <c>tests/fixtures/conformance</c> is used,
/// so the contract is checked on every build without any setup.
/// </summary>
internal static class Conformance
{
    public static string? Fixture(string variable, string file)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (configured is not null)
        {
            return configured;
        }

        var committed = Path.Combine(AppContext.BaseDirectory, "conformance", file);
        return File.Exists(committed) || Directory.Exists(committed) ? committed : null;
    }
}

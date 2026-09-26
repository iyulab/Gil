using AwesomeAssertions;

namespace Gil.Tests.Traverse;

public sealed class PathStepTests
{
    [Fact]
    public void Printing_a_step_shows_its_probabilities()
    {
        var step = new PathStep("root", 1, "billing", 0.97, "accept", new Dictionary<string, double> { ["billing"] = 0.97, ["cards"] = 0.01 }, 312, 0.02);

        step.ToString().Should().Contain("Probs = { billing: 0.97, cards: 0.01 }").And.NotContain("Dictionary");
    }
}

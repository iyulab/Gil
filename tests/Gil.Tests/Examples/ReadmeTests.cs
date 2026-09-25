using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Gil.Tests.Examples;

public sealed partial class ReadmeTests
{
    [Fact]
    public void The_readme_code_is_the_compiled_example()
    {
        var root = Root();
        var example = Lines(File.ReadAllText(Path.Combine(root, "tests", "Gil.Tests", "Examples", "ReadmeExample.cs")));
        var readme = File.ReadAllText(Path.Combine(root, "README.md")).ReplaceLineEndings("\n");
        var blocks = CodeBlock().Matches(readme).Select(m => Lines(m.Groups[1].Value)).ToList();

        blocks.Should().NotBeEmpty();
        var at = 0;
        foreach (var line in blocks.SelectMany(block => block))
        {
            var found = example.FindIndex(at, l => l == line);
            found.Should().BeGreaterThanOrEqualTo(0, $"README line \"{line}\" should appear in ReadmeExample.cs after line {at}");
            at = found + 1;
        }
    }

    private static List<string> Lines(string text) =>
        [.. text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gil.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Gil.slnx not found above the test output");
    }

    [GeneratedRegex(@"```csharp\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();
}

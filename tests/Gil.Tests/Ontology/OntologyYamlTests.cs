using AwesomeAssertions;
using Gil.Ontology;

namespace Gil.Tests.Ontology;

public sealed class OntologyYamlTests
{
    private const string Example = """
        id: root
        label_scheme: letters
        children:
          - id: quality
            label: Quality
            description: defects, inspection results, out-of-spec parts
            children:
              - id: dimension
                label: Dimension
                description: dimensions out of tolerance
                options:
                  - id: ask-measurements
                    kind: answer
                    label: Ask for measurements
                    description: please send the measurement data
                    text: Please attach the lot's measurement data.
                    origin: seed
              - id: appearance
                label: Appearance
                description: scratches, discoloration
                options:
                  - id: received
                    kind: template
                    label: Received
                    description: acknowledges receipt
                    template: "{lot} received. {team} will reply."
                    slots:
                      - name: lot
                        instruction: the lot number as written
                      - name: team
                        instruction: fixed
                        fixed: Quality assurance
          - id: document
            label: Documents
            description: reading figures out of documents
            label_scheme: digits
            options:
              - id: extract-table
                kind: procedure
                label: Extract table
                description: read the table
                steps: Read the table at the top of page 2.
                origin: promoted
        """;

    [Fact]
    public void Reads_nodes_habits_slots_and_the_inherited_label_scheme()
    {
        var (root, warnings) = OntologyYaml.Parse(Example);

        warnings.Should().BeEmpty();
        root.CandidateIds.Should().Equal("quality", "document");
        var dimension = root.Find("dimension")!;
        dimension.LabelScheme.Should().Be("letters");
        dimension.Habits.Single().Text.Should().Be("Please attach the lot's measurement data.");
        var template = root.Find("appearance")!.Habits.Single();
        template.Kind.Should().Be(HabitKind.Template);
        template.Slots.Should().Equal(new Slot("lot", "the lot number as written"), new Slot("team", "fixed", "Quality assurance"));
        var document = root.Find("document")!;
        document.LabelScheme.Should().Be("digits");
        document.Habits.Single().Origin.Should().Be("promoted");
        root.Walk().Select(n => n.Id).Should().Equal("root", "quality", "dimension", "appearance", "document");
    }

    [Fact]
    public void Writing_then_reading_gives_the_same_tree_and_omits_defaults()
    {
        var (root, _) = OntologyYaml.Parse(Example);

        var written = OntologyYaml.Dump(root);
        var again = OntologyYaml.Dump(OntologyYaml.Parse(written).Root);

        again.Should().Be(written);
        written.Should().NotContain("kind: concept").And.Contain("fixed: Quality assurance");
    }

    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("123")]
    public void Strings_other_yaml_readers_would_reinterpret_are_quoted(string answer)
    {
        var root = new Node
        {
            Id = "root",
            Label = "root",
            Description = "",
            Habits = [new Habit { Id = "h", Kind = HabitKind.Answer, Label = answer, Description = answer, Text = answer }],
        };

        var written = OntologyYaml.Dump(root);

        written.Should().MatchRegex($"text: ['\"]{answer}['\"]");
        OntologyYaml.Parse(written).Root.Habits.Single().Text.Should().Be(answer);
    }

    [Theory]
    [InlineData("id: a\nchildren: [{id: b}]\noptions: [{id: c, kind: answer, text: x}]", "children and options together")]
    [InlineData("id: a\nchildren: [{id: a}]", "duplicate id: a")]
    [InlineData("id: a\noptions: [{id: b, kind: answer}]", "an answer needs text")]
    [InlineData("id: a\noptions: [{id: b, kind: template, template: '{x} {y}', slots: [{name: x}]}]", "template blanks without a slot — [y]")]
    [InlineData("id: a\noptions: [{id: b, kind: procedure}]", "a procedure needs steps")]
    [InlineData("id: a\noptions: [{id: b, kind: guess, text: x}]", "unknown kind guess")]
    public void Structural_rules_are_errors(string yaml, string message)
    {
        var parse = () => OntologyYaml.Parse(yaml);

        parse.Should().Throw<OntologyException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void More_candidates_than_the_scheme_can_label_is_an_error()
    {
        var tenOptions = string.Join(", ", Enumerable.Range(0, 10).Select(i => $"{{id: o{i}, kind: answer, text: t{i}}}"));

        var parse = () => OntologyYaml.Parse($"id: a\nlabel_scheme: digits\noptions: [{tenOptions}]");

        parse.Should().Throw<OntologyException>().WithMessage("*10 candidates*digits*at most 9*");
    }

    [Fact]
    public void Siblings_sharing_distinctive_words_raise_a_warning_but_still_load()
    {
        const string yaml = """
            id: root
            children:
              - {id: a, label: A, description: refund request for damaged parcel delivery}
              - {id: b, label: B, description: refund status of damaged parcel order}
              - {id: c, label: C, description: opening hours}
              - {id: d, label: D, description: password reset}
            """;

        var (root, warnings) = OntologyYaml.Parse(yaml);

        root.Children.Should().HaveCount(4);
        warnings.Should().ContainSingle().Which.Should().Contain("a and b").And.Contain("damaged, parcel, refund");
    }

    [Fact]
    public void Reads_every_tree_file_another_implementation_wrote()
    {
        // Point GIL_COMPAT_ONTOLOGY at a directory of tree files written elsewhere to check they load unchanged.
        var directory = Environment.GetEnvironmentVariable("GIL_COMPAT_ONTOLOGY");
        if (directory is null)
        {
            Assert.Skip("GIL_COMPAT_ONTOLOGY is not set");
        }

        var files = Directory.GetFiles(directory, "*.yaml");
        files.Should().NotBeEmpty();
        foreach (var file in files)
        {
            var (root, _) = OntologyYaml.Load(file);
            var written = OntologyYaml.Dump(root);
            OntologyYaml.Dump(OntologyYaml.Parse(written).Root).Should().Be(written, file);
            var target = Environment.GetEnvironmentVariable("GIL_COMPAT_ONTOLOGY_OUT");
            if (target is not null)
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, Path.GetFileName(file)), written);
            }
        }
    }
}

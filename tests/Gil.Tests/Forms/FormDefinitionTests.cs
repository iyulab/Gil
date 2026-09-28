using AwesomeAssertions;

namespace Gil.Tests.Forms;

public sealed class FormDefinitionTests
{
    private static FormDefinition Form(params FieldDefinition[] fields) => new("form", fields, PromptLanguage.English);

    [Fact]
    public void Contradictory_or_dangling_declarations_are_refused_when_the_form_is_defined()
    {
        var judged = new FieldDefinition("team", FieldRole.Judged);

        FluentActions.Invoking(() => Form(judged, judged)).Should().Throw<ArgumentException>().WithMessage("*more than once*");
        FluentActions.Invoking(() => Form(new FieldDefinition("component", FieldRole.Observed)))
            .Should().Throw<ArgumentException>().WithMessage("*at least one judged field*");
        FluentActions.Invoking(() => Form(judged with { DependsOn = ["component"] }))
            .Should().Throw<ArgumentException>().WithMessage("*not another field*");
        FluentActions.Invoking(() => Form(judged with { DependsOn = ["team"] }))
            .Should().Throw<ArgumentException>().WithMessage("*not another field*");
        FluentActions.Invoking(() => Form(new FieldDefinition("reporter", FieldRole.Observed) { UseAsEvidence = false }, judged with { DependsOn = ["reporter"] }))
            .Should().Throw<ArgumentException>().WithMessage("*not evidence*");
    }

    [Fact]
    public void A_field_is_supported_by_other_evidence_fields_limited_to_its_declared_dependencies()
    {
        var reporter = new FieldDefinition("reporter", FieldRole.Observed) { UseAsEvidence = false };
        var component = new FieldDefinition("component", FieldRole.Observed);
        var summary = new FieldDefinition("summary", FieldRole.Observed);
        var team = new FieldDefinition("team", FieldRole.Judged);
        var severity = new FieldDefinition("severity", FieldRole.Judged) { DependsOn = ["component"] };
        var form = Form(reporter, component, summary, team, severity);

        form.Supports("component", "team").Should().BeTrue();
        form.Supports("severity", "team").Should().BeTrue(); // a settled judged field is evidence too
        form.Supports("reporter", "team").Should().BeFalse();
        form.Supports("team", "team").Should().BeFalse();
        form.Supports("component", "severity").Should().BeTrue();
        form.Supports("summary", "severity").Should().BeFalse();
        form.Field("team").Should().BeSameAs(team);
        FluentActions.Invoking(() => form.Field("owner")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => form.Supports("owner", "team")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_settlement_carries_its_value_only_when_it_settles_one()
    {
        (Settlement.Accept("high").Kind, Settlement.Accept("high").Value).Should().Be((SettlementKind.Accept, "high"));
        Settlement.Correct("low").Value.Should().Be("low");
        Settlement.Restore("medium").Kind.Should().Be(SettlementKind.Restore);
        Settlement.Reject().Value.Should().BeNull();
        Settlement.Revert().Value.Should().BeNull();
    }
}

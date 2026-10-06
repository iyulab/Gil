using AwesomeAssertions;
using Gil.Forms;
using Gil.Memory;

namespace Gil.Tests.Forms;

/// <summary>
/// Korean typed text is compared by keystrokes, so a syllable still being composed is on the way to the value it begins.
/// </summary>
public sealed class HangulTests
{
    [Theory]
    [InlineData("박물관", "ㅂ")]
    [InlineData("박물관", "바")]
    [InlineData("박물관", "박")]
    [InlineData("박물관", "박무")]
    [InlineData("박물관", "박물")]
    [InlineData("바가지", "박")] // the final consonant may yet start the next syllable
    [InlineData("와이파이", "오")] // ㅘ is typed ㅗ then ㅏ
    [InlineData("닭갈비", "달")] // ㄺ is typed ㄹ then ㄱ
    [InlineData("A동 101호", "a도")]
    public void Typed_hangul_begins_a_value_its_keystrokes_begin(string value, string typed) =>
        FieldMemory.Begins(value, typed).Should().BeTrue();

    [Theory]
    [InlineData("까치", "ㄱ")] // ㄲ is one keystroke on the standard keyboard
    [InlineData("박물관", "버")]
    [InlineData("박물관", "김")]
    [InlineData("와이파이", "우")]
    public void Typed_hangul_does_not_begin_a_value_its_keystrokes_do_not_begin(string value, string typed) =>
        FieldMemory.Begins(value, typed).Should().BeFalse();

    [Fact]
    public void Keystrokes_split_syllables_and_compound_letters()
    {
        Hangul.Keystrokes("박").Should().Be("ㅂㅏㄱ");
        Hangul.Keystrokes("왔").Should().Be("ㅇㅗㅏㅆ");
        Hangul.Keystrokes("닭").Should().Be("ㄷㅏㄹㄱ");
        Hangul.Keystrokes("ㅘ").Should().Be("ㅗㅏ");
        Hangul.Keystrokes("Gil 길").Should().Be("Gil ㄱㅣㄹ");
    }

    [Fact]
    public void Text_without_hangul_is_compared_as_before() =>
        FieldMemory.Begins("network", "NET").Should().BeTrue();

    [Fact]
    public async Task A_suggestion_offers_the_value_a_composing_syllable_is_on_the_way_to()
    {
        var form = new FormDefinition(
            "handover",
            [
                new FieldDefinition("team", FieldRole.Observed),
                new FieldDefinition("owner", FieldRole.Judged) { TypedKeyThresholds = [0.2] },
            ],
            PromptLanguage.Korean);
        var history = Enumerable.Range(1, 6)
            .Select(i => new SettledDocument($"d{i}", new Dictionary<string, string> { ["team"] = "가", ["owner"] = i % 2 == 0 ? "박물관" : "김치찌개" }, DateTimeOffset.UnixEpoch.AddMinutes(i)))
            .ToList();
        var resolver = new FormResolver(new FieldMemory());
        await resolver.RebuildAsync(form, history, TestContext.Current.CancellationToken);

        var composing = await resolver.SuggestAsync(
            form, "d9", "owner", new Dictionary<string, string> { ["team"] = "가" }, typed: "바", cancellationToken: TestContext.Current.CancellationToken);

        composing.Candidates.Select(c => c.Value).Should().Equal("박물관");
        composing.Answered.Should().BeTrue();
    }
}

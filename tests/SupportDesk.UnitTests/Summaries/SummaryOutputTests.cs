using SupportDesk.Core.Summaries;

namespace SupportDesk.UnitTests.Summaries;

public class SummaryOutputTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t \r\n")]
    public void Empty_or_whitespace_output_fails_the_check(string? text)
    {
        Assert.Null(SummaryOutput.Check(text));
    }

    [Fact]
    public void Whitespace_around_the_summary_is_trimmed()
    {
        Assert.Equal("The customer wants a refund.", SummaryOutput.Check("\n  The customer wants a refund. \n\n"));
    }

    [Fact]
    public void A_summary_of_exactly_the_limit_is_kept_whole()
    {
        var text = new string('a', SummaryOutput.MaxLength - 1) + ".";

        Assert.Equal(text, SummaryOutput.Check("  " + text + "  "));
    }

    [Fact]
    public void A_long_summary_is_cut_after_the_last_sentence_that_fits()
    {
        var first = Sentence(250);
        var second = Sentence(300);
        var third = Sentence(200);

        var result = SummaryOutput.Check($"{first} {second} {third}");

        Assert.Equal($"{first} {second}", result);
    }

    [Fact]
    public void A_sentence_ending_exactly_at_the_limit_is_kept()
    {
        var first = Sentence(299);
        var second = Sentence(300);

        var result = SummaryOutput.Check($"{first} {second} And more after that.");

        Assert.Equal(SummaryOutput.MaxLength, result!.Length);
        Assert.Equal($"{first} {second}", result);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("?")]
    [InlineData(".\"")]
    [InlineData(".)")]
    [InlineData("?”")]
    public void Question_marks_exclamations_and_closing_quotes_end_a_sentence(string ending)
    {
        var first = new string('a', 400) + ending;

        var result = SummaryOutput.Check(first + " " + new string('b', 400) + ".");

        Assert.Equal(first, result);
    }

    [Fact]
    public void Dots_inside_numbers_and_addresses_are_not_sentence_ends()
    {
        var first = Sentence(100);
        var tail = "The customer ordered the 1.5 litre bottle from kestrel.example and wants it by Friday " + new string('x', 500) + ".";

        var result = SummaryOutput.Check(first + " " + tail);

        Assert.Equal(first, result);
    }

    [Fact]
    public void A_single_sentence_longer_than_the_limit_is_cut_at_a_word_with_an_ellipsis()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 200)) + ".";

        var result = SummaryOutput.Check(text);

        Assert.NotNull(result);
        Assert.True(result.Length <= SummaryOutput.MaxLength, $"length {result.Length}");
        Assert.EndsWith("word…", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_run_without_spaces_is_cut_at_the_limit_with_an_ellipsis()
    {
        var result = SummaryOutput.Check(new string('x', 900));

        Assert.Equal(new string('x', SummaryOutput.MaxLength - 1) + "…", result);
    }

    /// <summary>A sentence of exactly <paramref name="length"/> characters, ending with a full stop.</summary>
    private static string Sentence(int length) => "S" + new string('s', length - 2) + ".";
}

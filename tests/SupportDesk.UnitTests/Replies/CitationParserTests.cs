using SupportDesk.Core.Replies;
using static SupportDesk.UnitTests.Replies.ReplyTestData;

namespace SupportDesk.UnitTests.Replies;

public class CitationParserTests
{
    private static readonly SupportDesk.Core.Retrieval.RetrievedChunk[] Sources =
    [
        Chunk("shipping-times", 0.8, title: "Shipping times"),
        Chunk("returns-policy", 0.7, title: "Returns policy"),
        Chunk("shipping-times", 0.6, title: "Shipping times"),
    ];

    [Theory]
    [InlineData("Ships in 3 days [1].", new[] { 1 })]
    [InlineData("Both apply [1][2].", new[] { 1, 2 })]
    [InlineData("Spaced [ 3 ] out.", new[] { 3 })]
    [InlineData("A list [1, 2] and [2 ,3].", new[] { 1, 2, 2, 3 })]
    [InlineData("[2]Starts and ends[1]", new[] { 2, 1 })]
    [InlineData("Line one [1]\n[\n2\n] next", new[] { 1, 2 })]
    [InlineData("No markers [a], [1a], [], [-1], [1.5] or (1).", new int[0])]
    [InlineData("Too long [1234567890] to be a marker.", new int[0])]
    [InlineData("Non-ASCII digits [١] aren't markers.", new int[0])]
    public void Finds_markers_in_odd_places(string text, int[] expected)
    {
        Assert.Equal(expected, CitationParser.Markers(text));
    }

    [Fact]
    public void Valid_markers_map_to_the_articles_of_their_sources()
    {
        var result = CitationParser.Check("Shipping [1]. Returns [2]. Again [3].", Sources);

        Assert.Equal(
            [new Citation(1, "shipping-times", "Shipping times"), new Citation(2, "returns-policy", "Returns policy"), new Citation(3, "shipping-times", "Shipping times")],
            result.Citations);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void A_repeated_marker_gives_one_citation_in_marker_order()
    {
        var result = CitationParser.Check("Returns [2]. Shipping [1]. Returns again [2][2].", Sources);

        Assert.Equal([1, 2], result.Citations.Select(c => c.Marker));
    }

    [Fact]
    public void Out_of_range_markers_are_removed_once_each()
    {
        var result = CitationParser.Check("Made up [7] and [0], again [7], fine [1].", Sources);

        Assert.Equal([1], result.Citations.Select(c => c.Marker));
        Assert.Equal([0, 7], result.Removed);
    }

    [Fact]
    public void Without_sources_every_marker_is_removed()
    {
        var result = CitationParser.Check("Ships soon [1].", []);

        Assert.Empty(result.Citations);
        Assert.Equal([1], result.Removed);
    }
}

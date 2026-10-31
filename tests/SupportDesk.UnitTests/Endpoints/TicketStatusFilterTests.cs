using SupportDesk.Api.Endpoints;
using SupportDesk.Core.Tickets;

namespace SupportDesk.UnitTests.Endpoints;

public class TicketStatusFilterTests
{
    [Theory]
    [InlineData("Open", TicketStatus.Open)]
    [InlineData("pending", TicketStatus.Pending)]
    [InlineData("CLOSED", TicketStatus.Closed)]
    public void Status_names_parse_in_any_case(string value, TicketStatus expected)
    {
        Assert.True(TicketEndpoints.TryParseStatus(value, out var status));
        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("Open,Closed")]
    [InlineData(" Open")]
    [InlineData("")]
    public void Numbers_lists_and_unknown_names_are_rejected(string value) =>
        Assert.False(TicketEndpoints.TryParseStatus(value, out _));
}

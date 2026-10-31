using SupportDesk.Api.Endpoints;
using SupportDesk.Core.Tickets;

namespace SupportDesk.UnitTests.Endpoints;

public class CreateTicketValidatorTests
{
    [Fact]
    public void Valid_request_is_trimmed_into_a_new_ticket()
    {
        var (ticket, errors) = CreateTicketValidator.Validate(new CreateTicketRequest("  Broken zipper ", "  Body as written  ", " Kim Lee ", " KO-123456 "));

        Assert.Empty(errors);
        Assert.Equal(new NewTicket("Broken zipper", "  Body as written  ", "Kim Lee", "KO-123456"), ticket);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_order_number_means_none(string? orderNumber)
    {
        var (ticket, errors) = CreateTicketValidator.Validate(new CreateTicketRequest("s", "b", "c", orderNumber));

        Assert.Empty(errors);
        Assert.Null(ticket?.OrderNumber);
    }

    [Fact]
    public void Limits_are_inclusive()
    {
        var (ticket, errors) = CreateTicketValidator.Validate(
            new CreateTicketRequest(new string('s', 200), new string('b', 5000), new string('c', 200), null));

        Assert.Empty(errors);
        Assert.NotNull(ticket);
    }

    public static TheoryData<CreateTicketRequest, string> InvalidRequests => new()
    {
        { new CreateTicketRequest(null, "b", "c", null), "subject" },
        { new CreateTicketRequest(" ", "b", "c", null), "subject" },
        { new CreateTicketRequest(new string('s', 201), "b", "c", null), "subject" },
        { new CreateTicketRequest("s", null, "c", null), "body" },
        { new CreateTicketRequest("s", "\t\n", "c", null), "body" },
        { new CreateTicketRequest("s", new string('b', 5001), "c", null), "body" },
        { new CreateTicketRequest("s", "b", null, null), "customerName" },
        { new CreateTicketRequest("s", "b", "  ", null), "customerName" },
        { new CreateTicketRequest("s", "b", new string('c', 201), null), "customerName" },
        { new CreateTicketRequest("s", "b", "c", "KO-1234"), "orderNumber" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Each_invalid_field_is_reported_under_its_json_name(CreateTicketRequest request, string field)
    {
        var (ticket, errors) = CreateTicketValidator.Validate(request);

        Assert.Null(ticket);
        Assert.Equal([field], errors.Keys);
    }

    [Fact]
    public void Every_invalid_field_is_reported_at_once()
    {
        var (_, errors) = CreateTicketValidator.Validate(new CreateTicketRequest(null, null, null, "bad"));

        Assert.Equal(["body", "customerName", "orderNumber", "subject"], errors.Keys.Order(StringComparer.Ordinal));
    }
}

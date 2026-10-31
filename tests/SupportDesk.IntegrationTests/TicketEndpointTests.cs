using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.Tickets;

namespace SupportDesk.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public class TicketEndpointTests(PostgresFixture postgres)
{
    private static readonly Uri Tickets = new("/api/tickets", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_returns_every_seeded_ticket_newest_first_in_the_documented_shape()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        using var json = JsonDocument.Parse(await api.Client.GetStringAsync(Tickets, Ct));

        var items = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(25, items.Count);
        Assert.Equal(
            ["category", "createdAt", "id", "number", "priority", "status", "subject"],
            items[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("T-1025", items[0].GetProperty("number").GetString());
        Assert.Equal("Open", items[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("category").ValueKind);
        var created = items.Select(i => i.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        Assert.Equal(created.OrderDescending(), created);
    }

    [Theory]
    [InlineData("Pending", 3)]
    [InlineData("closed", 3)]
    [InlineData("OPEN", 19)]
    public async Task List_filters_by_status_in_any_case(string status, int expected)
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var items = await api.Client.GetFromJsonAsync<List<TicketListItemResponse>>($"/api/tickets?status={status}", JsonDefaults.Options, Ct);

        Assert.NotNull(items);
        Assert.Equal(expected, items.Count);
        Assert.All(items, i => Assert.Equal(status, i.Status.ToString(), ignoreCase: true));
    }

    [Theory]
    [InlineData("Archived")]
    [InlineData("1")]
    [InlineData("")]
    public async Task List_rejects_an_unknown_status_with_400_naming_status(string status)
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var response = await api.Client.GetAsync(new Uri($"/api/tickets?status={status}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Equal(["status"], problem?.Errors.Keys);
    }

    [Fact]
    public async Task Detail_returns_every_field_with_summary_null_and_classification_not_run()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var list = await api.Client.GetFromJsonAsync<List<TicketListItemResponse>>(Tickets, JsonDefaults.Options, Ct);
        var id = list!.Single(t => t.Number == "T-1002").Id;

        using var json = JsonDocument.Parse(await api.Client.GetStringAsync(new Uri($"/api/tickets/{id}", UriKind.Relative), Ct));

        var root = json.RootElement;
        Assert.Equal(
            ["body", "classification", "createdAt", "customerName", "id", "number", "orderNumber", "status", "subject", "summary"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("T-1002", root.GetProperty("number").GetString());
        Assert.Equal("Daniel Okafor", root.GetProperty("customerName").GetString());
        Assert.Equal("KO-205002", root.GetProperty("orderNumber").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("summary").ValueKind);
        var classification = root.GetProperty("classification");
        Assert.Equal(
            ["category", "classifiedAt", "priority", "promptVersion", "reason", "status"],
            classification.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("NotRun", classification.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, classification.GetProperty("category").ValueKind);
    }

    [Theory]
    [InlineData("/api/tickets/999999")]
    [InlineData("/api/tickets/not-a-number")]
    public async Task Detail_returns_404_problem_details_for_an_unknown_id(string path)
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var response = await api.Client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Equal(404, problem?.Status);
    }

    [Fact]
    public async Task Create_returns_201_with_location_and_the_ticket_is_listed_first()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var response = await api.Client.PostAsJsonAsync(
            Tickets, new CreateTicketRequest("  Zipper broke  ", "The main zipper split on day two.", " Kim Lee ", "KO-123456"), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TicketDetailResponse>(JsonDefaults.Options, Ct);
        Assert.NotNull(created);
        Assert.Equal($"/api/tickets/{created.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Zipper broke", created.Subject);
        Assert.Equal("Kim Lee", created.CustomerName);
        Assert.Equal("KO-123456", created.OrderNumber);
        Assert.Equal(TicketStatus.Open, created.Status);
        Assert.Equal(ClassificationStatus.NotRun, created.Classification.Status);

        var fetched = await api.Client.GetFromJsonAsync<TicketDetailResponse>(response.Headers.Location, JsonDefaults.Options, Ct);
        Assert.Equal(created, fetched);
        var list = await api.Client.GetFromJsonAsync<List<TicketListItemResponse>>(Tickets, JsonDefaults.Options, Ct);
        Assert.Equal(created.Id, list![0].Id);
    }

    [Fact]
    public async Task Create_accepts_the_longest_subject_and_body_and_no_order_number()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var response = await api.Client.PostAsJsonAsync(
            Tickets, new CreateTicketRequest(new string('s', 200), new string('b', 5000), "Kim Lee", ""), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TicketDetailResponse>(JsonDefaults.Options, Ct);
        Assert.Null(created?.OrderNumber);
    }

    public static TheoryData<string, string> InvalidFields => new()
    {
        { """{ "body": "b", "customerName": "c" }""", "subject" },
        { """{ "subject": "   ", "body": "b", "customerName": "c" }""", "subject" },
        { $$"""{ "subject": "{{new string('s', 201)}}", "body": "b", "customerName": "c" }""", "subject" },
        { """{ "subject": "s", "customerName": "c" }""", "body" },
        { """{ "subject": "s", "body": " \n ", "customerName": "c" }""", "body" },
        { $$"""{ "subject": "s", "body": "{{new string('b', 5001)}}", "customerName": "c" }""", "body" },
        { """{ "subject": "s", "body": "b" }""", "customerName" },
        { """{ "subject": "s", "body": "b", "customerName": "" }""", "customerName" },
        { """{ "subject": "s", "body": "b", "customerName": "c", "orderNumber": "KO-12345" }""", "orderNumber" },
        { """{ "subject": "s", "body": "b", "customerName": "c", "orderNumber": "ko-123456" }""", "orderNumber" },
        { """{ "subject": "s", "body": "b", "customerName": "c", "orderNumber": "123456" }""", "orderNumber" },
    };

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public async Task Create_returns_400_naming_the_invalid_field(string body, string field)
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await api.Client.PostAsync(Tickets, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Equal([field], problem?.Errors.Keys);
    }

    [Theory]
    [InlineData("{ \"subject\": ")]
    [InlineData("null")]
    [InlineData("{ \"subject\": 5, \"body\": \"b\", \"customerName\": \"c\" }")]
    public async Task Create_returns_400_problem_details_for_a_body_that_is_not_a_ticket(string body)
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await api.Client.PostAsync(Tickets, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Create_with_every_field_invalid_lists_every_field_and_saves_nothing()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        var response = await api.Client.PostAsJsonAsync(Tickets, new CreateTicketRequest("", "", "", "nope"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Equal(["body", "customerName", "orderNumber", "subject"], problem?.Errors.Keys.Order(StringComparer.Ordinal));
        var list = await api.Client.GetFromJsonAsync<List<TicketListItemResponse>>(Tickets, JsonDefaults.Options, Ct);
        Assert.Equal(25, list?.Count);
    }
}

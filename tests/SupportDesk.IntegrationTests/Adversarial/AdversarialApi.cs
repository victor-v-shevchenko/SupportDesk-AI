using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.Tickets;

namespace SupportDesk.IntegrationTests.Adversarial;

/// <summary>
/// The API against its own database with a fake-provider scenario written by the test (PLAN.md 6.8) for the one ticket
/// <see cref="TicketNumber"/>, with every other setting at what a test of the reply needs: all chunks retrieved, short
/// retry delays, one-second stream timeouts.
/// </summary>
internal sealed class AdversarialApi : IAsyncDisposable
{
    public const string TicketNumber = "T-9001";

    private readonly DirectoryInfo _directory;

    private AdversarialApi(SeededApi api, DirectoryInfo directory)
    {
        Api = api;
        _directory = directory;
    }

    public SeededApi Api { get; }

    public static async Task<AdversarialApi> StartAsync(
        PostgresFixture postgres, string turns, CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? settings = null, LogCollector? logs = null)
    {
        var directory = Directory.CreateTempSubdirectory("adversarial-");
        var path = Path.Combine(directory.FullName, "scenarios.json");
        await File.WriteAllTextAsync(path, Scenario(turns), cancellationToken);

        var values = new Dictionary<string, string?>
        {
            ["AI:Fake:ScenariosPath"] = path,
            ["Retrieval:MinSimilarity"] = "0",
            ["Retrieval:ReplyChunks"] = "10",
            ["AI:Retry:MaxAttempts"] = "2",
            ["AI:Retry:BaseDelayMs"] = "1",
            ["AI:Retry:MaxDelayMs"] = "200",
            ["AI:Timeouts:FirstItemSeconds"] = "1",
            ["AI:Timeouts:GapSeconds"] = "1",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        return new AdversarialApi(await SeededApi.StartAsync(postgres, cancellationToken, values, logger: logs), directory);
    }

    /// <summary>A default scenario (the file has to have one) and one for <see cref="TicketNumber"/> whose reply plays <paramref name="turns"/>.</summary>
    public static string Scenario(string turns) => $$"""
        {
          "scenarios": [
            { "name": "default", "ticketNumber": null, "turns": { "*": [ { "steps": [ { "type": "text", "text": "Default.", "chunkSize": 20, "delayMs": 0 } ] } ] } },
            { "name": "adversarial", "ticketNumber": "{{TicketNumber}}", "turns": { "Reply": [ {{turns}} ] } }
          ]
        }
        """;

    // Scenario building blocks

    public static string Turns(params string[] turns) => string.Join(", ", turns);

    public static string Turn(params string[] steps) => $$"""{ "steps": [ {{string.Join(", ", steps)}} ] }""";

    public static string Text(string text, int chunkSize = 12, int delayMs = 0) =>
        $$"""{ "type": "text", "text": {{JsonSerializer.Serialize(text)}}, "chunkSize": {{chunkSize}}, "delayMs": {{delayMs}} }""";

    public static string Usage(int input = 500, int output = 40) => $$"""{ "type": "usage", "inputTokens": {{input}}, "outputTokens": {{output}} }""";

    public static string Error(int status, double? retryAfterSeconds = null) => retryAfterSeconds is { } seconds
        ? $$"""{ "type": "error", "statusCode": {{status}}, "retryAfterSeconds": {{seconds.ToString(CultureInfo.InvariantCulture)}} }"""
        : $$"""{ "type": "error", "statusCode": {{status}} }""";

    public static string Stall(double seconds) => $$"""{ "type": "stall", "seconds": {{seconds.ToString(CultureInfo.InvariantCulture)}} }""";

    public static string Reset() => """{ "type": "reset" }""";

    public static string Finish(string reason) => $$"""{ "type": "finish", "reason": "{{reason}}" }""";

    public static string ToolCall(string name, string arguments) => $$"""{ "type": "tool_call", "name": {{JsonSerializer.Serialize(name)}}, "arguments": {{arguments}} }""";

    public static string Propose(string title, string notes = "", int? dueInDays = null) =>
        ToolCall("propose_follow_up_task", JsonSerializer.Serialize(dueInDays is { } days ? new { title, notes, dueInDays = days } : (object)new { title, notes }));

    // The API

    /// <summary>Adds a ticket with the scenario's number straight to the database (the endpoint numbers its tickets itself).</summary>
    public async Task<int> AddTicketAsync(string subject, string body, CancellationToken cancellationToken)
    {
        await using var db = Api.CreateDbContext();
        var ticket = new Ticket
        {
            Number = TicketNumber,
            Subject = subject,
            Body = body,
            CustomerName = "Maya Lindqvist",
            CreatedAt = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        return ticket.Id;
    }

    /// <summary>Asks for a suggested reply and reads the stream to its end.</summary>
    public async Task<ReplyStream> ReplyAsync(int ticketId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/tickets/{ticketId}/reply", UriKind.Relative));
        using var response = await Api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return ReplyStream.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>The ticket's model call rows, once there are <paramref name="count"/> (they are written by a background service) and no more arrive for a moment.</summary>
    public async Task<List<ModelCallResponse>> RowsAsync(int ticketId, int count, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var rows = await Api.Client.GetFromJsonAsync<List<ModelCallResponse>>(new Uri($"/api/model-calls?ticketId={ticketId}", UriKind.Relative), JsonDefaults.Options, cancellationToken);
            if (rows!.Count >= count || DateTime.UtcNow > deadline)
            {
                // A row that should not exist has had its chance to arrive
                await Task.Delay(300, cancellationToken);
                return [.. (await Api.Client.GetFromJsonAsync<List<ModelCallResponse>>(new Uri($"/api/model-calls?ticketId={ticketId}", UriKind.Relative), JsonDefaults.Options, cancellationToken))!.OrderBy(r => r.Id)];
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        try
        {
            _directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is harmless
        }
    }
}

/// <summary>A reply's server-sent events, split into frames: an <c>event:</c> line, a <c>data:</c> line, a blank line.</summary>
internal sealed class ReplyStream
{
    private ReplyStream(string body, List<(string Event, JsonElement Data)> events)
    {
        Body = body;
        Events = events;
    }

    public string Body { get; }

    public List<(string Event, JsonElement Data)> Events { get; }

    public IEnumerable<string> Names => Events.Select(e => e.Event);

    public string Text => string.Concat(Events.Where(e => e.Event == "token").Select(e => e.Data.GetProperty("text").GetString()));

    public JsonElement Single(string name) => Events.Single(e => e.Event == name).Data;

    public static ReplyStream Parse(string body)
    {
        var events = new List<(string, JsonElement)>();
        foreach (var frame in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = frame.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            events.Add((name, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return new ReplyStream(body, events);
    }

    /// <summary>Exactly one <c>done</c> or one <c>error</c>, and it is the last event (PLAN.md 7).</summary>
    public string AssertEndsOnce()
    {
        var terminal = Events.Where(e => e.Event is "done" or "error").ToList();
        Assert.Single(terminal);
        Assert.Equal(terminal[0].Event, Events[^1].Event);
        if (terminal[0].Event == "error")
        {
            Assert.DoesNotContain(Names, n => n is "citations" or "usage");
        }
        else
        {
            Assert.Equal(["citations", "usage", "done"], Names.SkipWhile(n => n is "token" or "tool_call"));
        }

        return terminal[0].Event;
    }
}

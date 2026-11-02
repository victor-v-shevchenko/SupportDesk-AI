using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.Tickets;
using SupportDesk.Eval.Metrics;
using SupportDesk.EmbedFixtures;
using Testcontainers.PostgreSql;

namespace SupportDesk.Eval.Suite;

/// <summary>
/// Runs classification and a reply for every golden ticket against the configured chat model, writes the report to
/// <c>docs/eval/</c>, then fails if the model missed a threshold (PLAN.md 11). With <c>Eval:Limit</c> it's a dry run on the
/// first few tickets: the report goes next to the test assembly instead, with the cost of a full run projected from it.
/// </summary>
[Trait("Category", "Eval")]
public class EvalSuiteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_configured_model_meets_its_thresholds_on_the_golden_set()
    {
        // Everything that can be wrong without a model is checked before the container starts
        var settings = EvalSettings.Load();
        var thresholds = EvalThresholds.Load(Path.Combine(AppContext.BaseDirectory, EvalThresholds.FileName)).For(settings.Model);
        var golden = await GoldenData.LoadAsync(Ct);
        var leakCheck = new SystemPromptLeakCheck(settings.Prompts.Select(p =>
            PromptStore.Load(Path.Combine(AppContext.BaseDirectory, "prompts"), p.Key, p.Value).System));
        var tickets = golden.Tickets.Take(settings.Limit ?? int.MaxValue).ToList();

        await using var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
        await postgres.StartAsync(Ct);
        var started = Stopwatch.StartNew();
        var outcomes = new List<TicketOutcome>();
        var ticketIds = new List<int>();
        var api = new EvalApi(postgres.GetConnectionString(), golden.Embeddings);
        await using (api)
        {
            using var client = api.CreateClient();
            // The pipeline has its own timeouts (PLAN.md 6.5); a local model on CPU can take minutes per reply
            client.Timeout = Timeout.InfiniteTimeSpan;
            foreach (var ticket in tickets)
            {
                var watch = Stopwatch.StartNew();
                TicketOutcome outcome;
                try
                {
                    (var id, outcome) = await RunAsync(client, ticket, Ct);
                    ticketIds.Add(id);
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    // One broken ticket mustn't lose an hour's run: it counts as failed, and the report says so
                    var error = $"not run ({exception.GetType().Name})";
                    outcome = new TicketOutcome(ticket, null, null, error, "", [], [], error, 0);
                }

                outcomes.Add(outcome);
                TestContext.Current.SendDiagnosticMessage(
                    $"{ticket.Id}: {outcome.Category?.ToString() ?? "unclassified"}/{outcome.Priority?.ToString() ?? "-"}, "
                    + $"cited [{string.Join(", ", outcome.CitedSlugs)}]{(outcome.ReplyError is null ? "" : $", reply error {outcome.ReplyError}")} "
                    + $"({watch.Elapsed.TotalSeconds:0} s)");
            }
        }

        // Disposing the API saved the usage rows still queued (PLAN.md 6.6)
        await using var db = api.CreateDbContext();
        var rows = await db.ModelCalls.Where(r => r.TicketId != null && ticketIds.Contains(r.TicketId.Value)).ToListAsync(Ct);

        var metrics = EvalMetricsCalculator.Compute(outcomes, leakCheck);
        var usage = UsageTotals.Of(rows);
        var dryRun = tickets.Count < golden.Tickets.Count;
        List<string> failures = [.. EvalThresholds.Check(metrics, thresholds)];
        // The suite reads the configuration on its own, so make sure the API called the model the report names
        if (rows.Select(r => r.Model).Distinct(StringComparer.Ordinal).Where(m => m != settings.Model).ToList() is { Count: > 0 } others)
        {
            failures.Add($"The API called {string.Join(", ", others)}, not only the configured {settings.Model}.");
        }
        var run = new EvalRun(
            DateOnly.FromDateTime(DateTime.UtcNow),
            settings.Provider.ToString(),
            settings.Model,
            golden.Embeddings.Model,
            [.. settings.Prompts.Values.Order(StringComparer.Ordinal)],
            started.Elapsed,
            golden.Tickets.Count);
        var report = EvalReport.Render(
            run, metrics, usage, thresholds, failures,
            [.. outcomes.Select(o => (o.Ticket.Id, o.Ticket.Subject, EvalMetricsCalculator.ProblemsOf(o, leakCheck)))]);

        var folder = dryRun ? Path.Combine(AppContext.BaseDirectory, "eval-dry-runs") : Path.Combine(RepositoryRoot(), "docs", "eval");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, EvalReport.FileName(run.Date, settings.Model, settings.Label));
        await File.WriteAllTextAsync(path, report, Ct);
        TestContext.Current.SendDiagnosticMessage($"Report: {path}");
        if (settings.SaveReplies)
        {
            // Off by default: a reply can quote the ticket, and CLAUDE.md keeps ticket text out of logs unless a setting asks
            var replies = Path.Combine(AppContext.BaseDirectory, "eval-replies", Path.GetFileNameWithoutExtension(EvalReport.FileName(run.Date, settings.Model, settings.Label)));
            Directory.CreateDirectory(replies);
            foreach (var outcome in outcomes)
            {
                // A failed reply has no text, so its error code is kept instead
                await File.WriteAllTextAsync(
                    Path.Combine(replies, $"{EvalReport.SafeName(outcome.Ticket.Id)}.txt"), outcome.ReplyError is null ? outcome.ReplyText : $"(reply failed: {outcome.ReplyError})", Ct);
            }

            TestContext.Current.SendDiagnosticMessage($"Replies: {replies}");
        }

        if (dryRun)
        {
            var projected = usage.CostUsd * golden.Tickets.Count / tickets.Count;
            TestContext.Current.SendDiagnosticMessage(
                $"Dry run: {usage.CostUsd:$0.0000} for {tickets.Count} tickets; a full run of {golden.Tickets.Count} would cost about {projected:$0.00}"
                + (usage.UnpricedCalls > 0 ? $" ({usage.UnpricedCalls} calls had no price)" : "")
                + ". The first tickets are short ordinary ones, so a full run, with injection and tool-call cases, may cost a little more.");
            return;
        }

        Assert.True(failures.Count == 0, $"{settings.Model} missed its thresholds (report: {path}):\n" + string.Join('\n', failures));
    }

    private static async Task<(int Id, TicketOutcome Outcome)> RunAsync(HttpClient client, GoldenTicket ticket, CancellationToken cancellationToken)
    {
        using var created = await client.PostAsJsonAsync(
            "/api/tickets", new CreateTicketRequest(ticket.Subject, ticket.Body, ticket.CustomerName, null), EvalApi.Json, cancellationToken);
        if (created.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"{ticket.Id} wasn't created: {(int)created.StatusCode}.");
        }

        var id = (await created.Content.ReadFromJsonAsync<TicketDetailResponse>(EvalApi.Json, cancellationToken))!.Id;

        Category? category = null;
        Priority? priority = null;
        string? classificationError = null;
        using (var classified = await client.PostAsync(new Uri($"/api/tickets/{id}/classification", UriKind.Relative), content: null, cancellationToken))
        {
            if (classified.IsSuccessStatusCode)
            {
                var result = (await classified.Content.ReadFromJsonAsync<TicketClassificationResponse>(EvalApi.Json, cancellationToken))!;
                if (result.Status == ClassificationStatus.Done)
                {
                    (category, priority) = (result.Category, result.Priority);
                }
                else
                {
                    classificationError = result.Status.ToString();
                }
            }
            else
            {
                classificationError = $"{(int)classified.StatusCode} {ProblemTitle(await classified.Content.ReadAsStringAsync(cancellationToken))}";
            }
        }

        var events = await ReplyAsync(client, id, cancellationToken);
        var text = new StringBuilder();
        var cited = new List<string>();
        var removed = new List<int>();
        string? replyError = null;
        foreach (var e in events)
        {
            switch (e.Event)
            {
                case "token":
                    text.Append(e.Data.GetProperty("text").GetString());
                    break;
                case "citations":
                    cited.AddRange(e.Data.GetProperty("citations").EnumerateArray().Select(c => c.GetProperty("articleSlug").GetString()!));
                    removed.AddRange(e.Data.GetProperty("removed").EnumerateArray().Select(m => m.GetInt32()));
                    break;
                case "error":
                    replyError = e.Data.GetProperty("code").GetString();
                    break;
            }
        }

        var followUps = (await client.GetFromJsonAsync<FollowUpsResponse>($"/api/tickets/{id}/follow-ups", EvalApi.Json, cancellationToken))!;
        return (id, new TicketOutcome(
            ticket, category, priority, classificationError, text.ToString(),
            [.. cited.Distinct(StringComparer.Ordinal)], removed, replyError, followUps.Proposals.Count));
    }

    private static async Task<IReadOnlyList<ServerSentEvent>> ReplyAsync(HttpClient client, int ticketId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/tickets/{ticketId}/reply", UriKind.Relative));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ServerSentEvents.ReadAllAsync(stream, cancellationToken);
    }

    private static string ProblemTitle(string body)
    {
        try
        {
            using var problem = JsonDocument.Parse(body);
            return problem.RootElement.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>The folder with <c>SupportDesk.slnx</c>, above the test assembly.</summary>
    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "SupportDesk.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("SupportDesk.slnx wasn't found above the test assembly, so the report has nowhere to go.");
    }
}

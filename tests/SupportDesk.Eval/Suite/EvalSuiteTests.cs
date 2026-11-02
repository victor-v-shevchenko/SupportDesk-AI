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
                var (id, outcome) = await RunAsync(client, ticket);
                ticketIds.Add(id);
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
        var failures = EvalThresholds.Check(metrics, thresholds);
        var run = new EvalRun(
            DateOnly.FromDateTime(DateTime.UtcNow),
            settings.Provider.ToString(),
            settings.Model,
            golden.Embeddings.Model,
            [.. rows.Select(r => r.PromptVersion).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            started.Elapsed,
            golden.Tickets.Count);
        var report = EvalReport.Render(
            run, metrics, usage, thresholds, failures,
            [.. outcomes.Select(o => (o.Ticket.Id, o.Ticket.Subject, EvalMetricsCalculator.ProblemsOf(o, leakCheck)))]);

        var folder = dryRun ? Path.Combine(AppContext.BaseDirectory, "eval-dry-runs") : Path.Combine(RepositoryRoot(), "docs", "eval");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, EvalReport.FileName(run.Date, settings.Model));
        await File.WriteAllTextAsync(path, report, Ct);
        TestContext.Current.SendDiagnosticMessage($"Report: {path}");

        if (dryRun)
        {
            var projected = usage.CostUsd * golden.Tickets.Count / tickets.Count;
            TestContext.Current.SendDiagnosticMessage(
                $"Dry run: {usage.CostUsd:$0.0000} for {tickets.Count} tickets; a full run of {golden.Tickets.Count} would cost about {projected:$0.00}"
                + (usage.UnpricedCalls > 0 ? $" ({usage.UnpricedCalls} calls had no price)" : "") + ".");
            return;
        }

        Assert.True(failures.Count == 0, $"{settings.Model} missed its thresholds (report: {path}):\n" + string.Join('\n', failures));
    }

    private static async Task<(int Id, TicketOutcome Outcome)> RunAsync(HttpClient client, GoldenTicket ticket)
    {
        var created = await client.PostAsJsonAsync("/api/tickets", new CreateTicketRequest(ticket.Subject, ticket.Body, ticket.CustomerName, null), EvalApi.Json, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{ticket.Id} wasn't created: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync(Ct)}");
        var id = (await created.Content.ReadFromJsonAsync<TicketDetailResponse>(EvalApi.Json, Ct))!.Id;

        Category? category = null;
        Priority? priority = null;
        string? classificationError = null;
        using (var classified = await client.PostAsync(new Uri($"/api/tickets/{id}/classification", UriKind.Relative), content: null, Ct))
        {
            if (classified.IsSuccessStatusCode)
            {
                var result = (await classified.Content.ReadFromJsonAsync<TicketClassificationResponse>(EvalApi.Json, Ct))!;
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
                classificationError = $"{(int)classified.StatusCode} {ProblemTitle(await classified.Content.ReadAsStringAsync(Ct))}";
            }
        }

        var events = await ReplyAsync(client, id);
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

        var followUps = (await client.GetFromJsonAsync<FollowUpsResponse>($"/api/tickets/{id}/follow-ups", EvalApi.Json, Ct))!;
        return (id, new TicketOutcome(
            ticket, category, priority, classificationError, text.ToString(),
            [.. cited.Distinct(StringComparer.Ordinal)], removed, replyError, followUps.Proposals.Count));
    }

    private static async Task<IReadOnlyList<ServerSentEvent>> ReplyAsync(HttpClient client, int ticketId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/tickets/{ticketId}/reply", UriKind.Relative));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        return await ServerSentEvents.ReadAllAsync(stream, Ct);
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

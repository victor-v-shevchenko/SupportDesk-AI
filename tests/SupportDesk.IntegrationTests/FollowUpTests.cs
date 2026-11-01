using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Api.Endpoints;
using SupportDesk.Core.FollowUps;

namespace SupportDesk.IntegrationTests;

/// <summary>Proposals and tasks (PLAN.md 4, 10): the repository's upsert and expiry, and the follow-up endpoints.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class FollowUpTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_repeated_proposal_in_the_same_run_updates_the_one_proposal()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1008");
        var runId = Guid.NewGuid();

        var first = await UpsertAsync(api, new NewProposal(ticketId, runId, "Trace the parcel", "", 2));
        var second = await UpsertAsync(api, new NewProposal(ticketId, runId, "Open a carrier trace", "Three weeks late.", null));
        var otherRun = await UpsertAsync(api, new NewProposal(ticketId, Guid.NewGuid(), "Call back", "", 1));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(("Open a carrier trace", "Three weeks late.", (DateOnly?)null), (second.Title, second.Notes, second.DueDate));
        Assert.Equal((first.CreatedAt, first.ExpiresAt), (second.CreatedAt, second.ExpiresAt));
        Assert.Equal(first.CreatedAt + TimeSpan.FromHours(24), first.ExpiresAt);
        Assert.Equal(DateOnly.FromDateTime(first.CreatedAt.UtcDateTime).AddDays(2), first.DueDate);
        Assert.NotEqual(first.Id, otherRun.Id);
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.FollowUpProposals.CountAsync(p => p.ReplyRunId == runId, Ct));
    }

    [Fact]
    public async Task Retries_racing_each_other_in_one_run_leave_one_proposal()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1008");
        var runId = Guid.NewGuid();

        var stored = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => UpsertAsync(api, new NewProposal(ticketId, runId, $"Attempt {i}", "", null))));

        Assert.Single(stored.Select(p => p.Id).Distinct());
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.FollowUpProposals.CountAsync(p => p.ReplyRunId == runId, Ct));
    }

    [Fact]
    public async Task A_proposal_that_is_no_longer_pending_is_not_changed_by_a_late_retry()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1008");
        var runId = Guid.NewGuid();
        var proposal = await UpsertAsync(api, new NewProposal(ticketId, runId, "Trace the parcel", "", 2));
        await ConfirmAsync(api, proposal.Id);

        var retried = await UpsertAsync(api, new NewProposal(ticketId, runId, "Something else", "", 5));

        Assert.Equal((proposal.Id, "Trace the parcel", ProposalStatus.Confirmed), (retried.Id, retried.Title, retried.Status));
    }

    [Fact]
    public async Task Expiry_marks_only_pending_proposals_past_their_expiry()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1008");
        var old = await UpsertAsync(api, new NewProposal(ticketId, Guid.NewGuid(), "Old", "", null));
        var fresh = await UpsertAsync(api, new NewProposal(ticketId, Guid.NewGuid(), "Fresh", "", null));
        var confirmed = await UpsertAsync(api, new NewProposal(ticketId, Guid.NewGuid(), "Confirmed", "", null));
        await ConfirmAsync(api, confirmed.Id);
        await AgeAsync(api, old.Id, confirmed.Id);

        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            // The background check also ran once at startup, before the aging, so only the statuses are checked
            await scope.ServiceProvider.GetRequiredService<IFollowUpRepository>().ExpireAsync(Ct);
        }

        await using var db = api.CreateDbContext();
        var statuses = await db.FollowUpProposals.ToDictionaryAsync(p => p.Id, p => p.Status, Ct);
        Assert.Equal(ProposalStatus.Expired, statuses[old.Id]);
        Assert.Equal(ProposalStatus.Pending, statuses[fresh.Id]);
        Assert.Equal(ProposalStatus.Confirmed, statuses[confirmed.Id]);
    }

    [Fact]
    public async Task Confirming_creates_the_task_once_and_a_second_confirm_returns_the_same_task()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1008");
        var proposal = await UpsertAsync(api, new NewProposal(ticketId, Guid.NewGuid(), "Open a carrier trace", "Three weeks late.", 2));

        using var first = await PostConfirmAsync(api, proposal.Id);
        using var second = await PostConfirmAsync(api, proposal.Id);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var task = (await first.Content.ReadFromJsonAsync<FollowUpTaskResponse>(JsonDefaults.Options, Ct))!;
        Assert.Equal(task, await second.Content.ReadFromJsonAsync<FollowUpTaskResponse>(JsonDefaults.Options, Ct));
        Assert.Equal((proposal.Id, "Open a carrier trace", "Three weeks late.", proposal.DueDate), (task.ProposalId, task.Title, task.Notes, task.DueDate));

        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.FollowUpTasks.CountAsync(t => t.ProposalId == proposal.Id, Ct));
        Assert.Equal(ProposalStatus.Confirmed, (await db.FollowUpProposals.SingleAsync(p => p.Id == proposal.Id, Ct)).Status);
    }

    [Fact]
    public async Task Confirms_racing_each_other_create_one_task()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1008");
        var proposal = await UpsertAsync(api, new NewProposal(ticketId, Guid.NewGuid(), "Open a carrier trace", "", null));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostConfirmAsync(api, proposal.Id)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<FollowUpTaskResponse>(JsonDefaults.Options, Ct))!.Id));
        Assert.Single(ids.Distinct());
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.FollowUpTasks.CountAsync(t => t.ProposalId == proposal.Id, Ct));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Confirming_an_expired_proposal_is_a_409_and_creates_nothing()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1008");
        var proposal = await UpsertAsync(api, new NewProposal(ticketId, Guid.NewGuid(), "Open a carrier trace", "", null));
        // Past its expiry but still Pending: the background check hasn't run yet
        await AgeAsync(api, proposal.Id);

        using var response = await PostConfirmAsync(api, proposal.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Proposal expired", (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
        await using var db = api.CreateDbContext();
        Assert.False(await db.FollowUpTasks.AnyAsync(t => t.ProposalId == proposal.Id, Ct));
        Assert.Equal(ProposalStatus.Expired, (await db.FollowUpProposals.SingleAsync(p => p.Id == proposal.Id, Ct)).Status);

        using var again = await PostConfirmAsync(api, proposal.Id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Confirming_an_unknown_proposal_is_a_404()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);

        using var response = await PostConfirmAsync(api, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Proposal not found", (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
    }

    [Fact]
    public async Task The_follow_ups_endpoint_lists_proposals_and_tasks_newest_first()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1008");
        var otherTicket = await TicketIdAsync(api, "T-1002");
        var older = await UpsertAsync(api, new NewProposal(ticketId, Guid.NewGuid(), "Older", "", null));
        var newer = await UpsertAsync(api, new NewProposal(ticketId, Guid.NewGuid(), "Newer", "Notes", 3));
        await UpsertAsync(api, new NewProposal(otherTicket, Guid.NewGuid(), "Other ticket", "", null));
        await ConfirmAsync(api, older.Id);

        var response = await api.Client.GetAsync(new Uri($"/api/tickets/{ticketId}/follow-ups", UriKind.Relative), Ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposals = json.GetProperty("proposals").EnumerateArray().ToList();
        Assert.Equal(["Newer", "Older"], proposals.Select(p => p.GetProperty("title").GetString()));
        Assert.Equal(["Pending", "Confirmed"], proposals.Select(p => p.GetProperty("status").GetString()));
        var first = proposals[0];
        Assert.Equal(newer.Id, first.GetProperty("id").GetGuid());
        Assert.Equal(newer.ReplyRunId, first.GetProperty("replyRunId").GetGuid());
        Assert.Equal("Notes", first.GetProperty("notes").GetString());
        Assert.Equal(newer.DueDate!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), first.GetProperty("dueDate").GetString());
        Assert.Equal(newer.ExpiresAt, first.GetProperty("expiresAt").GetDateTimeOffset());
        var task = Assert.Single(json.GetProperty("tasks").EnumerateArray());
        Assert.Equal(older.Id, task.GetProperty("proposalId").GetGuid());
    }

    [Fact]
    public async Task The_follow_ups_of_an_unknown_ticket_are_a_404_and_of_a_ticket_without_any_are_empty()
    {
        await using var api = await SeededApi.StartAsync(postgres, Ct);
        var ticketId = await TicketIdAsync(api, "T-1001");

        var unknown = await api.Client.GetAsync(new Uri("/api/tickets/999999/follow-ups", UriKind.Relative), Ct);
        var empty = await api.Client.GetFromJsonAsync<FollowUpsResponse>(new Uri($"/api/tickets/{ticketId}/follow-ups", UriKind.Relative), JsonDefaults.Options, Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(empty!.Proposals);
        Assert.Empty(empty.Tasks);
    }

    internal static async Task<int> TicketIdAsync(SeededApi api, string number)
    {
        await using var db = api.CreateDbContext();
        return await db.Tickets.Where(t => t.Number == number).Select(t => t.Id).SingleAsync(Ct);
    }

    internal static Task<HttpResponseMessage> PostConfirmAsync(SeededApi api, Guid proposalId) =>
        api.Client.PostAsync(new Uri($"/api/follow-ups/{proposalId}/confirm", UriKind.Relative), null, Ct);

    /// <summary>Each in its own scope, as separate requests would be.</summary>
    private static async Task<FollowUpProposal> UpsertAsync(SeededApi api, NewProposal proposal)
    {
        await using var scope = api.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFollowUpRepository>().UpsertProposalAsync(proposal, Ct);
    }

    private static async Task ConfirmAsync(SeededApi api, Guid proposalId)
    {
        using var response = await PostConfirmAsync(api, proposalId);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Moves the proposals' creation and expiry a day and a minute into the past.</summary>
    private static async Task AgeAsync(SeededApi api, params Guid[] proposalIds)
    {
        await using var db = api.CreateDbContext();
        var age = TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1);
        await db.FollowUpProposals
            .Where(p => proposalIds.Contains(p.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, p => p.CreatedAt - age).SetProperty(p => p.ExpiresAt, p => p.ExpiresAt - age), Ct);
    }
}

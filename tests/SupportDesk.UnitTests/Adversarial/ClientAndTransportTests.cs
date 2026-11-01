using System.Text.RegularExpressions;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Orders;
using SupportDesk.Core.Replies;
using SupportDesk.UnitTests.AI;
using SupportDesk.UnitTests.Replies;
using static SupportDesk.UnitTests.Adversarial.ReplyRig;

namespace SupportDesk.UnitTests.Adversarial;

/// <summary>
/// Step 9, client and transport (PLAN.md 7): the client leaves, aborts and starts again, or opens the same ticket in two
/// tabs. A disconnect must reach the provider call within a second of fake time, and runs must never share anything.
/// </summary>
public sealed partial class ClientAndTransportTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"(?m)^\[\d+\] Title of ", RegexOptions.CultureInvariant)]
    private static partial Regex SourceLine();

    // Disconnects

    [Fact]
    public async Task A_disconnect_mid_stream_cancels_the_provider_call_within_one_second_and_sends_no_error()
    {
        using var rig = Create(Turn(Text("First part. "), Stall(60), Text("never seen")));
        using var client = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var events = new List<ReplyEvent>();
        var read = rig.ReadAsync(Ticket(), events, client.Token);
        await WaitForAsync(() => events.Count >= 1);

        var left = rig.Time.GetUtcNow();
        await client.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Time.RunAsync(read, Step));

        Assert.True(rig.Time.GetUtcNow() - left <= TimeSpan.FromSeconds(1), "The provider call was still running a second after the client left.");
        Assert.Equal(1, rig.Spy.CancelledCalls);
        Assert.All(events, e => Assert.IsType<ReplyToken>(e));
        var row = Assert.Single(rig.Rows);
        Assert.Equal(ModelCallOutcome.Cancelled, row.Outcome);
        Assert.Contains(rig.Logs, m => m.Contains("was cancelled", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Logs, m => m.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_disconnect_before_the_first_item_cancels_the_provider_call_within_one_second()
    {
        using var rig = Create(Turn(Stall(60), Text("never seen")));
        using var client = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var events = new List<ReplyEvent>();
        var read = rig.ReadAsync(Ticket(), events, client.Token);
        await WaitForAsync(() => rig.Spy.Calls.Count == 1);

        var left = rig.Time.GetUtcNow();
        await client.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Time.RunAsync(read, Step));

        Assert.True(rig.Time.GetUtcNow() - left <= TimeSpan.FromSeconds(1));
        Assert.Equal(1, rig.Spy.CancelledCalls);
        Assert.Empty(events);
        Assert.Equal(ModelCallOutcome.Cancelled, Assert.Single(rig.Rows).Outcome);
    }

    [Fact]
    public async Task A_disconnect_while_waiting_to_retry_stops_the_retries_within_one_second()
    {
        using var rig = Create(Turn(Error(503)), configure: o => o.Retry.BaseDelayMs = 4000);
        using var client = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var events = new List<ReplyEvent>();
        var read = rig.ReadAsync(Ticket(), events, client.Token);
        await WaitForAsync(() => rig.Spy.Calls.Count == 1);
        // The first attempt failed at once; the call is now waiting out its backoff
        await Task.Delay(100, Ct);

        var left = rig.Time.GetUtcNow();
        await client.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Time.RunAsync(read, Step));

        Assert.True(rig.Time.GetUtcNow() - left <= TimeSpan.FromSeconds(1));
        Assert.Single(rig.Spy.Calls);
        Assert.Empty(events);
        Assert.Equal(ModelCallOutcome.Cancelled, Assert.Single(rig.Rows).Outcome);
    }

    [Fact]
    public async Task A_client_that_stops_reading_cancels_the_provider_call_too()
    {
        using var rig = Create(Turn(Text("First part. "), Stall(60), Text("never seen")));
        var events = new List<ReplyEvent>();
        using var stop = new CancellationTokenSource();

        // As the endpoint does when the connection drops: the request's token is cancelled, then the enumerator is disposed
        await using (var stream = rig.Service.StreamAsync(Ticket(), stop.Token).GetAsyncEnumerator(Ct))
        {
            Assert.True(await stream.MoveNextAsync());
            events.Add(stream.Current);
            await stop.CancelAsync();
        }

        Assert.Equal(1, rig.Spy.CancelledCalls);
        Assert.Equal(ModelCallOutcome.Cancelled, Assert.Single(rig.Rows).Outcome);
        Assert.Contains(rig.Logs, m => m.Contains("was cancelled", StringComparison.Ordinal));
    }

    // Abort and start again

    [Fact]
    public async Task Aborting_a_reply_and_starting_a_new_one_at_once_gives_the_new_one_its_own_run_and_proposal()
    {
        using var rig = Create(Turns(
            Turn(Propose("Open a carrier trace", "Late.", 2), Usage(300, 10)),
            Turn(Text("Sorry about the delay. "), Stall(10), Text("An agent will follow up."), Usage(400, 20))));
        using var first = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var firstEvents = new List<ReplyEvent>();
        var secondEvents = new List<ReplyEvent>();

        var firstRead = rig.ReadAsync(Ticket(id: 7), firstEvents, first.Token);
        await WaitForAsync(() => firstEvents.OfType<ReplyToken>().Any());
        // The client aborts and clicks "Suggest reply" again in the same moment
        await first.CancelAsync();
        var secondRead = rig.ReadAsync(Ticket(id: 7), secondEvents, Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Time.RunAsync(firstRead, Step));
        await rig.Time.RunAsync(secondRead, Step);

        // The aborted run has no terminal event; the new one finishes normally
        Assert.DoesNotContain(firstEvents, e => e is ReplyError or ReplyDone);
        StreamAssert.EndsOnce(secondEvents);
        var secondDone = Assert.IsType<ReplyDone>(secondEvents[^1]);
        Assert.Equal("Sorry about the delay. An agent will follow up.", string.Concat(secondEvents.OfType<ReplyToken>().Select(t => t.Text)));

        var runs = rig.FollowUps.Upserts.Select(u => u.ReplyRunId).ToList();
        Assert.Equal(2, runs.Distinct().Count());
        Assert.Contains(secondDone.ReplyRunId, runs);
        Assert.Equal(2, rig.FollowUps.Count);
        Assert.Equal(0, rig.FollowUps.Confirms);
        // The aborted run's last call is Cancelled; nothing of the new run is
        var byRun = rig.Rows.GroupBy(r => r.ReplyRunId).ToDictionary(g => g.Key!.Value, g => g.Select(r => r.Outcome).ToList());
        Assert.Equal(2, byRun.Count);
        Assert.Equal([ModelCallOutcome.Success, ModelCallOutcome.Success], byRun[secondDone.ReplyRunId]);
        Assert.Equal([ModelCallOutcome.Success, ModelCallOutcome.Cancelled], byRun[runs.Single(r => r != secondDone.ReplyRunId)]);
        Assert.Equal(1, rig.Spy.CancelledCalls);
    }

    [Fact]
    public async Task Two_replies_for_one_ticket_at_once_each_complete_with_their_own_run_tool_events_and_proposal()
    {
        var order = new OrderStatus("KO-205002", "Delayed", "FedEx", new DateTimeOffset(2026, 10, 11, 7, 30, 0, TimeSpan.Zero), "Weather delay");
        // Both runs play the same scenario, so their tool calls even carry the same call IDs
        using var rig = Create(
            Turns(
                Turn(Lookup("KO-205002"), Propose("Open a carrier trace", "Late.", 2), Usage(300, 10)),
                Turn(Text("Your order is delayed. ", delayMs: 100), Text("An agent will follow up [1]."), Usage(400, 20))),
            new StubRetrieval(ReplyTestData.Chunk("shipping-times", 0.9, title: "Shipping")),
            orders: new StubOrders(order));
        var tabA = new List<ReplyEvent>();
        var tabB = new List<ReplyEvent>();

        var readA = rig.ReadAsync(Ticket(id: 7), tabA, Ct);
        var readB = rig.ReadAsync(Ticket(id: 7), tabB, Ct);
        // Both are waiting on the fake clock in their second call at the same time
        await WaitForAsync(() => rig.Spy.Calls.Count == 4);
        Assert.False(readA.IsCompleted || readB.IsCompleted);
        await rig.Time.RunAsync(Task.WhenAll(readA, readB), Step);

        foreach (var events in new[] { tabA, tabB })
        {
            StreamAssert.EndsOnce(events);
            Assert.Equal([typeof(ReplyOrderLookup), typeof(ReplyProposal)], events.Where(e => e is ReplyToolCall).Select(e => e.GetType()));
            Assert.Equal("Your order is delayed. An agent will follow up [1].", string.Concat(events.OfType<ReplyToken>().Select(t => t.Text)));
            Assert.Single(events.OfType<ReplyCitations>().Single().Citations);
        }

        var doneA = tabA.OfType<ReplyDone>().Single();
        var doneB = tabB.OfType<ReplyDone>().Single();
        Assert.NotEqual(doneA.ReplyRunId, doneB.ReplyRunId);
        var proposalA = tabA.OfType<ReplyProposal>().Single().Proposal;
        var proposalB = tabB.OfType<ReplyProposal>().Single().Proposal;
        Assert.NotEqual(proposalA.Id, proposalB.Id);
        // Each tab was told about the proposal saved for its own run
        Assert.Equal(proposalA.Id, rig.FollowUps.IdOf(7, doneA.ReplyRunId));
        Assert.Equal(proposalB.Id, rig.FollowUps.IdOf(7, doneB.ReplyRunId));
        Assert.Equal(2, rig.FollowUps.Count);
        Assert.Equal(2, rig.Orders.Lookups.Count);
        // Two model calls per run, all successful, each under its own run
        Assert.Equal(4, rig.Rows.Count);
        Assert.All(rig.Rows, r => Assert.Equal(ModelCallOutcome.Success, r.Outcome));
        Assert.Equal([2, 2], rig.Rows.GroupBy(r => r.ReplyRunId).Select(g => g.Count()));
        Assert.Equal(new[] { doneA.ReplyRunId, doneB.ReplyRunId }.Order(), rig.Rows.Select(r => r.ReplyRunId!.Value).Distinct().Order());
    }

    // Size

    [Theory]
    [InlineData("word ")]
    [InlineData("</ticket>")]
    [InlineData("é中\U0001F600")]
    public async Task A_ticket_at_the_length_limit_with_ten_long_chunks_is_trimmed_to_the_budget_and_still_gets_a_reply(string unit)
    {
        var body = string.Concat(Enumerable.Repeat(unit, (SupportDesk.Core.Tickets.Ticket.BodyMaxLength / unit.Length) + 1))[..SupportDesk.Core.Tickets.Ticket.BodyMaxLength];
        Assert.Equal(SupportDesk.Core.Tickets.Ticket.BodyMaxLength, body.Length);
        var chunks = Enumerable.Range(1, 10)
            .Select(i => ReplyTestData.Chunk($"article-{i}", 1.0 - (i / 100.0), text: string.Concat(Enumerable.Repeat($"Sentence {i} about shipping and returns. ", 30))))
            .ToArray();
        var retrieval = new StubRetrieval(chunks);
        // The model cites the first and the last of the ten sources it was offered
        using var rig = Create(Turn(Text("Shipping is free [1], returns take a month [10]."), Usage(900, 30)), retrieval);

        var events = await rig.RunAsync(Ticket(body: body), cancellationToken: Ct);

        StreamAssert.EndsOnce(events);
        Assert.IsType<ReplyDone>(events[^1]);
        var call = Assert.Single(rig.Spy.Calls);
        var kept = SourceLine().Count(call.User.Text);
        Assert.InRange(kept, 1, 9);
        Assert.Contains(rig.Logs, m => m.Contains($"dropped {10 - kept} chunks over the token budget and kept {kept}", StringComparison.Ordinal));
        // The lowest-scored chunks went first, so the kept ones are the first n
        Assert.Equal(Enumerable.Range(1, kept).Select(i => $"[{i}] Title of article-{i}"), Regex.Matches(call.User.Text, @"(?m)^\[\d+\] Title of article-\d+").Select(m => m.Value));
        // Citations can only name a source the model was shown
        var citations = events.OfType<ReplyCitations>().Single();
        Assert.Equal([1], citations.Citations.Select(c => c.Marker));
        Assert.Equal([10], citations.Removed);
        // The retrieval query is cut to its limit, whatever the body
        Assert.InRange(Assert.Single(retrieval.Calls).Query.Length, 1, ReplyPromptBuilder.QueryMaxLength);
        Assert.Equal(ModelCallOutcome.Success, Assert.Single(rig.Rows).Outcome);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(5, Ct);
        }
    }
}

using Microsoft.Extensions.AI;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.FollowUps;
using SupportDesk.Core.Orders;
using SupportDesk.Core.Replies;
using SupportDesk.UnitTests.AI;
using SupportDesk.UnitTests.Replies;
using static SupportDesk.UnitTests.Adversarial.ReplyRig;

namespace SupportDesk.UnitTests.Adversarial;

/// <summary>
/// Step 9, provider faults (PLAN.md 6.5, 7): the fake provider fails the way real ones do, on a fake clock, through the
/// whole pipeline. Every fault must end the stream with exactly one <c>error</c> (or a <c>done</c>), and every call
/// the pipeline makes leaves one <see cref="ModelCall"/> row, however many attempts it took.
/// </summary>
public sealed class ProviderFaultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StubOrders KnownOrder() => new(new OrderStatus("KO-205002", "Delayed", "FedEx", new DateTimeOffset(2026, 10, 11, 7, 30, 0, TimeSpan.Zero), "Weather delay"));

    // HTTP errors

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(529)]
    public async Task A_transient_status_is_retried_to_the_limit_then_ends_in_one_model_unavailable_error(int status)
    {
        using var rig = Create(Turn(Error(status)));

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        var error = StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        Assert.Equal([error], events);
        Assert.Equal(3, rig.Spy.Calls.Count);
        // Backoff between attempts: at least half the base delay (the jitter's lower end)
        Assert.All(rig.Spy.Calls.Zip(rig.Spy.Calls.Skip(1)), pair => Assert.True(pair.Second.At - pair.First.At >= TimeSpan.FromMilliseconds(50)));
        var row = Assert.Single(rig.Rows);
        Assert.Equal(ModelCallOutcome.Error, row.Outcome);
        Assert.Contains(rig.Logs, m => m.Contains($"(status {status})", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task A_status_that_trying_again_cannot_fix_is_not_retried(int status)
    {
        using var rig = Create(Turn(Error(status)));

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        Assert.Single(rig.Spy.Calls);
        Assert.Equal(ModelCallOutcome.Error, Assert.Single(rig.Rows).Outcome);
    }

    [Fact]
    public async Task A_429_with_retry_after_waits_exactly_that_long_instead_of_backing_off()
    {
        using var rig = Create(Turn(Error(429, retryAfterSeconds: 2)));

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        var calls = rig.Spy.Calls;
        Assert.Equal(3, calls.Count);
        Assert.All(calls.Zip(calls.Skip(1)), pair => Assert.InRange(pair.Second.At - pair.First.At, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.25)));
        Assert.Single(rig.Rows);
    }

    [Fact]
    public async Task A_429_whose_retry_after_is_over_the_cap_fails_at_once_without_waiting()
    {
        using var rig = Create(Turn(Error(429, retryAfterSeconds: 60)));
        var start = rig.Time.GetUtcNow();

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        Assert.Single(rig.Spy.Calls);
        Assert.True(rig.Time.GetUtcNow() - start < TimeSpan.FromSeconds(1), "The call waited for the provider's Retry-After instead of failing.");
        Assert.Single(rig.Rows);
    }

    [Fact]
    public async Task A_429_with_retry_after_that_then_succeeds_is_one_row_and_a_normal_reply()
    {
        // The fake provider is stateless, so "fail once, then answer" is a scripted client (PLAN.md 6.8)
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        var provider = new ScriptedChatClient(
            time,
            ScriptedChatClient.Steps(ScriptedChatClient.Fail(429, retryAfterSeconds: 2)),
            ScriptedChatClient.Steps(ScriptedChatClient.Text("Sorry for the wait."), ScriptedChatClient.Usage(300, 10)));
        using var pipeline = TestPipeline.Create(provider, time, new SupportDesk.Core.AI.Fake.FakeErrorClassifier());
        var updates = new List<ChatResponseUpdate>();

        await time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hi")], new SupportDesk.Core.AI.AiCallContext(SupportDesk.Core.AI.AiFeature.Reply, "reply.v2", 1, "T-1", Guid.NewGuid()).ApplyTo(new ChatOptions()), Ct), updates, TimeSpan.FromMilliseconds(250));

        Assert.Equal("Sorry for the wait.", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal(2, provider.Calls);
        Assert.True(provider.CallTimes[1] - provider.CallTimes[0] >= TimeSpan.FromSeconds(2));
        var row = Assert.Single(pipeline.Rows.All);
        Assert.Equal((ModelCallOutcome.Success, 300L, 10L), (row.Outcome, row.InputTokens, row.OutputTokens));
    }

    // Connection

    [Fact]
    public async Task A_connection_reset_before_the_first_item_is_retried_then_ends_in_one_model_unavailable_error()
    {
        using var rig = Create(Turn(Reset()));

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        Assert.Equal(3, rig.Spy.Calls.Count);
        Assert.Equal(ModelCallOutcome.Error, Assert.Single(rig.Rows).Outcome);
    }

    [Fact]
    public async Task A_connection_reset_after_some_text_is_not_retried_and_ends_the_stream_with_one_error()
    {
        using var rig = Create(Turn(Text("Thanks for waiting. "), Reset()));

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        Assert.All(events.Take(events.Count - 1), e => Assert.IsType<ReplyToken>(e));
        Assert.NotEmpty(events.OfType<ReplyToken>());
        StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        Assert.Single(rig.Spy.Calls);
        Assert.Equal(ModelCallOutcome.Error, Assert.Single(rig.Rows).Outcome);
    }

    // Timeouts and stalls

    [Fact]
    public async Task No_first_item_within_the_limit_times_out_on_every_attempt_and_ends_in_one_timeout_error()
    {
        using var rig = Create(Turn(Stall(60), Text("too late")));
        var start = rig.Time.GetUtcNow();

        var events = await rig.RunAsync(Ticket(), TimeSpan.FromSeconds(1), Ct);

        Assert.Equal([ReplyErrorCodes.Timeout], events.OfType<ReplyError>().Select(e => e.Code));
        StreamAssert.Error(events, ReplyErrorCodes.Timeout);
        Assert.Equal([events[^1]], events);
        // Three attempts of FirstItemSeconds (20) each, with the backoff between them
        Assert.Equal(3, rig.Spy.Calls.Count);
        Assert.InRange(rig.Time.GetUtcNow() - start, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65));
        Assert.Equal(ModelCallOutcome.Error, Assert.Single(rig.Rows).Outcome);
    }

    [Fact]
    public async Task A_stall_longer_than_the_gap_timeout_after_some_text_ends_in_one_timeout_error_without_a_retry()
    {
        using var rig = Create(Turn(Text("Thanks for your patience. "), Stall(30), Text("never seen")));
        var start = rig.Time.GetUtcNow();

        var events = await rig.RunAsync(Ticket(), TimeSpan.FromSeconds(1), Ct);

        StreamAssert.Error(events, ReplyErrorCodes.Timeout);
        Assert.Equal("Thanks for your patience. ", string.Concat(events.OfType<ReplyToken>().Select(t => t.Text)));
        Assert.Single(rig.Spy.Calls);
        // GapSeconds (15) after the last text, not the 30 the provider would have taken
        Assert.InRange(rig.Time.GetUtcNow() - start, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(17));
        Assert.Equal(ModelCallOutcome.Error, Assert.Single(rig.Rows).Outcome);
    }

    [Fact]
    public async Task A_pause_shorter_than_the_gap_timeout_is_not_a_fault()
    {
        using var rig = Create(Turn(Text("Thanks for your patience. "), Stall(10), Text("Here is the answer [1]."), Usage()));

        var events = await rig.RunAsync(Ticket(), TimeSpan.FromSeconds(1), Ct);

        StreamAssert.EndsOnce(events);
        Assert.IsType<ReplyDone>(events[^1]);
        Assert.Equal("Thanks for your patience. Here is the answer [1].", string.Concat(events.OfType<ReplyToken>().Select(t => t.Text)));
    }

    [Fact]
    public async Task A_stream_that_keeps_trickling_past_the_total_limit_ends_in_one_timeout_error()
    {
        // 25 chunks, 5 seconds apart: each gap is fine, the whole is over StreamTotalSeconds (120)
        using var rig = Create(Turn(Text(new string('x', 12 * 25), delayMs: 5000)));

        var events = await rig.RunAsync(Ticket(), TimeSpan.FromSeconds(1), Ct);

        StreamAssert.Error(events, ReplyErrorCodes.Timeout);
        Assert.InRange(events.OfType<ReplyToken>().Count(), 20, 24);
        Assert.Single(rig.Spy.Calls);
        Assert.Equal(ModelCallOutcome.Error, Assert.Single(rig.Rows).Outcome);
    }

    // Errors inside the stream

    [Fact]
    public async Task An_error_inside_the_stream_after_text_ends_it_with_the_text_so_far_and_one_error()
    {
        using var rig = Create(Turn(Text("Your parcel left our warehouse "), Error(503), Text("never seen")));

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        Assert.Equal("Your parcel left our warehouse ", string.Concat(events.OfType<ReplyToken>().Select(t => t.Text)));
        // Text already went to the client, so nothing is retried: a second answer would repeat it
        Assert.Single(rig.Spy.Calls);
        Assert.Equal(ModelCallOutcome.Error, Assert.Single(rig.Rows).Outcome);
    }

    [Fact]
    public async Task An_error_after_a_tool_call_ends_the_stream_after_the_tool_event_with_one_row_per_call()
    {
        using var rig = Create(Turns(Turn(Lookup("KO-205002"), Usage(400, 20)), Turn(Error(503))), orders: KnownOrder());

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        Assert.Equal([typeof(ReplyOrderLookup), typeof(ReplyError)], events.Select(e => e.GetType()));
        // The first call, then the second one tried three times; the retries are not extra rows
        Assert.Equal(1 + 3, rig.Spy.Calls.Count);
        Assert.Equal([ModelCallOutcome.Success, ModelCallOutcome.Error], rig.Rows.Select(r => r.Outcome));
        Assert.Single(rig.Rows.Select(r => r.ReplyRunId).Distinct());
        Assert.Single(rig.Orders.Lookups);
    }

    [Fact]
    public async Task An_error_after_a_proposal_keeps_the_one_pending_proposal_and_never_confirms_it()
    {
        using var rig = Create(Turns(Turn(Propose("Open a carrier trace", "Late.", 2), Usage()), Turn(Reset(), Text("never"))));

        var events = await rig.RunAsync(Ticket(id: 7), cancellationToken: Ct);

        StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        var proposal = Assert.Single(events.OfType<ReplyProposal>()).Proposal;
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Equal(1, rig.FollowUps.Count);
        Assert.Equal(0, rig.FollowUps.Confirms);
        Assert.Equal(2, rig.Rows.Count);
    }

    // Usage

    [Fact]
    public async Task A_stream_that_never_sends_usage_still_ends_in_done_with_an_estimate_and_an_estimated_row()
    {
        using var rig = Create(Turn(Text("Standard shipping takes 3 to 5 business days [1].")));

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        StreamAssert.EndsOnce(events);
        var usage = events.OfType<ReplyUsage>().Single();
        Assert.True(usage.Estimated);
        Assert.True(usage.InputTokens > 0);
        Assert.True(usage.OutputTokens > 0);
        var row = Assert.Single(rig.Rows);
        Assert.Equal((ModelCallOutcome.Success, true), (row.Outcome, row.IsEstimated));
        Assert.Equal((usage.InputTokens, usage.OutputTokens), (row.InputTokens, row.OutputTokens));
    }

    // Refusals

    [Fact]
    public async Task A_refusal_after_some_text_ends_in_one_model_refused_error_and_keeps_the_usage()
    {
        using var rig = Create(Turn(Text("I can't help with "), Usage(310, 6), Finish("content_filter")));

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        StreamAssert.Error(events, ReplyErrorCodes.ModelRefused);
        Assert.Single(rig.Spy.Calls);
        var row = Assert.Single(rig.Rows);
        Assert.Equal((ModelCallOutcome.Error, 310L, 6L, false), (row.Outcome, row.InputTokens, row.OutputTokens, row.IsEstimated));
    }

    [Fact]
    public async Task A_refusal_without_any_text_is_the_only_event()
    {
        using var rig = Create(Turn(Usage(310, 0), Finish("content_filter")));

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        Assert.Equal(ReplyErrorCodes.ModelRefused, Assert.IsType<ReplyError>(Assert.Single(events)).Code);
        Assert.Single(rig.Spy.Calls);
        Assert.Single(rig.Rows);
    }

    [Fact]
    public async Task A_refusal_after_a_tool_call_ends_the_stream_after_the_tool_event()
    {
        using var rig = Create(Turns(Turn(Lookup("KO-205002"), Usage()), Turn(Finish("content_filter"), Usage())), orders: KnownOrder());

        var events = await rig.RunAsync(Ticket(), cancellationToken: Ct);

        StreamAssert.Error(events, ReplyErrorCodes.ModelRefused);
        Assert.Equal([typeof(ReplyOrderLookup), typeof(ReplyError)], events.Select(e => e.GetType()));
        Assert.Equal(2, rig.Spy.Calls.Count);
        Assert.Equal([ModelCallOutcome.Success, ModelCallOutcome.Error], rig.Rows.Select(r => r.Outcome));
    }

    // What every fault shares

    [Fact]
    public async Task Error_events_carry_fixed_text_never_the_providers_message_or_the_ticket()
    {
        using var rig = Create(Turn(Text("partial "), Error(500)));
        var ticket = Ticket(subject: "SECRET-SUBJECT", body: "SECRET-BODY order KO-123456");

        var events = await rig.RunAsync(ticket, cancellationToken: Ct);

        var error = StreamAssert.Error(events, ReplyErrorCodes.ModelUnavailable);
        Assert.Equal("The model is unavailable right now. Try again in a moment.", error.Message);
        Assert.DoesNotContain(rig.Logs, m => m.Contains("SECRET", StringComparison.Ordinal));
    }
}

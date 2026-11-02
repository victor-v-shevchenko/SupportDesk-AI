using Microsoft.Extensions.AI;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Fake;
using SupportDesk.Core.Replies;
using SupportDesk.UnitTests.Adversarial;
using static SupportDesk.UnitTests.AI.ScriptedChatClient;

namespace SupportDesk.UnitTests.AI;

/// <summary>
/// The fake-clock helpers must move time only while the code under test waits on a timer. A busy machine makes the code
/// slow to react once a timer fires; here the timers fire 30 ms late in real time, which must change nothing in fake time.
/// </summary>
public class SlowMachineTimingTests
{
    private static readonly TimeSpan Late = TimeSpan.FromMilliseconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatOptions Reply => new AiCallContext(AiFeature.Reply, "reply.v1", TicketId: 1, TicketNumber: "T-1001").ApplyTo(new ChatOptions());

    [Fact]
    public async Task A_first_item_timeout_is_retried_after_exactly_the_backoff_however_late_the_timers_fire()
    {
        var time = new TrackedFakeTime(callbackLatency: Late);
        var provider = new ScriptedChatClient(time, Steps(Empty(), Wait(time, TimeSpan.FromSeconds(25)), Text("late")));
        using var pipeline = TestPipeline.Create(provider, time, new FakeErrorClassifier());
        var received = new List<ChatResponseUpdate>();

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() =>
            time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct), received));

        Assert.True(error.IsTimeout);
        Assert.Equal(3, provider.Calls);
        // 20 s first-item timeout, then a backoff of at most one second (PLAN.md 6.5)
        Assert.InRange((provider.CallTimes[1] - provider.CallTimes[0]).TotalSeconds, 20.2, 21);
    }

    [Fact]
    public async Task A_gap_timeout_after_some_text_is_exactly_GapSeconds_however_late_the_timers_fire()
    {
        var time = new TrackedFakeTime(callbackLatency: Late);
        var provider = new ScriptedChatClient(time, Steps(Text("Hel"), Wait(time, TimeSpan.FromSeconds(30)), Text("lo")));
        using var pipeline = TestPipeline.Create(provider, time, new FakeErrorClassifier());
        var received = new List<ChatResponseUpdate>();
        var start = time.GetUtcNow();

        await Assert.ThrowsAsync<ModelUnavailableException>(() =>
            time.ReadAllAsync(pipeline.Client.GetStreamingResponseAsync("hi", Reply, Ct), received));

        Assert.Single(received);
        Assert.InRange((time.GetUtcNow() - start).TotalSeconds, 15, 15.01);
    }

    [Fact]
    public async Task A_reply_stall_after_some_text_ends_at_GapSeconds_however_late_the_timers_fire()
    {
        using var rig = ReplyRig.Create(
            ReplyRig.Turn(ReplyRig.Text("Thanks for your patience. "), ReplyRig.Stall(30), ReplyRig.Text("never seen")), callbackLatency: Late);
        var start = rig.Time.GetUtcNow();

        var events = await rig.RunAsync(ReplyRig.Ticket(), Ct);

        StreamAssert.Error(events, ReplyErrorCodes.Timeout);
        Assert.Single(rig.Spy.Calls);
        Assert.InRange(rig.Time.GetUtcNow() - start, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15.01));
    }
}

using SupportDesk.Eval.Suite;

namespace SupportDesk.Eval.Tests;

/// <summary>How the evaluation treats a ticket that breaks part-way, without a model or a container.</summary>
public class GuardedTicketRunTests
{
    public static TheoryData<Exception> TicketFailures => new()
    {
        new HttpRequestException(),
        new System.Text.Json.JsonException(),
        new InvalidOperationException(),
        new KeyNotFoundException(),
        new IOException("The response ended prematurely."),
    };

    [Fact]
    public async Task A_ticket_that_fails_after_it_was_created_keeps_its_ID_so_its_cost_is_counted()
    {
        List<int> ticketIds = [];

        var outcome = await GuardedTicketRun.RunAsync(EvalMetricsTests.Ticket("t-1"), ticketIds, (_, created) =>
        {
            created(42);
            throw new HttpRequestException("The reply stream broke.");
        });

        Assert.Equal([42], ticketIds);
        Assert.Equal("not run (HttpRequestException)", outcome.ReplyError);
        Assert.Equal("not run (HttpRequestException)", outcome.ClassificationError);
    }

    [Theory]
    [MemberData(nameof(TicketFailures))]
    public async Task A_failure_while_running_a_ticket_is_recorded_as_a_failed_ticket_not_a_stopped_run(Exception failure)
    {
        var outcome = await GuardedTicketRun.RunAsync(EvalMetricsTests.Ticket("t-1"), [], (_, _) => throw failure);

        Assert.Equal($"not run ({failure.GetType().Name})", outcome.ReplyError);
        Assert.Null(outcome.Category);
    }

    [Fact]
    public async Task Other_exceptions_still_stop_the_run()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            GuardedTicketRun.RunAsync(EvalMetricsTests.Ticket("t-1"), [], (_, _) => throw new ArgumentException("A bug in the suite.")));
    }

    [Fact]
    public async Task A_ticket_that_runs_returns_its_outcome_and_its_ID()
    {
        List<int> ticketIds = [];
        var expected = EvalMetricsTests.Outcome(EvalMetricsTests.Ticket("t-1"), null, null);

        var outcome = await GuardedTicketRun.RunAsync(expected.Ticket, ticketIds, (_, created) =>
        {
            created(7);
            return Task.FromResult(expected);
        });

        Assert.Same(expected, outcome);
        Assert.Equal([7], ticketIds);
    }
}

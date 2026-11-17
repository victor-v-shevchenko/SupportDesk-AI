using System.Diagnostics;
using SupportDesk.Core.FollowUps;
using SupportDesk.Core.Telemetry;
using SupportDesk.UnitTests.Telemetry;

namespace SupportDesk.UnitTests.FollowUps;

/// <summary>
/// The confirm's span and <c>supportdesk.follow_ups</c> count (PLAN.md 14), written by the service from what the repository
/// reports; the integration TracingTests and MetricsTests run the same path against the database.
/// </summary>
public class FollowUpServiceTests
{
    private static readonly Guid ProposalId = Guid.Parse("0195f0a0-0000-7000-8000-000000000001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confirm_that_creates_the_task_counts_confirmed_and_tags_the_span()
    {
        using var trace = new TraceCapture();
        using var metrics = new TestMetrics();
        var service = new FollowUpService(new Repository(new ConfirmResult(ConfirmOutcome.Confirmed, Task(), RowsChanged: 2)), metrics.Metrics);

        var result = await service.ConfirmAsync(ProposalId, Ct);

        Assert.Equal(ConfirmOutcome.Confirmed, result.Outcome);
        Assert.Equal(1, TestMetrics.Sum(metrics.FollowUps, ("event", MetricValues.Confirmed)));
        var span = Assert.Single(trace.Named(FollowUpService.ConfirmSpanName));
        Assert.Equal(ProposalId.ToString(), span.GetTagItem("supportdesk.proposal_id"));
        Assert.Equal("Confirmed", span.GetTagItem(SupportDeskTelemetry.Outcome));
        Assert.Equal(2, span.GetTagItem(SupportDeskTelemetry.RowsChanged));
    }

    [Fact]
    public async Task A_repeat_confirm_returns_the_task_without_counting_it_again()
    {
        using var trace = new TraceCapture();
        using var metrics = new TestMetrics();
        var service = new FollowUpService(new Repository(new ConfirmResult(ConfirmOutcome.Confirmed, Task(), RowsChanged: 0)), metrics.Metrics);

        await service.ConfirmAsync(ProposalId, Ct);

        Assert.Empty(metrics.FollowUps.GetMeasurementSnapshot());
        Assert.Equal(0, Assert.Single(trace.Named(FollowUpService.ConfirmSpanName)).GetTagItem(SupportDeskTelemetry.RowsChanged));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public async Task Expired_is_counted_only_when_this_confirm_marked_the_proposal(int rowsChanged, long counted)
    {
        using var trace = new TraceCapture();
        using var metrics = new TestMetrics();
        var service = new FollowUpService(new Repository(new ConfirmResult(ConfirmOutcome.Expired, null, rowsChanged)), metrics.Metrics);

        await service.ConfirmAsync(ProposalId, Ct);

        Assert.Equal(counted, TestMetrics.Sum(metrics.FollowUps, ("event", MetricValues.Expired)));
        Assert.Equal(0, TestMetrics.Sum(metrics.FollowUps, ("event", MetricValues.Confirmed)));
        Assert.Equal("Expired", Assert.Single(trace.Named(FollowUpService.ConfirmSpanName)).GetTagItem(SupportDeskTelemetry.Outcome));
    }

    [Fact]
    public async Task An_unknown_proposal_counts_nothing()
    {
        using var trace = new TraceCapture();
        using var metrics = new TestMetrics();
        var service = new FollowUpService(new Repository(new ConfirmResult(ConfirmOutcome.NotFound, null)), metrics.Metrics);

        await service.ConfirmAsync(ProposalId, Ct);

        Assert.Empty(metrics.FollowUps.GetMeasurementSnapshot());
        Assert.Equal("NotFound", Assert.Single(trace.Named(FollowUpService.ConfirmSpanName)).GetTagItem(SupportDeskTelemetry.Outcome));
    }

    [Fact]
    public async Task A_repository_failure_marks_the_span_failed_and_goes_on()
    {
        using var trace = new TraceCapture();
        using var metrics = new TestMetrics();
        var service = new FollowUpService(new Repository(null) { Failure = new InvalidOperationException("database down") }, metrics.Metrics);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(ProposalId, Ct));

        var span = Assert.Single(trace.Named(FollowUpService.ConfirmSpanName));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Empty(metrics.FollowUps.GetMeasurementSnapshot());
    }

    private static FollowUpTask Task() => new() { Id = 7, TicketId = 1, ProposalId = ProposalId, Title = "Call back", Notes = "" };

    private sealed class Repository(ConfirmResult? result) : IFollowUpRepository
    {
        public Exception? Failure { get; init; }

        public Task<ConfirmResult> ConfirmAsync(Guid proposalId, CancellationToken cancellationToken) =>
            Failure is null ? System.Threading.Tasks.Task.FromResult(result!) : System.Threading.Tasks.Task.FromException<ConfirmResult>(Failure);

        public Task<FollowUpProposal> UpsertProposalAsync(NewProposal proposal, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TicketFollowUps?> ListAsync(int ticketId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> ExpireAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

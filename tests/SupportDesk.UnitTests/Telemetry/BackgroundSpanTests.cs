using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SupportDesk.Api;
using SupportDesk.Core.FollowUps;
using SupportDesk.Core.Telemetry;
using SupportDesk.UnitTests.AI;

namespace SupportDesk.UnitTests.Telemetry;

/// <summary>The spans of work a timer starts (PLAN.md 14). Alone in the collection: it looks at every span the provider exports.</summary>
[Collection(FakeClockGroup.Name)]
public class BackgroundSpanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_expiry_check_is_a_root_span_even_when_something_else_is_current()
    {
        using var trace = new TraceCapture();
        using var services = Services(new Repository(Expired: 4242));
        var service = new ProposalExpiryService(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<ProposalExpiryService>.Instance);

        // TraceCapture's own span is current here, as a request's would be
        Assert.NotNull(Activity.Current);
        await service.ExpireAsync(Ct);

        var span = Assert.Single(trace.AllSpans, a => a.OperationName == "proposal_expiry");
        Assert.Equal(default, span.ParentSpanId);
        Assert.NotEqual(trace.TraceId, span.TraceId);
        Assert.Equal(4242, span.GetTagItem(SupportDeskTelemetry.RowsChanged));
    }

    [Fact]
    public async Task A_run_cut_short_by_shutdown_is_tagged_cancelled_and_is_not_an_error()
    {
        using var trace = new TraceCapture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var services = Services(new Repository(Expired: 0) { Failure = new OperationCanceledException(cts.Token) });
        var service = new ProposalExpiryService(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<ProposalExpiryService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExpireAsync(cts.Token));

        var span = Assert.Single(trace.AllSpans, a => a.OperationName == "proposal_expiry");
        Assert.Equal(true, span.GetTagItem(SupportDeskTelemetry.Cancelled));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    private static ServiceProvider Services(IFollowUpRepository repository) =>
        new ServiceCollection().AddScoped(_ => repository).BuildServiceProvider();

    private sealed record Repository(int Expired) : IFollowUpRepository
    {
        public Exception? Failure { get; init; }

        public Task<int> ExpireAsync(CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Expired) : throw Failure;

        public Task<FollowUpProposal> UpsertProposalAsync(NewProposal proposal, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConfirmResult> ConfirmAsync(Guid proposalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TicketFollowUps?> ListAsync(int ticketId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

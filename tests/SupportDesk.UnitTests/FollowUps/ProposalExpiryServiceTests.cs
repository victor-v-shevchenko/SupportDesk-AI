using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Api;
using SupportDesk.Core.FollowUps;
using SupportDesk.UnitTests.AI;

namespace SupportDesk.UnitTests.FollowUps;

/// <summary>When the expiry check runs (PLAN.md 10); what it marks is the repository's job, covered by integration tests.</summary>
public class ProposalExpiryServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Expiry_runs_at_startup_then_every_ten_minutes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var repository = new CountingRepository();
        using var services = Services(repository);
        using var service = new ProposalExpiryService(services.GetRequiredService<IServiceScopeFactory>(), time, NullLogger<ProposalExpiryService>.Instance);

        await service.StartAsync(Ct);
        await WaitForAsync(() => repository.Runs == 1);

        time.Advance(TimeSpan.FromMinutes(9));
        await Task.Delay(50, Ct);
        Assert.Equal(1, repository.Runs);

        time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => repository.Runs == 2);
        time.Advance(FollowUpRules.ExpiryCheckInterval);
        await WaitForAsync(() => repository.Runs == 3);

        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task A_failed_run_is_logged_and_the_next_run_still_happens()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var repository = new CountingRepository { FailFirst = true };
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var services = Services(repository);
        using var service = new ProposalExpiryService(services.GetRequiredService<IServiceScopeFactory>(), time, loggerFactory.CreateLogger<ProposalExpiryService>());

        await service.StartAsync(Ct);
        await WaitForAsync(() => repository.Runs == 1);
        time.Advance(FollowUpRules.ExpiryCheckInterval);
        await WaitForAsync(() => repository.Runs == 2);
        await service.StopAsync(Ct);

        Assert.Contains(logs.Messages, m => m.Contains("expiry check failed", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m => m.Contains("Marked 1 follow-up proposals as expired", StringComparison.Ordinal));
    }

    private static ServiceProvider Services(IFollowUpRepository repository) =>
        new ServiceCollection().AddScoped(_ => repository).BuildServiceProvider();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class CountingRepository : IFollowUpRepository
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public bool FailFirst { get; init; }

        public Task<int> ExpireAsync(CancellationToken cancellationToken)
        {
            var run = Interlocked.Increment(ref _runs);
            return FailFirst && run == 1 ? throw new InvalidOperationException("database down") : Task.FromResult(1);
        }

        public Task<FollowUpProposal> UpsertProposalAsync(NewProposal proposal, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConfirmResult> ConfirmAsync(Guid proposalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TicketFollowUps?> ListAsync(int ticketId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

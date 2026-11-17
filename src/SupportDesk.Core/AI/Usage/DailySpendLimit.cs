using Microsoft.Extensions.Options;

namespace SupportDesk.Core.AI.Usage;

/// <summary>Today's model spend against <c>AI:Budget:DailySpendLimitUsd</c>, and when the day resets.</summary>
public sealed record SpendStatus(decimal SpentUsd, decimal LimitUsd, DateTimeOffset ResetsAt)
{
    public bool IsReached => SpentUsd >= LimitUsd;
}

/// <summary>
/// The daily spending limit (PLAN.md 6.4): the summed cost of today's <c>ModelCalls</c> rows, where today is the UTC day.
/// Checked when an AI request starts, so it's a soft limit: requests already running finish, and rows reach the table a
/// moment after their call.
/// </summary>
public sealed class DailySpendLimit(IModelCallRepository calls, IOptions<AiOptions> options, TimeProvider time)
{
    public async Task<SpendStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var dayStart = new DateTimeOffset(time.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        var spent = await calls.SumCostSinceAsync(dayStart, cancellationToken);
        return new SpendStatus(spent, options.Value.Budget.DailySpendLimitUsd, dayStart.AddDays(1));
    }
}

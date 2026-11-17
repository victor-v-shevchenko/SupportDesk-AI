using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SupportDesk.Api;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.UnitTests.AI;

/// <summary>The daily spending limit's arithmetic and the request limits' setting ranges (PLAN.md 5, 6.4).</summary>
public class DailySpendLimitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 21, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_spend_is_counted_from_midnight_UTC_and_resets_at_the_next_one()
    {
        // A local offset, so a check that used the clock's local day would ask from the wrong midnight
        var time = new FakeTimeProvider(Now.ToOffset(TimeSpan.FromHours(-8)));
        var calls = new SummingRepository(1.25m);

        var status = await Limit(calls, 5m, time).CheckAsync(Ct);

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), calls.Since);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero), status.ResetsAt);
        Assert.Equal(1.25m, status.SpentUsd);
        Assert.Equal(5m, status.LimitUsd);
    }

    [Theory]
    [InlineData("4.99", false)]
    [InlineData("5.00", true)]
    [InlineData("7.10", true)]
    public async Task The_limit_is_reached_at_and_above_the_setting(string spent, bool reached)
    {
        var status = await Limit(new SummingRepository(decimal.Parse(spent, System.Globalization.CultureInfo.InvariantCulture)), 5m, new FakeTimeProvider(Now)).CheckAsync(Ct);

        Assert.Equal(reached, status.IsReached);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("0.001", false)]
    [InlineData("0.01", true)]
    [InlineData("1000000", true)]
    [InlineData("1000000.01", false)]
    public void The_daily_limit_setting_must_be_in_range(string value, bool valid)
    {
        var options = new AiOptions { Budget = { DailySpendLimitUsd = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) } };

        var result = new AiOptionsValidator().Validate(null, options);

        // Other settings of a bare AiOptions fail too (no prompts), so only this setting's message is looked at
        Assert.Equal(!valid, result.Failures?.Any(f => f.StartsWith("AI:Budget:DailySpendLimitUsd", StringComparison.Ordinal)) ?? false);
    }

    [Theory]
    [InlineData("0", "60", false)]
    [InlineData("1", "60", true)]
    [InlineData("100000", "3600", true)]
    [InlineData("20", "0", false)]
    [InlineData("20", "3601", false)]
    public void The_rate_limit_settings_are_validated_where_the_app_registers_them(string permitLimit, string windowSeconds, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:Ai:PermitLimit"] = permitLimit,
            ["RateLimiting:Ai:WindowSeconds"] = windowSeconds,
        }).Build();
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddSupportDeskRequestLimits().BuildServiceProvider();

        var failure = Record.Exception(() => services.GetRequiredService<IOptions<AiRateLimitOptions>>().Value);

        Assert.Equal(valid, failure is null);
        if (!valid)
        {
            Assert.IsType<OptionsValidationException>(failure);
        }
    }

    [Fact]
    public async Task A_request_over_the_daily_limit_is_refused_with_retry_after_and_a_warning_holding_only_the_amounts()
    {
        var time = new FakeTimeProvider(Now);
        var logger = new FakeLogger<DailySpendLimitFilter>();
        var filter = new DailySpendLimitFilter(Limit(new SummingRepository(5.5m), 5m, time), time, logger);
        var http = new DefaultHttpContext();
        var handlerRan = false;

        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ =>
        {
            handlerRan = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.False(handlerRan);
        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.StatusCode);
        Assert.Equal("daily_limit", problem.ProblemDetails.Extensions["code"]);
        // 21:30 UTC, so midnight is 2.5 hours away
        Assert.Equal("9000", http.Response.Headers.RetryAfter.ToString());
        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Equal(["SpentUsd", "LimitUsd", "{OriginalFormat}"], record.StructuredState!.Select(p => p.Key));
    }

    [Fact]
    public async Task A_request_under_the_daily_limit_runs_its_handler()
    {
        var time = new FakeTimeProvider(Now);
        var logger = new FakeLogger<DailySpendLimitFilter>();
        var filter = new DailySpendLimitFilter(Limit(new SummingRepository(4.99m), 5m, time), time, logger);

        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(new DefaultHttpContext()), _ => ValueTask.FromResult<object?>("handled"));

        Assert.Equal("handled", result);
        Assert.Empty(logger.Collector.GetSnapshot());
    }

    private static DailySpendLimit Limit(IModelCallRepository calls, decimal limit, TimeProvider time) =>
        new(calls, Options.Create(new AiOptions { Budget = { DailySpendLimitUsd = limit } }), time);

    private sealed class SummingRepository(decimal total) : IModelCallRepository
    {
        public DateTimeOffset? Since { get; private set; }

        public Task<decimal> SumCostSinceAsync(DateTimeOffset since, CancellationToken cancellationToken)
        {
            Since = since;
            return Task.FromResult(total);
        }

        public Task AddRangeAsync(IReadOnlyList<ModelCall> calls, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ModelCall>> ListAsync(int? ticketId, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

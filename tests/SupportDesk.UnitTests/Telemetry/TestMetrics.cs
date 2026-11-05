using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.UnitTests.Telemetry;

/// <summary>
/// The app's counters on a meter factory of the test's own, with a collector per instrument made up front (a collector only
/// sees what is recorded after it exists). Nothing is shared between tests.
/// </summary>
internal sealed class TestMetrics : IDisposable
{
    private static readonly string[] CounterNames =
    [
        SupportDeskMetrics.ModelCallsName, SupportDeskMetrics.TokensName, SupportDeskMetrics.DroppedName, SupportDeskMetrics.RepliesName,
        SupportDeskMetrics.ToolCallsName, SupportDeskMetrics.GuardHitsName, SupportDeskMetrics.ClassificationsName, SupportDeskMetrics.FollowUpsName,
    ];

    private readonly ServiceProvider _services;
    private readonly Dictionary<string, MetricCollector<long>> _counters;

    public TestMetrics()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = _services.GetRequiredService<IMeterFactory>();
        Metrics = new SupportDeskMetrics(factory);
        _counters = CounterNames.ToDictionary(name => name, name => new MetricCollector<long>(factory, SupportDeskMetrics.MeterName, name));
        Cost = new MetricCollector<double>(factory, SupportDeskMetrics.MeterName, SupportDeskMetrics.CostName);
    }

    public SupportDeskMetrics Metrics { get; }

    public MetricCollector<double> Cost { get; }

    public MetricCollector<long> ModelCalls => _counters[SupportDeskMetrics.ModelCallsName];

    public MetricCollector<long> Tokens => _counters[SupportDeskMetrics.TokensName];

    public MetricCollector<long> Dropped => _counters[SupportDeskMetrics.DroppedName];

    public MetricCollector<long> Replies => _counters[SupportDeskMetrics.RepliesName];

    public MetricCollector<long> ToolCalls => _counters[SupportDeskMetrics.ToolCallsName];

    public MetricCollector<long> GuardHits => _counters[SupportDeskMetrics.GuardHitsName];

    public MetricCollector<long> Classifications => _counters[SupportDeskMetrics.ClassificationsName];

    public MetricCollector<long> FollowUps => _counters[SupportDeskMetrics.FollowUpsName];

    /// <summary>Every recorded measurement's instrument name and tags, for the allowed-values check.</summary>
    public IEnumerable<(string Instrument, IReadOnlyDictionary<string, object?> Tags)> AllTags() =>
        _counters.SelectMany(c => c.Value.GetMeasurementSnapshot().Select(m => (c.Key, m.Tags)))
            .Concat(Cost.GetMeasurementSnapshot().Select(m => (SupportDeskMetrics.CostName, m.Tags)));

    /// <summary>The sum of the measurements whose tags include all of <paramref name="tags"/>.</summary>
    public static long Sum(MetricCollector<long> collector, params (string Key, object Value)[] tags) =>
        collector.GetMeasurementSnapshot().Where(m => Matches(m.Tags, tags)).Sum(m => m.Value);

    public static double Sum(MetricCollector<double> collector, params (string Key, object Value)[] tags) =>
        collector.GetMeasurementSnapshot().Where(m => Matches(m.Tags, tags)).Sum(m => m.Value);

    public void Dispose() => _services.Dispose();

    private static bool Matches(IReadOnlyDictionary<string, object?> actual, (string Key, object Value)[] expected) =>
        expected.All(t => actual.TryGetValue(t.Key, out var value) && Equals(value, t.Value));
}

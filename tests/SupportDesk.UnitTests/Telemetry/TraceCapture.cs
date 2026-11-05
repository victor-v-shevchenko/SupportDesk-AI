using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using SupportDesk.Api.Telemetry;

namespace SupportDesk.UnitTests.Telemetry;

/// <summary>
/// The app's tracing set up as in the API (<c>AddSupportDeskSources</c>) with an in-memory exporter. Sources are
/// process-wide, so it opens a root span of its own and <see cref="Spans"/> keeps only that trace: spans other tests make
/// at the same time are not in it.
/// </summary>
internal sealed class TraceCapture : IDisposable
{
    private static readonly ActivitySource TestSource = new("SupportDesk.Tests");

    private readonly List<Activity> _exported = [];
    private readonly TracerProvider _provider;
    private readonly Activity _root;

    public TraceCapture()
    {
        _provider = Sdk.CreateTracerProviderBuilder()
            .AddSupportDeskSources()
            .AddSource(TestSource.Name)
            .AddInMemoryExporter(_exported)
            .Build();
        _root = TestSource.StartActivity("test")!;
    }

    /// <summary>The ended spans of this test's trace, root excluded, oldest first.</summary>
    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_exported)
            {
                return [.. _exported.Where(a => a.TraceId == _root.TraceId && a != _root).OrderBy(a => a.StartTimeUtc)];
            }
        }
    }

    /// <summary>The trace this capture's own root span started, to tell it from traces of their own.</summary>
    public ActivityTraceId TraceId => _root.TraceId;

    /// <summary>Every ended span the provider exported, of any trace.</summary>
    public IReadOnlyList<Activity> AllSpans
    {
        get
        {
            lock (_exported)
            {
                return [.. _exported];
            }
        }
    }

    public IReadOnlyList<Activity> Named(string name) => [.. Spans.Where(a => a.OperationName == name)];

    public IReadOnlyList<Activity> Chat => [.. Spans.Where(a => a.OperationName.StartsWith("chat", StringComparison.Ordinal))];

    public IReadOnlyList<Activity> Tools => [.. Spans.Where(a => a.OperationName.StartsWith("execute_tool", StringComparison.Ordinal))];

    /// <summary>Whether <paramref name="span"/> is below <paramref name="ancestor"/> in the trace.</summary>
    public bool IsDescendant(Activity span, Activity ancestor)
    {
        for (var parent = span.ParentSpanId; parent != default;)
        {
            if (parent == ancestor.SpanId)
            {
                return true;
            }

            parent = Spans.FirstOrDefault(a => a.SpanId == parent)?.ParentSpanId ?? default;
        }

        return false;
    }

    /// <summary>Every string a span carries: tags, events and their tags, links' tags, the status description, the name.</summary>
    public static IEnumerable<string> AllText(Activity span)
    {
        yield return span.OperationName;
        yield return span.DisplayName;
        yield return span.StatusDescription ?? "";
        foreach (var tag in span.TagObjects)
        {
            yield return tag.Key;
            yield return Render(tag.Value);
        }

        foreach (var spanEvent in span.Events)
        {
            yield return spanEvent.Name;
            foreach (var tag in spanEvent.Tags)
            {
                yield return tag.Key;
                yield return Render(tag.Value);
            }
        }

        foreach (var link in span.Links)
        {
            foreach (var tag in link.Tags ?? [])
            {
                yield return Render(tag.Value);
            }
        }

        foreach (var baggage in span.Baggage)
        {
            yield return Render(baggage.Value);
        }
    }

    private static string Render(object? value) => value switch
    {
        null => "",
        string text => text,
        System.Collections.IEnumerable items => string.Join("|", items.Cast<object?>().Select(Render)),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    public void Dispose()
    {
        _root.Stop();
        _provider.ForceFlush();
        _provider.Dispose();
    }
}

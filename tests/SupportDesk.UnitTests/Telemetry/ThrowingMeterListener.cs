using System.Diagnostics.Metrics;

namespace SupportDesk.UnitTests.Telemetry;

/// <summary>
/// A measurement listener that throws on every measurement of the meters made by one factory, like a faulty exporter. Counters
/// call their listeners synchronously, so the exception comes out of <c>Counter.Add</c>.
/// </summary>
internal sealed class ThrowingMeterListener : IDisposable
{
    private readonly MeterListener _listener = new();

    public ThrowingMeterListener(IMeterFactory scope)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            // Only the app's own meter: ASP.NET Core's meters share the factory, and a throwing listener there would break the request itself
            if (ReferenceEquals(instrument.Meter.Scope, scope) && instrument.Meter.Name == "SupportDesk")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("listener broke"));
        _listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("listener broke"));
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();
}

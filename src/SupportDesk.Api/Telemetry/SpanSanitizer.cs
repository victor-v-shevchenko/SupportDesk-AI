using System.Diagnostics;
using OpenTelemetry;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.Telemetry;

/// <summary>
/// Runs on every span as it ends, before the exporters (PLAN.md 14). Instrumentation libraries put an exception's message in
/// a failed span's status, and a provider's message can echo the request, which holds ticket text; so the status description
/// becomes the app's error code (or nothing). A span of a call the caller cancelled isn't an error, whatever the library
/// that wrote it thought.
/// </summary>
internal sealed class SpanSanitizer : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.GetTagItem(SupportDeskTelemetry.Cancelled) is true)
        {
            data.SetStatus(ActivityStatusCode.Unset);
            data.SetTag("error.type", null);
        }
        else if (data.Status == ActivityStatusCode.Error)
        {
            data.SetStatus(ActivityStatusCode.Error, data.GetTagItem(SupportDeskTelemetry.ErrorCode) as string);
        }
    }
}

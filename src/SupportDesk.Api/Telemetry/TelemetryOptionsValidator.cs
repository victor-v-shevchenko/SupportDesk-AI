using Microsoft.Extensions.Options;
using SupportDesk.Core.Telemetry;

namespace SupportDesk.Api.Telemetry;

/// <summary>Capturing message content puts ticket text in spans, so it is refused in <c>Production</c>, like the fake provider.</summary>
public sealed class TelemetryOptionsValidator(IHostEnvironment environment) : IValidateOptions<TelemetryOptions>
{
    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.CaptureMessageContent && environment.IsProduction()
            ? ValidateOptionsResult.Fail("Telemetry:CaptureMessageContent is true, which isn't allowed in Production: it puts prompts and ticket text in traces.")
            : ValidateOptionsResult.Success;
    }
}

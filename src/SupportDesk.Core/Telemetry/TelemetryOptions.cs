namespace SupportDesk.Core.Telemetry;

/// <summary>The <c>Telemetry</c> section (PLAN.md 14).</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Puts prompts, replies, tool arguments and tool results in the model-call spans. Off by default, and refused in
    /// <c>Production</c>. It is always set explicitly on the clients, so <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c> can't turn it on.
    /// </summary>
    public bool CaptureMessageContent { get; set; }
}

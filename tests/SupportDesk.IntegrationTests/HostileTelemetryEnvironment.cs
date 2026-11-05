using System.Runtime.CompilerServices;

namespace SupportDesk.IntegrationTests;

/// <summary>
/// Turns on Microsoft.Extensions.AI's content-capture variable for the whole test process (PLAN.md 14). The library reads it
/// once, when it first runs, so it has to be set before any test: a test can't set it itself. With it on, every test that
/// builds the pipeline checks that the app's own setting (<c>Telemetry:CaptureMessageContent</c>) wins over the environment.
/// </summary>
internal static class HostileTelemetryEnvironment
{
    public const string CaptureVariable = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    [ModuleInitializer]
    internal static void Set() => Environment.SetEnvironmentVariable(CaptureVariable, "true");
}

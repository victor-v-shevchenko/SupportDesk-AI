# Step 15: OpenTelemetry structured logs

- **Branch:** `step/15-logs`
- **Depends on:** step 14 (the OpenTelemetry setup in `AddSupportDeskTelemetry`, with tracing and metrics on one OTLP exporter and the Aspire dashboard)
- **Design:** PLAN.md 6.6 (what failures log), 9 (the classification rejection log), and the Telemetry section from steps 13 and 14

## Goal

The app's logs go to the same OTLP endpoint as its traces and metrics, as structured records: the message template, each placeholder as an attribute, the level, the category, and the trace and span of the work that wrote it. In the Aspire dashboard a log line opens its trace, and a trace lists its log lines. Logs leave the machine under the same rules as spans: no prompt, ticket, reply or tool text, and no exception messages. With no collector configured nothing changes, and the console log stays as it is today.

Every log call already uses `[LoggerMessage]` templates with IDs, counts and codes as values, so this step adds the export path and its safeguards, not new log lines.

## Scope

**Wiring (`AddSupportDeskTelemetry`)**
- `WithLogging(...)` beside `WithTracing` and `WithMetrics`, with the same resource (`OTEL_SERVICE_NAME`, default `supportdesk-api`) and the OTLP exporter. It's added only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. When it isn't, no OpenTelemetry logger provider is registered at all, the same rule as the tracer and meter providers
- The console provider stays, with its format (`json`, or `simple` in Development) unchanged. OTLP is an extra provider, not a replacement
- `IncludeFormattedMessage = true`, so the dashboard shows the line as the console does, and `IncludeScopes = true`, so ASP.NET Core's request scopes (`RequestPath`, `RequestId`) are attributes. `ParseStateValues` (a property of `OpenTelemetryLoggerOptions` in 1.20.1; the `[LoggerMessage]` state is parsed into attributes even without it, and with it on `LogRecord.State` is null) puts each template placeholder in an attribute, so records can be filtered by `TicketId`, `ReplyRunId`, `Feature` or `Code`
- Trace correlation comes from `Activity.Current` when the record is written, so a log written inside a `reply`, `summary`, `classification` or `model_calls.save` span carries that span's trace and span ID. The background services' logs are inside their root spans (step 13), so they correlate too
- Log levels follow the existing `Logging:LogLevel` filters. The provider alias `OpenTelemetry` (`Logging:OpenTelemetry:LogLevel`) can set a different level for the export; nothing is set by default, and PLAN.md documents the alias
- **No feedback loop:** exporting must not write log records that are exported in turn. Checked against OpenTelemetry 1.20.1: the OTLP exporter sends with a plain `HttpClient` (not one from `IHttpClientFactory`, so no `System.Net.Http.HttpClient.*` lines) and reports its own problems to an `EventSource`, not to `ILogger`. So no category filter is added. A test with the real exporter against a local sink checks that an idle app, an export and a failing export produce no log record of their own; if a later version of the exporter logs through `ILogger`, those categories are filtered out of the `OpenTelemetry` provider

**What leaves the machine**
- **No exception text.** The OTLP exporter writes a logged exception's type, message and stack trace (`exception.type`, `exception.message`, `exception.stacktrace`; the stack trace is `ToString()`, which includes every inner message). The messages can echo the request: a PostgreSQL error quotes values, and a provider SDK's message can quote the ticket. A `LogRecord` processor, `LogSanitizer`, runs before the exporter (processors run in the order they are added, so it is added before `AddOtlpExporter`). It sets `LogRecord.Exception` to null and adds `exception.type` as an attribute with the exception type only, the same rule as `SpanSanitizer` (step 13); the exporter writes `exception.*` only from `LogRecord.Exception`, so nothing else is written. The type of the innermost exception is kept as well (`supportdesk.inner_exception.type`), so a `DbUpdateException` that wraps a `PostgresException` still says so. This covers the five log calls that take an exception today (`ReplyService.LogUnexpected`, `ModelCallWriter.LogSaveFailed`, `ProposalExpiryService.LogFailed`, and the two in `SupportDeskMetrics`) and any added later. The console log keeps the full exception, as today
- **Content-bearing logs stay local.** One log call holds model output: the classification rejection at debug level (`Raw output: {Output}`, PLAN.md 9), and the output can echo ticket text. It gets a fixed event ID (`ContentLogEvents`), and any record with an event ID from the content set is dropped unless `Telemetry:CaptureMessageContent` is `true` (the setting is already refused in `Production`). A log processor can rewrite a record but not drop it, and the OTLP exporter can't be wrapped in a filter (it takes its resource from the provider that owns it), so `ContentLogFilteringProvider` does it: it wraps the OpenTelemetry `ILoggerProvider`, keeps its alias, and doesn't pass those events on. The console still writes it when the level allows, as today. Any later log call that takes prompt, ticket, reply or tool text has to join the set, and the review checks the new step's log calls for it
- **No other text attributes.** Scopes and state values are already IDs, counts, codes and names. Nothing else is added (no request body, no query string: `RequestPath` has none)

**Local viewing**
- The Aspire dashboard from step 13 has a Structured logs page, so nothing is added to Compose. The CLAUDE.md commands line for telemetry says logs are exported too

**Config**
- No new settings. `Telemetry:CaptureMessageContent` now covers the content-bearing logs as well as spans: its warning at startup and its PLAN.md row say so

**Tests** (CI needs no collector: `OpenTelemetry.Exporter.InMemory` for logs, copying each record's fields when it's exported, since the SDK reuses `LogRecord` objects)
- Unit: with export on, a log written inside a `reply` span has that span's trace and span ID; a log written outside a span has none
- Unit: an exception logged with a unique marker in its message and in an inner exception's message is exported with its type and inner type only, and the marker is in no attribute, body or exception field. A positive control without `LogSanitizer` shows the marker does reach the export, so the test isn't vacuous
- Unit: with the level at `Debug`, a rejected classification whose raw output is a unique marker exports no record holding the marker while `Telemetry:CaptureMessageContent` is `false`, and exports it when it's `true`
- Unit: with `OTEL_EXPORTER_OTLP_ENDPOINT` unset, no OpenTelemetry logger provider is registered (`TelemetryStartupTests`)
- Unit: an idle app with export on writes no log record triggered by its own export (the feedback-loop check above)
- Integration: a reply on T-1002 and a classification on a ticket whose body is a unique marker: every exported record from the requests carries the request's trace ID, the classification's records are inside its trace, and no exported record holds the marker
- Each test is shown to fail when its behaviour is broken (say how in the PR)

## Out of scope

- New log lines, changing the existing messages, or changing the console format
- Logs from the browser
- A hosted log backend, retention, and alerts
- Sampling or rate-limiting logs
- Exporting EF Core's SQL command logs (spans already show the SQL, step 13; the `Microsoft.EntityFrameworkCore.Database.Command` filter stays at `Warning`)

## Acceptance criteria

- [ ] With `OTEL_EXPORTER_OTLP_ENDPOINT` unset, no logs are exported, the console log is unchanged, and every test behaves as before
- [ ] With the `telemetry` profile up, the Aspire dashboard's Structured logs page shows the app's logs with their placeholders as attributes, and a reply's log lines link to its trace (screenshot in the PR)
- [ ] No exported log record holds an exception message, a stack trace, or prompt, ticket, reply or tool text unless `Telemetry:CaptureMessageContent` is `true`
- [ ] Exporting logs doesn't produce log records of its own
- [ ] PLAN.md's Telemetry section describes the log export, `LogSanitizer`, the content event set and the `OpenTelemetry` level alias; the "exporting logs is out of scope" lines in PLAN.md and specs 13 and 14 point to this step; README and CLAUDE.md updated where they mention telemetry
- [ ] All checks in CLAUDE.md pass and the SonarQube Cloud gate passes

## Review focus

That no text from a ticket, prompt, reply, tool or exception reaches an exported log record by any path (state values, scopes, the formatted message, the exception fields, inner exceptions); that `LogSanitizer` runs before the exporter and covers log calls added later, and the content event set holds every log call that takes text; that export off really registers nothing; that the exporter can't feed its own logs back into the export.

# Step 13: OpenTelemetry tracing

- **Branch:** `step/13-tracing`
- **Depends on:** step 12
- **Design:** PLAN.md 6.2 (middleware order), 6.6 (usage and cost), 7 (streaming), 10 (tools); adds a new PLAN.md section, "Telemetry"

## Goal

A request to the API can be followed end to end in a trace: the HTTP request, the feature that handled it, retrieval and its embedding call, each model call with its tokens and outcome, each tool call, and the SQL it ran. Traces carry IDs and counts, never prompt or ticket text. Nothing changes when no collector is configured, so CI and the default dev setup run as before.

## Scope

**Wiring (`SupportDesk.Api` composition root)**
- `OpenTelemetry.Extensions.Hosting` with tracing, the OTLP exporter, and instrumentation for ASP.NET Core, `HttpClient` and Npgsql (`Npgsql.OpenTelemetry`). EF Core's instrumentation is still prerelease and isn't used: Npgsql's spans already show each SQL command. Package versions go in `Directory.Packages.props` (stable releases only)
- The exporter is on only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (the standard variable; `OTEL_SERVICE_NAME` defaults to `supportdesk-api`). Without it, nothing is exported and the app behaves as today
- `GET /api/health` isn't traced, since it's polled
- Logs already carry `TraceId` and `SpanId` in their scopes (the host's default `ActivityTrackingOptions`, and the JSON console format includes scopes), so a log line can be matched to its trace. Exporting logs and metrics over OTLP is out of scope (below)

**Model calls and embeddings**
- `UseOpenTelemetry()` from Microsoft.Extensions.AI 10.11 (Gen AI semantic conventions v1.41) goes into the chat pipeline **directly under `FunctionInvokingChatClient`**, above `RequestCapCounterMiddleware`. That gives one `chat` span per inner model call (the same unit as a `ModelCalls` row: retries stay inside it, and their HTTP attempts show as child spans), and lets the tool loop emit `execute_tool` spans, which `FunctionInvokingChatClient` creates only when a telemetry client sits under it. The order test (`The_middleware_order_matches_the_plan`) and PLAN.md 6.2 change with it
- The embedding generator gets `UseOpenTelemetry()` too, so retrieval and the startup sync show `embeddings` spans
- Tracing subscribes to Microsoft.Extensions.AI's source by its default name, `Experimental.Microsoft.Extensions.AI` (`AddSource`), as well as the app's own; without it the `chat` and `embeddings` spans are created but never exported
- **No message content:** `EnableSensitiveData` is set explicitly from `Telemetry:CaptureMessageContent` (default `false`) on both clients. Setting it in code matters: otherwise the `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` environment variable would turn on capture of prompts, ticket text, tool arguments and tool results. `true` is refused at startup in `Production`, like the fake provider, and logged at warning level when on (it's the "debug setting" CLAUDE.md allows for prompt text)
- Each `chat` span also gets the app's own tags from `AiCallContext`: `supportdesk.feature`, `supportdesk.prompt_version`, `supportdesk.ticket_id`, `supportdesk.reply_run_id` (IDs only: no subject, customer name or other ticket field)

**App spans (`ActivitySource` "SupportDesk")**
- One span per feature operation, parent of its model calls: `reply` (ticket ID, reply run ID, whether the injection guard limited the tools and the signal names that caused it, chunks retrieved and kept, citations and removed markers, outcome), `summary`, `classification` (including a held one, with the strong signal names and no model call under it), `retrieval` (query length, `k`, results kept, minimum similarity; not the query)
- `confirm_follow_up` and `proposal_expiry` (rows changed); `model_calls.save` for each batch the writer saves (row count), as a root span, since the writer runs in the background
- Errors set the span status to `Error` with the exception type and the error code the client sees (`timeout`, `model_unavailable`, …); a caller's cancellation is `Unset` with a `supportdesk.cancelled` tag, not an error. Exception messages aren't recorded, for the same reason they aren't logged (PLAN.md 6.6)
- A streamed reply's spans end when the stream ends, with `done`, `error`, or the caller's disconnect

**Local viewing**
- An optional `telemetry` profile in `docker-compose.yml` runs the .NET Aspire dashboard (pinned image tag), which takes OTLP and shows traces, on `127.0.0.1` only. Run with `docker compose --profile telemetry up -d telemetry` and `OTEL_EXPORTER_OTLP_ENDPOINT=http://telemetry:18889` (dev container) or the Compose variable `OTEL_EXPORTER_OTLP_ENDPOINT` for the `api` service
- The CLAUDE.md commands table and the README's quick start get one line each

**Config**

| Key | Default | Meaning |
| --- | --- | --- |
| `Telemetry:CaptureMessageContent` | `false` | Put prompts, replies and tool arguments in spans; refused in `Production` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (environment) | none | Where to send traces; unset means no export |
| `OTEL_SERVICE_NAME` (environment) | `supportdesk-api` | Service name on every span |

Options with `ValidateOnStart`, documented in PLAN.md's new Telemetry section. No secrets are added.

**Tests** (CI needs no collector: tests use the in-memory exporter, `OpenTelemetry.Exporter.InMemory`)
- Unit: through the pipeline with the fake provider, a reply with one tool round gives two `chat` spans and one `execute_tool` span under one `reply` span; a retried call is still one `chat` span; a refused budget, a refusal and a timeout give `Error` spans with the right code; a caller's cancellation isn't an error
- Unit: no span attribute or event holds ticket text, prompt text, tool arguments or results, checked with a ticket whose text is a unique marker, including with `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` set for the test (a non-parallel collection that restores the variable)
- Unit: `Telemetry:CaptureMessageContent=true` fails startup in `Production`
- Integration: `POST /api/tickets/{id}/reply` for T-1002 gives one trace from the HTTP span down to the SQL spans, and the number of `chat` spans equals the `ModelCalls` rows it wrote; `GET /api/health` gives no span
- Each test is shown to fail when its behaviour is broken (say how in the PR)

## Out of scope

- Metrics: [step 14](14-metrics.md). Log export over OTLP. The `ModelCalls` table stays the source for cost
- Tracing in the browser, and a trace ID in the reply stream's events (problem details already carry `traceId`)
- The Anthropic SDK's own OpenTelemetry spans: model calls go through the shared pipeline, so its spans would duplicate the `chat` span
- Sampling settings (the SDK default, parent-based always-on, is fine for one developer); a hosted backend

## Acceptance criteria

- [ ] With `OTEL_EXPORTER_OTLP_ENDPOINT` unset, the app and every test behave as before, and no exporter runs
- [ ] With the `telemetry` profile up, a reply on T-1002 and T-1008 shows in the dashboard as one trace: HTTP request → `reply` → `retrieval` → `embeddings`, `chat` spans with token usage, `execute_tool` spans, and SQL spans (screenshot in the PR)
- [ ] `chat` spans match `ModelCalls` rows one to one; retries don't add `chat` spans
- [ ] No span holds prompt, ticket, reply or tool-argument text unless `Telemetry:CaptureMessageContent` is `true`, whatever `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` says; `true` is refused in `Production`
- [ ] Errors and cancellations are reported as described, without exception messages
- [ ] The middleware order in PLAN.md 6.2, its test, and the new PLAN.md Telemetry section match the code
- [ ] CLAUDE.md commands, `appsettings.json`, README and `.env.example` (the Compose variable, empty) are updated
- [ ] All checks in CLAUDE.md pass and the SonarQube Cloud gate passes

## Review focus

That no text from a ticket, prompt, reply or tool reaches a span by any path (including the environment variable and `ChatOptions.AdditionalProperties`); the pipeline position of the telemetry client and what it does to the one-row-per-call rule; that tracing off really costs nothing.

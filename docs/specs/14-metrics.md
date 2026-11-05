# Step 14: OpenTelemetry metrics

- **Branch:** `step/14-metrics`
- **Depends on:** step 13 (the OpenTelemetry wiring, the OTLP exporter and the Aspire dashboard)
- **Design:** PLAN.md 6.6 (usage and cost), 9 (classification guard), 10 (tools and proposals), and the Telemetry section step 13 adds

## Goal

The running app reports counters for what matters when it's used: model calls, tokens and cost by feature and model, errors by code, injection-guard hits, tool calls, and proposals, so a dashboard shows usage and failures over time without querying the `ModelCalls` table. The counters agree with that table, because they're recorded from the same values.

## Scope

**Wiring**
- Metrics are added to step 13's OpenTelemetry setup: the app's meter `SupportDesk`, Microsoft.Extensions.AI's meter `Experimental.Microsoft.Extensions.AI` (its default name; its `gen_ai.client.token.usage` and `gen_ai.client.operation.duration` histograms come from the `UseOpenTelemetry()` clients step 13 adds), and ASP.NET Core's built-in meters (request duration and active requests, from `AddAspNetCoreInstrumentation()` on the meter provider)
- Exported over OTLP with the traces, only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The Aspire dashboard from step 13 shows them, so nothing is added to Compose
- The meter comes from `IMeterFactory` through DI (`SupportDeskMetrics`, a singleton that creates the instruments once), not a static `Meter`, so tests get their own (CLAUDE.md: no static mutable state)

**Counters** (`Counter<T>`, monotonic; names follow OpenTelemetry's naming rules)

| Instrument | Unit | Tags | Recorded where |
| --- | --- | --- | --- |
| `supportdesk.model_calls` | `{call}` | `feature`, `provider`, `model`, `outcome` (`success`, `error`, `cancelled`), `error.type` (on errors: the reply stream's code; `invalid_output` is raised by the services after a call, so a model call never has it) | `UsageLoggingMiddleware`, when it builds the row, before the row goes to the sink: one per `ModelCalls` row |
| `supportdesk.model_calls.tokens` | `{token}` | `feature`, `model`, `direction` (`input`, `output`), `estimated` | the same place, from the row's token counts |
| `supportdesk.model_calls.cost` | `USD` (`double`) | `feature`, `model` | the same place, only for rows with a price (`CostUsd` not null) |
| `supportdesk.model_calls.dropped` | `{row}` | `reason` (`buffer_full`, `shutdown`, `save_failed`, `drain_deadline`) | `ModelCallBuffer` (`buffer_full`, and `shutdown` once it's closed) and `ModelCallWriter` (`save_failed`, `shutdown` for a save cut short, `drain_deadline`), where they log the drop today, by the number of rows |
| `supportdesk.replies` | `{reply}` | `outcome` (`done`, the error code, `cancelled`), `guarded` | `ReplyService`, when the stream ends |
| `supportdesk.tool_calls` | `{call}` | `tool`, `result` (`ran`, `invalid_arguments`, `over_limit`) | `ReplyFunction`, the base of both tools, once the call's result is known (a tool that throws isn't counted) |
| `supportdesk.injection_guard.hits` | `{ticket}` | `scope` (`reply`, `classification`), `signal` (one count per signal name found; for `classification`, only the strong signals that hold the ticket) | `ReplyService`, where it finds the signals and removes the proposal tool; `ClassificationService`, once the held result is saved |
| `supportdesk.classifications` | `{classification}` | `status` (`done`, `needs_manual`, `held`), `reason` for `needs_manual` (`invalid_output`, `output_limit`) | `ClassificationService`, after the result is saved. A model error is thrown and saves nothing, so it isn't counted here (the failed call is in `model_calls`) |
| `supportdesk.follow_ups` | `{proposal}` | `event` (`proposed`, `updated`, `confirmed`, `expired`) | the proposal tool (`proposed` the run's first saved proposal, `updated` each later call in the run; a proposal already confirmed or expired counts neither); `FollowUpRepository.ConfirmAsync` after the transaction commits (`confirmed` only for the confirm that created the task, a repeat returns the existing task and counts nothing; `expired` when a confirm finds a pending proposal past its expiry); `ProposalExpiryService` (`expired`, by the number of rows it marked) |

- **Low cardinality only:** no ticket ID, reply run ID, ticket number or any text as a tag. `model` comes from configuration (one value per setup), and `signal` and `error.type` are fixed sets. A test checks every tag value against its allowed set
- **One source of truth:** tokens and cost are recorded from the values written to the `ModelCalls` row (estimated where the row is estimated), so the sums over a period equal the table's sums for that period. A row that's dropped is still counted in `model_calls` (it happened) and also in `dropped`
- Recording can't change a call's outcome: counters are added after the outcome is known (where the row or the log line is made today), outside the code that decides retries, errors or the stream's last event

**Config**
- No new settings: the export interval stays the SDK default (60 s, `OTEL_METRIC_EXPORT_INTERVAL`), documented in PLAN.md for anyone who wants it faster locally

**Tests** (`Microsoft.Extensions.Diagnostics.Testing`'s `MetricCollector<T>`; CI needs no collector)
- Unit: a reply with one tool round adds 2 to `model_calls` (feature `Reply`, outcome `success`), the row's tokens to `tokens`, 1 to `tool_calls` (`ran`) and 1 to `replies` (`done`); a retried call adds 1, not 2; a refusal and a timeout add `error` calls with the right `error.type`; a call the budget refuses writes no row, so it adds no `model_calls` (the reply's `replies` counts `budget_exceeded`); a caller's cancellation is `cancelled`, not `error`
- Unit: an unpriced model adds tokens but no cost; an estimated row records `estimated=true`
- Unit: a ticket the classification guard holds adds 1 to `classifications` (`held`) and one `injection_guard.hits` per strong signal, and no `model_calls`
- Unit: a full buffer and a drain past its deadline add to `dropped` with the right reason and count
- Unit: every tag value recorded across the suite's scenarios is in its allowed set (no IDs or text)
- Integration: after replies on T-1002 and T-1008, the summed `tokens` and `cost` equal the sums of the `ModelCalls` rows the requests wrote, and `follow_ups` counts the proposal and its confirmation
- Each test is shown to fail when its behaviour is broken (say how in the PR)

## Out of scope

- Histograms of our own (Microsoft.Extensions.AI and ASP.NET Core already give durations), gauges, and runtime metrics
- OTLP log export
- Alerts, a hosted metrics backend, and dashboards beyond the Aspire one
- Replacing the `ModelCalls` table or the cost readout: the table stays the source for per-ticket cost and the README's numbers

## Acceptance criteria

- [ ] With `OTEL_EXPORTER_OTLP_ENDPOINT` unset, nothing is exported and every test behaves as before
- [ ] With step 13's `telemetry` profile up, the Aspire dashboard shows each counter above after a few replies, a summary and a classification (screenshot in the PR)
- [ ] `model_calls` counts one per `ModelCalls` row, and `tokens` and `cost` sum to the table's values for the same requests
- [ ] No tag holds an ID, a ticket number or text; every tag value is from its fixed set
- [ ] Errors are counted by their client-facing code, cancellations separately, and dropped rows by reason
- [ ] PLAN.md's Telemetry section lists the instruments, their tags and where each is recorded; README and CLAUDE.md updated where they mention telemetry
- [ ] All checks in CLAUDE.md pass and the SonarQube Cloud gate passes

## Review focus

Tag cardinality and that no ticket data becomes a tag; that the counters and the `ModelCalls` table can't drift apart (same values, same place, including retries, estimates and dropped rows); that recording a metric can't change a call's outcome.

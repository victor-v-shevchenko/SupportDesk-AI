# Fix: request limits, follow-up telemetry and license

- **Branch:** `fix/request-limits`
- **Depends on:** steps 3b (`ModelCalls` rows and their cost), 8 (follow-ups), 13 and 14 (the `confirm_follow_up` span and the `supportdesk.follow_ups` metric)
- **Design:** PLAN.md 5 (API), 6.4 (token budget), 9 (model errors as problem details), 10 (tools), 14 (telemetry)
- **Why a fix, not a step:** three findings from a review of the finished project. It changes behaviour and adds settings, so it has a spec like a step

## Goal

1. **Nothing stops a script from spending the provider key.** The token budget caps one call and one request, but a loop calling `POST /api/tickets/{id}/reply` with a hosted model spends without end. The README says the app runs LLM features "the way they'd run in production", so it needs a request rate limit on the AI endpoints and a daily spending limit
2. **The data layer records telemetry.** `FollowUpRepository.ConfirmAsync` starts the `confirm_follow_up` span and records `supportdesk.follow_ups` itself. That belongs in a service above the repository
3. **There's no license.** Add MIT

## Scope

**Rate limit (`AddRateLimiter`)**
- One fixed-window policy, `ai`, on the four endpoints that call a model or an embedding API: `POST /api/tickets/{id}/reply`, `POST /api/tickets/{id}/summary`, `POST /api/tickets/{id}/classification` and `GET /api/search`. No other endpoint is limited
- **One window for the whole app, not per client.** There is no sign-in, so the only client key would be an IP address. In Docker Compose every request reaches the API from the web container's address, and trusting `X-Forwarded-For` without a known proxy lets a client choose its own key. The limit's job is to cap how fast the key is spent, which an app-wide window does. The cost: anyone who can reach the API can use up the window for everyone until it resets. Per-user limits come with authentication (README, Possible improvements)
- Settings `RateLimiting:Ai:PermitLimit` (default `20`, 1–100000) and `RateLimiting:Ai:WindowSeconds` (default `60`, 1–3600), through the options pattern with `ValidateOnStart`. No queue: a request over the limit is refused at once
- A refused request is `429` problem details with `code` `rate_limited`, the title "Too many requests" and the detail "Too many AI requests in a short time. Try again in a moment.", and a `Retry-After` header in whole seconds when the limiter gives one (the whole window: an upper bound). The handler isn't reached, so the ticket isn't looked up and nothing is logged as a model call

**Daily spending limit**
- Setting `AI:Budget:DailySpendLimitUsd` (default `5`, 0.01–1000000), validated with the other budget settings
- Before the reply, summary and classification endpoints run (an endpoint filter, after the rate limit), the API adds up today's `ModelCalls.CostUsd`, the UTC day from midnight (`DailySpendLimit` in Core over `IModelCallRepository.SumCostSinceAsync`). At or over the limit the request is refused with `429` problem details, `code` `daily_limit`, the title "Daily spending limit reached", the detail "The model's spending limit for today has been reached. It resets at midnight UTC.", and `Retry-After` set to the seconds until the next UTC midnight. A warning is logged with the amount spent and the limit (numbers only)
- Search isn't checked: its embedding calls write no `ModelCalls` row, so it has no cost to count. The rate limit still covers it
- **A soft limit.** The check is made when a request starts. A request already running finishes, and rows reach the table a moment after the call (the background writer, PLAN.md 6.6). So the overshoot is the requests that start before the crossing call's row is saved, each held to `AI:Budget:MaxTokensPerRequest`, at the model's price. Rows the writer drops are never counted. Calls with no price (`CostUsd` null, a model missing from `AI:Pricing`) don't count, so a hosted model needs its price in `AI:Pricing` for the limit to work, as it already does for the cost readout
- The check costs one indexed query (`ModelCalls.Timestamp` is indexed) per AI request. No cache, so there's no shared mutable state, and the rate limit bounds how often it runs

**UI**
- The reply panel shows a refused reply's problem `detail` (both 429s) instead of "Couldn't start the reply (status 429)". `ReplyRequestError` carries the detail when the response has one. A `404` keeps its own text
- Summary and classification already show a problem's `detail`. Search didn't: it blamed the embedding model for any failure but a `400`, so a `429` now shows the problem's text (found in review). `errorMessages` gains `rate_limited` and `daily_limit` texts for a response without one

**Follow-up telemetry out of the repository**
- New `FollowUpService` (Core, `IFollowUpService.ConfirmAsync`) starts the `confirm_follow_up` span, sets its tags, records `supportdesk.follow_ups` after the commit, and calls the repository. `ConfirmProposal` calls the service
- `FollowUpRepository` no longer references `SupportDeskMetrics` or `SupportDeskTelemetry`. `ConfirmResult` gains `RowsChanged`, the rows the confirm saved, so the service can set `supportdesk.rows_changed` and count only what this call changed: `confirmed` when it created the task, `expired` when it marked the proposal expired. The span, its tags and the metric keep their names and meaning; only the code that writes them moves. The database spans stay children of `confirm_follow_up`, since the service's span is around the whole repository call

**License**
- `LICENSE` at the root: MIT, "Copyright (c) 2026 Viktor Shevchenko". The README says so

**Docs and environments**
- PLAN.md: the settings, the two 429s in the problem table (9), the limits in 5 and 6.4, the span's source in 14
- The integration and evaluation hosts raise both limits, since they run many AI requests in a few seconds. Tests of the limits set low ones
- The end-to-end API environment (`playwright.config.ts`, and the CLAUDE.md commands line for reusing a server) raises both limits: the tests price the fake model at $1000 per million input tokens, so a few replies pass $5
- The README: the limits in the design notes, and the authentication bullet in Possible improvements says per-user limits

## Out of scope

- Per-user or per-IP limits, and authentication
- Trusting forwarded headers
- Limiting the embedding cost of search by money (no rows to count)
- Refusing calls inside a request that's already running (the per-request cap bounds them)
- A metric for refused requests

## Tests

- Unit: `DailySpendLimit` asks for the cost since the start of the current UTC day, is reached at and above the limit and not below it, and gives the next UTC midnight as the reset
- Unit: `FollowUpService` records `confirmed` once for a new task and not for a repeat confirm, `expired` only when the confirm changed the status, sets the span's outcome and rows changed, and marks the span failed when the repository throws
- Unit: the new settings out of range fail validation
- Integration: with `PermitLimit` 2, the third AI request in the window is a `429` with `code` `rate_limited` and a `Retry-After` header, and `GET /api/tickets` isn't limited
- Integration: with today's `ModelCalls` cost at the limit, reply, summary and classification are `429` with `code` `daily_limit` and `Retry-After`, and no new `ModelCalls` row is written; rows from before today's UTC midnight don't count, and search isn't refused
- The existing tracing and metrics integration tests for the confirm still pass unchanged
- Angular: a `429` with problem details shows its detail in the reply panel, and a rate-limited search says so instead of blaming the embedding model
- Unit: the rate-limit settings are validated through the app's registration, and the spend filter refuses with `Retry-After` and logs only the amounts
- Each new test fails when its behaviour is broken (say how in the PR)

## Acceptance criteria

- [ ] A loop of AI requests is refused with `429` `rate_limited` once it passes `RateLimiting:Ai:PermitLimit` in the window
- [ ] Once today's cost reaches `AI:Budget:DailySpendLimitUsd`, reply, summary and classification are refused with `429` `daily_limit` until midnight UTC
- [ ] The UI shows both refusals in words, not as a status code
- [ ] `FollowUpRepository` has no telemetry code, and the `confirm_follow_up` span and `supportdesk.follow_ups` metric behave as before
- [ ] `LICENSE` is MIT
- [ ] New settings are in `appsettings.json` and PLAN.md; CLAUDE.md's e2e environment is updated

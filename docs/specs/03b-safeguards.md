# Step 3b: Safeguards

- **Branch:** `step/03b-safeguards`
- **Depends on:** step 3a
- **Design:** PLAN.md sections 6.2, 6.4, 6.5, 6.6

## Goal

Retries, timeouts, the token budget and cost logging are in the pipeline before any feature streams a response.

## Scope

**`RetryTimeoutMiddleware`**
- Retries per PLAN.md 6.5: transient errors only, exponential backoff with jitter, `Retry-After` honored, at most `AI:Retry:MaxAttempts`
- Streaming: retries only until the first item (text or tool call) has been yielded to the caller; after that, an error is rethrown as `ModelUnavailableException`
- Timeouts: non-streaming, first item, gap between items, total stream. A timeout counts as transient before the first item
- Cancellation from the caller is never retried and never mapped to a timeout

**`BudgetGuardMiddleware`**
- Estimates input tokens (characters ÷ 4 × 1.2 over all messages and tool definitions); over `MaxInputTokensPerCall` throws `BudgetExceededException`
- Sets `MaxOutputTokens` from the feature's setting when the caller didn't

**`RequestCapMiddleware`**
- Outermost; adds input and output tokens of each inner call (from usage, or the estimate when usage is missing) and stops the request once `MaxTokensPerRequest` is passed

**`UsageLoggingMiddleware` and `ModelCalls`**
- `ModelCall` entity and migration `AddModelCalls`
- One row per inner call with the fields in PLAN.md 4; `IsEstimated = true` when the provider sent no usage
- Cost from `AI:Pricing`; unknown model gives `null` cost and a warning
- Writes don't block the response stream: rows go through a bounded channel to a background writer; if the channel is full, the row is dropped with a warning
- `GET /api/model-calls?ticketId=` returns the rows, newest first

**Token estimate check**
- A test in `SupportDesk.Eval` (opt-in) calls Anthropic's token-counting endpoint for the three prompts with sample tickets and checks that the estimate is not lower than the real count

**Quality gate becomes required**
- Look at the SonarQube Cloud results for steps 1–3b. For rules that are noise for this project or conflict with the .NET analyzers, add `sonar.issue.ignore.multicriteria` entries to `sonar/SonarQube.Analysis.xml` and list each one with its reason in PLAN.md 11
- Fix every open issue on new code. For any you believe is a false positive, explain it in the pull request; the user marks it in SonarQube Cloud

## Done by the user

1. Mark the false positives listed in the pull request in SonarQube Cloud
2. Review the rule exclusions in the pull request diff
3. Add the SonarQube Cloud check to the required checks for `main` (or approve Claude Code doing it with `gh api`)

## Out of scope

Features that use the pipeline. Fault cases beyond what's listed (step 9 adds more).

## Acceptance criteria

- [ ] Middleware order matches PLAN.md 6.2 (checked by a test that builds the pipeline and inspects it)
- [ ] A 429 or 503 before the first item is retried up to the limit, then surfaces as `ModelUnavailableException`
- [ ] A 400 is not retried
- [ ] An error after the first streamed item is not retried; the caller sees the items already sent, then `ModelUnavailableException`
- [ ] A first item later than `FirstItemSeconds`, or a gap longer than `GapSeconds`, ends the call as a timeout
- [ ] Caller cancellation stops the call at once, with no retry, and the row is logged with `Outcome = Cancelled`
- [ ] An oversized call throws `BudgetExceededException` before reaching the provider
- [ ] The request cap stops a tool loop whose total passes the cap
- [ ] Every inner call writes exactly one `ModelCalls` row, with the right feature, prompt version and ticket
- [ ] A stream cut off before usage writes an estimated row
- [ ] Cost matches the price table; an unknown model logs a warning and stores `null`
- [ ] Prompt and ticket text aren't in the logs at the default log level
- [ ] The SonarQube Cloud quality gate passes on this branch
- [ ] Every rule exclusion is in `sonar/SonarQube.Analysis.xml` (none in the SonarQube Cloud UI) and listed with its reason in PLAN.md 11
- [ ] The gate is a required check for merging into `main` (confirmed by the user)

## Tests

- Unit, all with the fake provider and a fake clock: each retry rule; each timeout; cancellation; budget guard; request cap across a tool loop; cost calculation; estimated usage
- Integration: rows written to `ModelCalls`; `GET /api/model-calls`

## Review focus

The retry boundary at the first item, timer handling (no leaked timers or unobserved tasks), that logging can't slow or break a stream, and that cancellation is never retried.

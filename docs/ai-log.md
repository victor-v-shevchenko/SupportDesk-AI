# AI log

Bugs found in AI-written code, how they were found, and the test that now covers each one. Newest first.

Entry format (from CLAUDE.md):

```
## <date> — <short title>
- Step: <N>
- Found by: review subagent | test | browser check | user
- What was wrong: <one or two sentences>
- Fix: <one sentence, with the commit or PR>
- Test: <test name that now covers it>
```

## 2026-10-31 — Waiting for the startup lock timed out after 30 seconds
- Step: 2
- Found by: review subagent
- What was wrong: the `pg_advisory_lock` call used Npgsql's default 30-second command timeout, so an instance waiting behind a slow first migration would still fail to start.
- Fix: the lock call has no command timeout; cancellation still comes from `ApplicationStopping` (step 2 PR).
- Test: none; a test would have to hold the lock for over 30 seconds in CI. `SeedingTests.Two_instances_starting_at_once_seed_once_without_failing` covers the lock itself

## 2026-10-31 — Malformed ticket body returned 500 in Development
- Step: 2
- Found by: review subagent
- What was wrong: Minimal APIs throw `BadHttpRequestException` for an unreadable body in Development, and the exception handler turned it into a `500` instead of a `400`.
- Fix: `ExceptionHandlerOptions.StatusCodeSelector` keeps that exception's status code (step 2 PR).
- Test: `TicketEndpointTests.Create_returns_400_problem_details_for_a_body_that_is_not_a_ticket`

## 2026-10-31 — Two instances starting together crashed on migration
- Step: 2 (the migration race dates from step 1)
- Found by: review subagent
- What was wrong: two API instances starting against one empty database both ran the migrations, and one failed with `relation "ticket_numbers" already exists`; seeding had the same race on the unique ticket number.
- Fix: migrations and seeding run under one PostgreSQL advisory lock in `MigrateAndSeedSupportDeskDatabaseAsync` (step 2 PR).
- Test: `SeedingTests.Two_instances_starting_at_once_seed_once_without_failing`

## 2026-10-31 — Created ticket's timestamp differed from the stored one
- Step: 2
- Found by: test
- What was wrong: `POST /api/tickets` returned `createdAt` with .NET's 100 ns precision, but PostgreSQL keeps microseconds, so a later `GET` of the same ticket returned a slightly different time.
- Fix: `TicketRepository` truncates `CreatedAt` to microseconds before saving (step 2 PR).
- Test: `TicketEndpointTests.Create_returns_201_with_location_and_the_ticket_is_listed_first`

## 2026-11-01 — A slow caller could time out a stream after its first item
- Step: 3b
- Found by: review subagent
- What was wrong: `RetryTimeoutMiddleware` stopped the first-item timer only after handing the first item to the caller, so a caller that took longer than `FirstItemSeconds` with it (a blocked response write) got a non-transient "timed out waiting for the next update" although the provider was fine. The scripted test provider ignored cancellation between steps, which hid it.
- Fix: the timer is stopped, and gap timing starts, before anything goes to the caller; the scripted provider now ends a cancelled call between steps, as an HTTP stream does (step 3b PR).
- Test: `RetryTimeoutMiddlewareTests.The_callers_time_with_the_first_item_does_not_count_against_the_first_item_timeout`

## 2026-11-01 — The token estimate was lower than the real count
- Step: 3b
- Found by: test (the opt-in `SupportDesk.Eval` token-count check, first run against Anthropic)
- What was wrong: the budget estimate (characters ÷ 4 × 1.2) came out 7–25% below Anthropic's count on 5 of the 6 prompts, because these prompts tokenize at about 2.6–3.1 characters per token and each message has a fixed cost, so the per-call guard could let an over-limit call through.
- Fix: the estimate is now characters ÷ 3 × 1.2 plus 10 tokens per message, computed in integers; all six cases are 11–25% above the real count (step 3b PR).
- Test: `TokenEstimateTests.The_estimate_is_not_lower_than_Anthropics_count` (opt-in), with `BudgetAndUsageTests.The_estimate_is_characters_over_3_times_1_2_rounded_up` and `Each_message_adds_10_tokens_for_its_framing` in CI

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

## 2026-11-01 — A stray cancellation could stop the API through the proposal expiry check
- Step: 8
- Found by: review subagent
- What was wrong: `ProposalExpiryService` let every `OperationCanceledException` out of a run, so a cancellation that wasn't shutdown (a driver or connection-pool timeout) faulted the background service, and the host's default behavior stops the app.
- Fix: a run's failure is caught unless the service's own token is cancelled, so it's logged and the next run tries again (step 8 PR).
- Test: `ProposalExpiryServiceTests.A_cancellation_that_is_not_shutdown_is_a_failed_run_not_a_crash`

## 2026-11-01 — A failed orders.json load broke every order lookup until restart
- Step: 8
- Found by: review subagent
- What was wrong: `SeedOrderStatusService` cached its load in a `Lazy<Task>`, which keeps a faulted task, so one failed read on the first lookup made every later lookup fail until the app restarted.
- Fix: a faulted load is replaced on the next lookup (step 8 PR).
- Test: `SeedOrderStatusServiceTests.A_failed_load_is_tried_again_on_the_next_lookup`

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

## 2026-11-01 — Startup-failure tests were flaky in CI
- Step: 3b (the tests date from step 3a)
- Found by: test (CI: one of two runs of the same commit failed)
- What was wrong: `AiStartupTests` expected the `OptionsValidationException` from `CreateClient()`, but inside `WebApplicationFactory` the failing app disposes its service provider while the factory still reads from it, so sometimes an `ObjectDisposedException` came out instead; the extra startup work in step 3b made that more likely.
- Fix: the tests check the host's "Hosting failed to start" log, written before anything is disposed, and that the app didn't start (step 3b PR).
- Test: `AiStartupTests.A_missing_api_key_stops_the_app_from_starting`, `AiStartupTests.The_fake_provider_stops_the_app_from_starting_in_Production`

## 2026-11-01 — Search results select showed 3 while searches sent 5
- Step: 4
- Found by: browser check
- What was wrong: the articles page bound `[value]` on the `<select>`, which Angular sets before the `@for` adds the options, so the select fell back to its first option (3) while the default `k` sent to `/api/search` was 5.
- Fix: each option binds `[selected]` to the current `k` instead (step 4 PR).
- Test: `ArticlesPage shows the default of 5 results in the select, matching what a search sends`

## 2026-11-01 — Classification retry message repeated what the model wrote
- Step: 7
- Found by: review subagent
- What was wrong: validation errors quoted unknown property names and wrong enum values (up to 40 characters) from the model's answer, and they go back in the retry user message, so text the model copied from the ticket could reach a user turn outside `<ticket>`.
- Fix: the errors name the allowed values and properties only, never what the model wrote (step 7 PR).
- Test: `ClassificationValidatorTests.Errors_never_repeat_what_the_model_wrote_so_ticket_text_cannot_reach_the_retry_message`

## 2026-11-01 — Summary cut fell back mid-word on newlines
- Step: 6
- Found by: review subagent
- What was wrong: when a summary over 600 characters had no sentence end that fit, `SummaryOutput` looked only for a space to cut at, so text broken by newlines was cut mid-word, and a cut could end in `foo,…`.
- Fix: the fallback cut looks for any whitespace and drops trailing commas, semicolons and colons before the ellipsis (step 6 PR).
- Test: `SummaryOutputTests.A_cut_without_a_sentence_end_breaks_at_newlines_too_and_drops_trailing_commas`

## 2026-11-01 — Embedding failures during a reply showed as internal errors
- Step: 5
- Found by: review subagent
- What was wrong: the embedding generator isn't behind the chat pipeline's error mapping, so a retrieval failure (Ollama down, an HTTP error, an `HttpClient` timeout) reached the reply as a raw exception and became `internal_error`, logged with the provider's exception, instead of `model_unavailable` or `timeout`.
- Fix: `RetrievalService` maps embedding failures to `ModelUnavailableException` with fixed text, keeping the status and marking unrequested cancellations as timeouts (step 5 PR).
- Test: `RetrievalServiceTests.An_embedding_provider_failure_becomes_ModelUnavailableException`

## 2026-11-01 — Ticket text could fake the sources block
- Step: 9
- Found by: test
- What was wrong: `TicketPromptFormatter` escaped `ticket` and `tool_result` tags but not `sources`, so a customer could write `</sources>` or a whole `<sources>[1] …</sources>` block into a ticket, inside `<ticket>`, and give the model a made-up article to answer from and cite. Article text already had `sources` escaped.
- Fix: the ticket escape covers `sources` tags too, in any case or spacing (step 9 PR).
- Test: `PromptInjectionTests.Ticket_text_stays_inside_the_ticket_block_of_the_user_message` and `TicketPromptFormatterTests.Ticket_text_cannot_fake_an_article_block`

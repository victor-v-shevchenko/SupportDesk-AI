# Step 3a: Pipeline and fake provider

- **Branch:** `step/03a-pipeline`
- **Depends on:** step 2
- **Design:** PLAN.md sections 6.1, 6.2, 6.3, 6.7, 6.8

## Goal

One shared `IChatClient` pipeline that every feature uses, with provider selection from config, versioned prompts, typed errors, and a scripted fake provider that tests and the UI can rely on. Safeguards come in step 3b; this step leaves slots for them in the pipeline.

## Scope

**Provider registration** (in `SupportDesk.Api`)
- `AI:Chat:Provider` selects Anthropic (official `Anthropic` package and its `IChatClient` adapter), OpenAI (`Microsoft.Extensions.AI.OpenAI`), Ollama (`OllamaSharp`) or Fake
- `AI:Embeddings:Provider` selects Ollama, OpenAI, Voyage (hand-written `IEmbeddingGenerator` over HTTP) or Fake
- Provider SDK retries off (Anthropic: max retries 0); `HttpClient` connect timeout from `AI:Timeouts:ConnectSeconds`
- Missing API key for the selected provider fails at startup with a clear message naming the setting. Keys are `AI:Anthropic:ApiKey`, `AI:OpenAI:ApiKey` and `AI:Voyage:ApiKey` (user secrets, or `AI__…__ApiKey` from docker compose), not `ANTHROPIC_API_KEY`, which the dev container keeps out of its environment for Claude Code's sake (PLAN.md 6.1)
- The fake provider fails at startup in `Production`

**Pipeline** (in `SupportDesk.Core/AI`)
- `ChatClientBuilder` with `FunctionInvokingChatClient` (max 3 iterations) and an `ErrorMappingMiddleware` that turns provider exceptions into `ModelUnavailableException` or `ModelRefusedException` (Anthropic `stop_reason = "refusal"`). It sits innermost, under the step 3b middleware, and recognizes SDK exceptions through one `IModelErrorClassifier` per provider registered in the API, so Core never references an SDK (PLAN.md 6.2)
- `AiCallContext` helpers that put and read feature, prompt version, ticket ID, ticket number and reply run ID in `ChatOptions.AdditionalProperties`
- Anthropic options per PLAN.md 6.7: no temperature or sampling options; effort set per feature through the adapter's provider-specific options. If the adapter can't pass effort, write that down in PLAN.md and continue without it

**Prompts**
- `IPromptStore` loads `prompts/<name>.v<N>.md` by the version in `AI:Prompts:<name>`, parses the system and user sections, and fills `{{placeholders}}`
- A `TicketPromptFormatter` wraps ticket text in `<ticket>…</ticket>` and escapes `</ticket>` inside it
- `prompts/reply.v1.md`, `summary.v1.md`, `classify.v1.md` as first drafts (refined in their steps)

**Fake provider**
- `FakeChatClient` and `FakeEmbeddingGenerator` per PLAN.md 6.8
- Besides the step types in PLAN.md 6.8, a `finish` step sets the finish reason, which is how the `refusal` scenario is scripted
- `tests/fixtures/fake-scenarios.json` with a default scenario (a short streamed reply with a citation marker) and these named scenarios, keyed by ticket number: `tool-call-order`, `tool-call-proposal`, `error-before-first-item`, `error-after-first-item`, `stall`, `stall-after-first-item` (added during review, for step 3b's gap timeout), `invalid-json`, `refusal`
- Scenarios can be loaded from a file path in config, so tests can supply their own

## Out of scope

Retries, timeouts, budget, usage logging (step 3b). Any feature using the pipeline (steps 5–8).

## Acceptance criteria

- [x] Each provider value in config resolves to the matching client; an unknown value fails at startup
- [x] A missing API key for the selected provider fails at startup with the setting's name; it is never logged
- [x] `AI__Chat__Provider=Fake` in `Production` fails at startup
- [x] Only `SupportDesk.Api` references provider packages (checked by a test that inspects project references)
- [x] Provider exceptions come out of the pipeline only as the typed exceptions from PLAN.md 6.5
- [x] `IPromptStore` loads the configured version; a missing file fails at startup, not on first use
- [x] `</ticket>` inside ticket text can't close the tag
- [x] The fake provider plays each named scenario: streamed text with delays, tool calls, errors before and after the first item, stalls before and after the first item, invalid JSON, refusal
- [x] `FakeEmbeddingGenerator` returns the same 1024-dimension vector for the same text

## Tests

- Unit: provider selection; startup failures; prompt loading and placeholder filling; ticket escaping; error mapping; each fake scenario
- Architecture test: no provider package referenced outside `SupportDesk.Api`

## Review focus

That nothing bypasses the pipeline, the ticket escaping, secrets handling at startup, and that the fake scenarios are realistic enough to test streaming edge cases.

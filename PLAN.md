# SupportDesk AI: Design

This is the design that every step spec builds on. [SupportDesk-AI.md](SupportDesk-AI.md) is the project brief: it explains what we're building and why. This file is the source of truth for how. When a spec and this file disagree, fix one of them in the same pull request.

## Project links

| What | Value |
| --- | --- |
| GitHub repository | [victor-v-shevchenko/SupportDesk-AI](https://github.com/victor-v-shevchenko/SupportDesk-AI) (`https://github.com/victor-v-shevchenko/SupportDesk-AI.git`) |
| SonarQube Cloud organization key | `victor-v-shevchenko` |
| SonarQube Cloud project key | `victor-v-shevchenko_SupportDesk-AI` |
| SonarQube Cloud project | [dashboard](https://sonarcloud.io/project/overview?id=victor-v-shevchenko_SupportDesk-AI) |
| CI token | `SONAR_TOKEN`, a SonarQube Cloud user token named `supportdesk-ci-github`, stored only as a GitHub Actions repository secret. Expires 2026-11-30: create a new one and update the secret before then |

The keys aren't secret and can be committed. Tokens never are.

## 1. Goals and non-goals

**Goals:** a support-ticket assistant with production-style LLM features: RAG with citations, structured output, tool calling, streaming, plus the safeguards around them (timeouts, retries, token budget, cost logging, prompt-injection defenses), and an evaluation suite.

**Non-goals:** authentication and roles, multi-tenancy, real email or chat intake, fine-tuning or hosting models.

## 2. Architecture

```
Angular (web/)  ──HTTP/JSON, SSE──►  SupportDesk.Api (Minimal API)
                                          │
                     ┌────────────────────┼─────────────────────┐
                     ▼                    ▼                     ▼
              SupportDesk.Core      SupportDesk.Data       Providers
              (services, AI         (EF Core, pgvector,    (Anthropic, OpenAI,
               pipeline, prompts)    migrations, seed)      Ollama, Voyage, Fake)
                                          │
                                          ▼
                                PostgreSQL + pgvector
```

- The API is thin: endpoints validate input, call a Core service and map results to HTTP
- Core holds the domain, the services (reply, summary, classification, retrieval) and the LLM pipeline. It depends on abstractions (`IChatClient`, `IEmbeddingGenerator`, repositories), never on a provider SDK
- Data holds EF Core, migrations, pgvector mapping and seed data
- Providers are registered in the API's composition root from config; only that code references provider SDKs

## 3. Repository layout

```
SupportDesk.slnx
Directory.Build.props          nullable, warnings as errors, LangVersion
Directory.Packages.props       central package versions
global.json                    pinned .NET SDK (CI installs exactly this one)
docker-compose.yml             db, api, web
.env.example                   every secret with empty values; copied to .env (gitignored)
.gitattributes                 LF line endings, so scripts run in the Linux container
.devcontainer/                 devcontainer.json, compose override (reads the root .env), post-create.sh
.vscode/ or .sonarlint/        SonarQube for IDE connected-mode binding (no token)
sonar/SonarQube.Analysis.xml   Sonar rule exclusions, passed to the scanner
.github/workflows/ci.yml
.claude/agents/reviewer.md
.mcp.json                      Playwright MCP (headless)
src/
  SupportDesk.Api/             endpoints, composition root, provider registration
  SupportDesk.Core/            domain, services, AI pipeline (AI/), fake provider (AI/Fake/)
  SupportDesk.Data/            DbContext, entities config, migrations, seed
prompts/                       versioned prompt files (reply.v1.md, ...)
seed/                          tickets.json, articles/*.md, orders.json
tools/EmbedFixtures/           console app that regenerates embedding fixtures
tests/
  SupportDesk.UnitTests/       fast, no database, fake provider
  SupportDesk.IntegrationTests/ Testcontainers PostgreSQL + pgvector
  SupportDesk.Eval/            Category=Eval, real model, opt-in
  fixtures/embeddings/         committed vectors for articles and test questions
  fixtures/fake-scenarios.json scripted responses for the fake provider
  coverage.runsettings         coverlet output in OpenCover format, for Sonar
web/                           Angular app; e2e/ holds Playwright tests
docs/
  specs/                       one spec per step
  ai-log.md                    bugs found and fixed
  eval/                        evaluation reports
```

## 4. Data model

| Entity | Fields | Notes |
| --- | --- | --- |
| `Ticket` | `Id`, `Number` (`T-1001`), `Subject`, `Body`, `CustomerName`, `OrderNumber?`, `Status` (Open, Pending, Closed), `CreatedAt` | Seeded from `seed/tickets.json`; also created through `POST /api/tickets` |
| `TicketSummary` (owned by `Ticket`) | `Text`, `PromptVersion`, `GeneratedAt` | Null until generated |
| `TicketClassification` (owned by `Ticket`) | `Category?`, `Priority?`, `Reason?`, `Status` (NotRun, Done, NeedsManual), `PromptVersion`, `ClassifiedAt` | |
| `Article` | `Id`, `Slug`, `Title`, `BodyMarkdown`, `UpdatedAt` | Seeded from `seed/articles/*.md` |
| `ArticleChunk` | `Id`, `ArticleId`, `Ordinal`, `Heading`, `Text`, `ContentHash`, `EmbeddingModel`, `Embedding vector(1024)` | HNSW index, cosine distance |
| `FollowUpProposal` | `Id` (Guid), `TicketId`, `ReplyRunId` (Guid), `Title`, `Notes`, `DueDate?`, `Status` (Pending, Confirmed, Expired), `CreatedAt`, `ExpiresAt` | Unique on (`TicketId`, `ReplyRunId`) |
| `FollowUpTask` | `Id`, `TicketId`, `ProposalId` (unique), `Title`, `Notes`, `DueDate?`, `CreatedAt` | Created only by the Confirm endpoint |
| `ModelCall` | `Id`, `Timestamp`, `Feature` (Reply, Summary, Classification), `Provider`, `Model`, `PromptVersion`, `InputTokens`, `OutputTokens`, `CostUsd?`, `LatencyMs`, `IsEstimated`, `Outcome` (Success, Error, Cancelled), `TicketId?`, `ReplyRunId?` | One row per inner model call |

- **Enums:** `Category` = Billing, Shipping, Returns, Account, Technical, Other. `Priority` = Low, Normal, High, Urgent
- **Orders** aren't an entity. The fake order service reads `seed/orders.json` (order number, status, carrier, last update). One seeded ticket references an order number that isn't in the file
- **Fictional data:** an online outdoor-gear store called Kestrel Outfitters. About 25 tickets, 12 articles, 10 orders
- **Seeding:** runs at startup in Development, after the migrations, and is safe on every start. Migrations and seeding run under one PostgreSQL advisory lock, so instances starting together against one database take turns instead of failing. Tickets are matched on `Number` and only missing ones are added; an existing ticket is never overwritten, since it may have been changed through the app. Articles are matched on `Slug`, added when missing and updated (with a new `UpdatedAt`) when the file's title or body changed, so editing a seed article reaches the database and, from step 4, its chunks
- **Seed files:** `seed/` is copied next to the app by `SupportDesk.Data.csproj` (and into the Docker image). `Seed:Path` (default `seed`) is resolved from the app's base directory. Parsing is strict (unknown JSON fields fail), so a typo fails startup instead of seeding partial data. Each ticket in `tickets.json` also has `category` and `priority`: the labels it was written for, used to check that the seed covers every value. They aren't stored; classification starts as `NotRun`
- **Ticket numbers:** `T-` plus a number from the PostgreSQL sequence `ticket_numbers` (starts at 1001). Seeded tickets carry their own numbers, so the seeder moves the sequence past the highest existing number; it never moves it back
- **Lengths:** `Subject` ≤ 200, `Body` ≤ 5,000, `CustomerName` ≤ 200 characters, `OrderNumber` matches `KO-[0-9]{6}`. Lengths count UTF-16 code units (.NET `string.Length`), the same unit as the `varchar` limits, so an emoji counts as 2. `CreatedAt` is truncated to microseconds, PostgreSQL's precision, so the `201` response equals what a later `GET` returns

## 5. API

All routes are under `/api`. Errors use RFC 7807 problem details.

| Method and route | Purpose | Step |
| --- | --- | --- |
| `GET /api/health` | Liveness and database check | 1 |
| `GET /api/tickets` | List: id, number, subject, status, category, priority, created | 2 |
| `GET /api/tickets/{id}` | Detail with summary and classification | 2 |
| `POST /api/tickets` | Create a ticket | 2 |
| `GET /api/articles`, `GET /api/articles/{slug}` | Knowledge base | 2 |
| `GET /api/search?q=&k=5` | Similarity search over chunks, with scores | 4 |
| `POST /api/tickets/{id}/reply` | Suggested reply, streamed as SSE | 5 |
| `POST /api/tickets/{id}/summary` | Generate and save the summary | 6 |
| `POST /api/tickets/{id}/classification` | Classify and save | 7 |
| `GET /api/tickets/{id}/follow-ups` | Proposals and tasks for a ticket | 8 |
| `POST /api/follow-ups/{proposalId}/confirm` | Create the task (idempotent) | 8 |
| `GET /api/model-calls?ticketId=` | Usage and cost rows | 3b |

- JSON is camelCase and enums are their names (`"Open"`, `"NotRun"`), never numbers
- **Ticket list item:** `id`, `number`, `subject`, `status`, `category`, `priority` (both null until classified), `classificationStatus` (`NotRun`, `Done`, `NeedsManual`, so the list can show the needs-manual badge), `createdAt`. Newest first; `?status=` takes a status name in any case, anything else gives `400`
- **Ticket detail:** `id`, `number`, `subject`, `body`, `customerName`, `orderNumber`, `status`, `createdAt`, `summary` (null, or `text`, `promptVersion`, `generatedAt`), `classification` (`status`, `category`, `priority`, `reason`, `promptVersion`, `classifiedAt`)
- **Create:** `subject`, `body`, `customerName`, `orderNumber?`. Subject and customer name are trimmed; an empty order number counts as none. Returns `201` with `Location: /api/tickets/{id}` and the detail. A `400` lists every invalid field under its JSON name. A body that isn't a JSON object of the right shape (malformed, `null`, a number where a string belongs) gets a `400` problem details without field names
- **Articles:** the list has `slug`, `title`, `updatedAt`, ordered by title; the detail adds `bodyMarkdown` (without the front matter)
- **Classify:** `POST /api/tickets/{id}/classification` takes no body and returns `200` with the saved classification (the same shape as the detail's `classification`); calling it again replaces it. An answer that fails validation twice is saved and returned as `NeedsManual`, not as an error
- **Model errors** on the non-streaming AI endpoints (classification, summary) are problem details with fixed messages, never the provider's (`ModelProblems`, table in section 9). A failed call leaves the saved result as it was
- An unknown ticket ID or article slug returns `404` problem details

## 6. LLM pipeline

### 6.1 Providers and config

| Key | Values | Default (development) |
| --- | --- | --- |
| `AI:Chat:Provider` | `Fake`, `Ollama`, `Anthropic`, `OpenAI` | `Ollama` |
| `AI:Chat:Model` | model ID; empty means the provider's default | `qwen2.5:1.5b` (Ollama), `claude-opus-5-5` (Anthropic), `fake` (Fake); OpenAI has no default and needs it set |
| `AI:Embeddings:Provider` | `Fake`, `Ollama`, `Voyage`, `OpenAI` | `Ollama` |
| `AI:Embeddings:Model` | model ID; empty means the provider's default | `mxbai-embed-large` (Ollama), `voyage-3.5` (Voyage), `text-embedding-3-small` (OpenAI), `fake-hash` (Fake) |
| `AI:Embeddings:Dimensions` | `1024` | `1024` |
| `AI:Ollama:Endpoint` | URL; required when Ollama is the chat or embedding provider, set only in config (no code default) | `http://host.docker.internal:11434` (plain HTTP: Ollama on the host has no TLS) |
| `AI:Prompts:<name>` | prompt version | `reply.v2`, `summary.v2`, `classify.v2` (all three required) |
| `AI:PromptsPath` | folder | `prompts`, resolved from the app's base directory, where the build copies `prompts/` |
| `AI:Anthropic:Effort:<feature>` | `Low`, `Medium`, `High`, `ExtraHigh` | `Reply` Medium, `Summary` Low, `Classification` Low (see 6.7) |
| `AI:Anthropic:ApiKey`, `AI:OpenAI:ApiKey`, `AI:Voyage:ApiKey` | secret | none; user secrets or `AI__…__ApiKey` variables, never appsettings |
| `AI:Fake:ScenariosPath` | file | `fixtures/fake-scenarios.json` (the build copies `tests/fixtures/fake-scenarios.json` there) |
| `AI:Budget:*` | see 6.4 | |
| `AI:Timeouts:*`, `AI:Retry:*` | see 6.5 | |
| `AI:Pricing` | list of `Model`, `InputPerMTok`, `OutputPerMTok` in USD | see 6.6 |
| `AI:Usage:QueueCapacity` | rows waiting to be saved | 1000 (see 6.6) |

- Environment variables use double underscores, for example `AI__Chat__Provider=Fake`
- **Ollama** runs on the host (the default endpoint) or in the optional `ollama` service in `docker-compose.yml`, which only starts with `--profile ollama` and keeps models in the `ollama` volume. Containers in the compose project reach that service at `http://ollama:11434`. Models (pull them first):

  | Model | Size | Use |
  | --- | --- | --- |
  | `qwen2.5:1.5b` | about 1 GB | default chat model: small, fast, supports tools; weak at following instructions (in the step 5 check it used the sources but wrote no `[n]` markers) |
  | `qwen2.5:7b` | about 4.7 GB | better replies and tool calls, and cites sources; the model the step 10 evaluation runs with. On CPU it needs longer timeouts (6.5) |
  | `mxbai-embed-large` | about 670 MB | embeddings (1024 dimensions, from step 4) |

  Not used here, but an option on machines with more memory: `gemma4` (tools and thinking; the smallest tags, `e2b` and `e4b`, are 4.6–9.5 GB). Its thinking output adds latency and reaches the stream as reasoning content, which step 5 would have to drop. On CPU, `qwen2.5:1.5b` takes about 35 seconds for a reply in the dev container
- **Startup checks** (`ValidateOnStart`): an unknown provider, a missing key for a selected provider (the message names the setting, never a value), the fake provider in `Production`, a missing or malformed prompt file, and a broken scenarios file all stop the app before it serves requests
- **API keys** are read from `AI:<Provider>:ApiKey`, not `ANTHROPIC_API_KEY`: the dev container keeps that variable out of its environment so Claude Code doesn't pick it up. `.env` still holds `ANTHROPIC_API_KEY` and so on; docker compose maps them to `AI__Anthropic__ApiKey`. With `dotnet run` in the dev container, use user secrets
- **Packages:** Anthropic's official `Anthropic` package (it has an `IChatClient` adapter); `Microsoft.Extensions.AI.OpenAI`; `OllamaSharp` for Ollama chat and embeddings; Voyage through a small hand-written `IEmbeddingGenerator` over HTTP, since there is no official .NET package
- **Embedding dimension 1024:** `mxbai-embed-large` (Ollama) and `voyage-3.5` produce 1024 dimensions, and OpenAI `text-embedding-3-small` can be asked for 1024. So all three providers fit one `vector(1024)` column. Switching models still means re-embedding, because vectors from different models aren't comparable

### 6.2 Middleware order

Built once with `ChatClientBuilder`, outermost first:

```
RequestCapMiddleware          starts the request's token tally
FunctionInvokingChatClient    the tool loop, MaximumIterationsPerRequest = 3
RequestCapCounterMiddleware   adds each call's tokens to the tally, refuses the next call once it's over the cap
BudgetGuardMiddleware         per-call input estimate, sets MaxOutputTokens
UsageLoggingMiddleware        one ModelCalls row per inner call
RetryTimeoutMiddleware        retries before the first item, timeouts
ErrorMappingMiddleware        provider exceptions → typed exceptions (6.5); refusals → ModelRefusedException
provider IChatClient          Anthropic (wrapped in AnthropicOptionsMiddleware) / OpenAI / Ollama / Fake
```

- `ErrorMappingMiddleware` is innermost, so everything above it, including the retry middleware, only sees typed exceptions. `ModelUnavailableException` carries `StatusCode` and `IsTransient` (the rules in 6.5), which is what `RetryTimeoutMiddleware` decides on. Provider SDK exceptions are recognized by one `IModelErrorClassifier` per provider, registered in the API; `HttpRequestException` (Ollama, the fake provider) is handled in Core; anything else becomes a non-transient `ModelUnavailableException`. A cancellation the caller didn't ask for (an SDK or `HttpClient` timeout) is a transient `ModelUnavailableException`; the caller's own stays an `OperationCanceledException`
- A refusal is `FinishReason = ContentFilter`, which is how the Anthropic adapter reports `stop_reason = "refusal"`. In a stream, the updates after it (they carry usage) are still passed on, and `ModelRefusedException` is thrown when the stream ends; the non-streaming exception carries the usage

- **The request cap has two parts.** Outside the tool loop a middleware can't see the inner calls, and inside it doesn't know where a request starts. So `RequestCapMiddleware` puts a new `RequestTally` in `ChatOptions.AdditionalProperties` (in a copy of the caller's options), and `RequestCapCounterMiddleware`, right under the tool loop, reads it
- `UsageLoggingMiddleware` sits above the retries, so a retried call is one row with the whole latency. Its tokens and cost are the successful attempt's: a failed attempt's usage is held back with its other updates and discarded, so retries that a provider bills for are under-counted
- `BudgetExceededException` and the missing-context check are thrown when the call is made, not on the first read of a stream, so nothing reaches the provider
- The order is checked by a test that walks the built chain (`The_middleware_order_matches_the_plan`)
- Provider SDK retries are turned off (Anthropic SDK: max retries 0), so only `RetryTimeoutMiddleware` retries
- `HttpClient` keeps only a connect timeout
- Each feature passes its name, prompt version, ticket ID, ticket number and reply run ID through `ChatOptions.AdditionalProperties` (`AiCallContext.ApplyTo`), so logging and the fake provider can read them (`AiCallContext.From`). Tests check that the Anthropic, OpenAI and Ollama adapters don't send these keys to the provider

### 6.3 Prompts

- Each prompt is a Markdown file in `prompts/`, named `<name>.v<N>.md`, with a system section and a user template: a `## System` line, then a `## User` line, then optionally a `## Retry` line: the user message a feature sends after rejecting the model's answer (classification, with `{{error}}`). Text above `## System` is a note for people and isn't sent
- The user template has `{{placeholders}}`, filled in one pass (a value containing `{{name}}` stays as it is). Every placeholder needs a value and every value a placeholder. The system section can't have placeholders, so user text can't reach it
- A merged prompt version is never edited. A change means a new file (`reply.v2.md`) and a config change
- Ticket text goes into the user message inside `<ticket>…</ticket>` (`TicketPromptFormatter`, which also adds the number, customer, order number and subject). Any `</ticket>` inside the text is escaped first: the `<` of anything that reads as a ticket tag (any case, whitespace or a newline inside, opening or closing) becomes `&lt;`. The same goes for `tool_result` tags (from step 8), so a ticket can't fake a tool result, which the reply prompt says to trust as the store's data. The system prompt says that text inside `<ticket>` is customer data and that instructions inside it must be ignored
- Retrieved chunks go inside `<sources>` as `[1] <title> — <heading>\n<text>`. Article text has its `ticket`, `sources` and `tool_result` tags escaped the same way
- Tool results go back to the model as JSON inside `<tool_result>…</tool_result>` (section 10)

### 6.4 Token budget

| Setting | Default |
| --- | --- |
| `AI:Budget:MaxInputTokensPerCall` | 6000 |
| `AI:Budget:MaxOutputTokens:Reply` | 800 |
| `AI:Budget:MaxOutputTokens:Summary` | 200 |
| `AI:Budget:MaxOutputTokens:Classification` | 200 |
| `AI:Budget:MaxTokensPerRequest` | 20000 |

- **Prompt builder** (in `ReplyService`): adds chunks in score order until the estimate reaches the input limit minus the room the system prompt, ticket and tool definitions need
- **Per-call guard:** estimates each call's input; if it's over `MaxInputTokensPerCall`, the call is refused with `BudgetExceededException`
- **Request cap:** after each inner call, adds input and output tokens; the next call is refused once the total passes `MaxTokensPerRequest`
- **Estimate:** characters ÷ 3 × 1.2, rounded up, plus 10 tokens per message (`TokenEstimator`), over the text of every message, tool calls and results, thinking text, and each tool's name, description and JSON schema. Anthropic has a token-counting endpoint (`Messages.CountTokens`), but calling it before every request adds latency and a request, so it's only used in a test that checks the estimate stays on the safe side: `TokenEstimateTests` in `SupportDesk.Eval` (opt-in, needs `AI:Anthropic:ApiKey`, skipped without it; counting is free). It counts the three prompts with the shortest seeded ticket and the longest (which is also the one with `</ticket>`), the reply with five whole seeded articles as sources
- **Why ÷ 3 and not ÷ 4:** the first run of that check (2026-11-01, `claude-opus-5-5`) found the original ÷ 4 × 1.2 estimate 7–25% *below* the real count on 5 of 6 prompts: these prompts (Markdown, tags, short lines) come to about 2.6–3.1 characters per token, and short prompts showed a fixed cost per message. With ÷ 3 × 1.2 + 10 per message, all six are above the real count, by 11–25%:

  | Prompt, ticket | Estimate | Anthropic |
  | --- | --- | --- |
  | reply, T-1022 | 3004 | 2407 |
  | reply, T-1025 | 2894 | 2312 |
  | summary, T-1022 | 433 | 384 |
  | summary, T-1025 | 323 | 289 |
  | classify, T-1022 | 502 | 439 |
  | classify, T-1025 | 393 | 344 |

  Run the check again when a prompt or the model changes: the margin on short prompts is the smallest
- **Tokens of a call** for the cap and for usage rows: the provider's usage where it sent it (streamed usage updates are added up, as Microsoft.Extensions.AI does), the estimate where it didn't (input from the messages, output from what came back). A call that failed still counts what it used

### 6.5 Retries and timeouts

| Setting | Default |
| --- | --- |
| `AI:Retry:MaxAttempts` | 3 (1 call + 2 retries) |
| `AI:Retry:BaseDelayMs` | 500, doubled per attempt, with jitter in the upper half (250–500 ms, then 500–1000 ms) |
| `AI:Retry:MaxDelayMs` | 10000; the longest wait before a retry |
| `AI:Timeouts:NonStreamingSeconds` | 30 |
| `AI:Timeouts:FirstItemSeconds` | 20 |
| `AI:Timeouts:GapSeconds` | 15 |
| `AI:Timeouts:StreamTotalSeconds` | 120 |
| `AI:Timeouts:ConnectSeconds` | 10 (on `HttpClient`) |

- Retried: 429, 5xx, timeouts, connection errors, provider "overloaded" errors
- Not retried: 400, 401, 403, 404, refusals, cancellations
- For streams, retries are allowed only until the first item (text or tool call) has gone to the caller. After that, an error ends the stream
- **Updates before the first item are held back** (Anthropic's empty "message started" update, usage) and passed on with the first item, so a retry never shows the caller anything from the failed attempt. A stream that ends without an item (only usage, or a refusal) passes them on at the end
- **`Retry-After`** is waited for instead of the backoff when the provider sends it and the SDK exposes it: OpenAI (response headers) and the fake provider (`retryAfterSeconds` on an `error` step). The Anthropic SDK (13.3) doesn't expose headers on its exceptions, so Anthropic gets the backoff. A `Retry-After` over `MaxDelayMs` ends the call at once rather than leaving the user waiting
- **Timeouts:** `NonStreamingSeconds` per non-streaming attempt; `FirstItemSeconds` from the start of a streaming attempt to its first text or tool call (empty updates don't restart it; thinking text ends the wait, since the model is clearly working); then `GapSeconds` between updates, not counting the caller's own time between reads; `StreamTotalSeconds` for the whole streaming call, retries included. A timeout before the first item is transient and retried; after it, or the total, it isn't. `ModelUnavailableException.IsTimeout` tells timeouts apart
- Timeouts cancel the inner call's token, so they rely on the provider honoring cancellation, as the SDKs' `HttpClient` calls do. Every timer is a `CancellationTokenSource` on the injected `TimeProvider`, disposed when its attempt ends, so tests run them on a fake clock
- The caller's cancellation is never retried and never reported as a timeout: it comes out as `OperationCanceledException`, including during the wait before a retry
- **Embeddings** aren't in the chat pipeline; their errors are mapped to `ModelUnavailableException` in step 4, which is the first caller
- **Local models on CPU:** the defaults suit hosted providers and `qwen2.5:1.5b`. `qwen2.5:7b` on CPU in the dev container took about 2 minutes to load and 84 seconds for a 77-token reply (step 5 check, November 2026), so with the defaults the first call times out while the model loads, and the reply stream can run past `StreamTotalSeconds`. For a run against it, raise the timeouts with environment variables rather than changing the defaults: `AI__Timeouts__FirstItemSeconds=180`, `AI__Timeouts__GapSeconds=60`, `AI__Timeouts__StreamTotalSeconds=600`. Loading the model first (`curl http://ollama:11434/api/generate -d '{"model":"qwen2.5:7b","keep_alive":"30m"}'`) keeps the load out of the first call
- Every failure the caller sees is one of: `ModelUnavailableException` (retries used up, timeout, provider down), `BudgetExceededException`, `ModelRefusedException` (the provider declined the request), `OperationCanceledException`

### 6.6 Usage and cost

- `UsageLoggingMiddleware` reads `UsageDetails` from the response, or adds up the streamed usage updates, and writes a `ModelCall` row
- If a stream ends without usage (cancelled, failed), it writes an estimate with `IsEstimated = true`
- **Outcome:** `Success`; `Error` for any failure, refusals included; `Cancelled` for the caller's cancellation and for a stream the caller stopped reading
- **Model** is the one asked for (`ChatOptions.ModelId`, or the provider client's default), which is also what the price is looked up by. Provider is the client's `ChatClientMetadata.ProviderName`
- Every call needs an `AiCallContext`; a call without one is refused with `InvalidOperationException`, since its row would have no feature or prompt version
- **Writing:** rows go to `ModelCallBuffer`, a bounded channel (`AI:Usage:QueueCapacity`, 1000), which never waits: when it's full, the row is dropped with a warning. `ModelCallWriter`, a background service, saves them in batches of up to 100; a batch that fails to save is logged and dropped, so a database problem can't reach a stream. On shutdown it saves what's left for up to 5 seconds. `ModelCalls.TicketId` has no foreign key, so a row never fails because of its ticket
- Unknown-model warnings are logged once per model. A row that can't be built (it never should be) is dropped with a warning rather than breaking the call; a buffer closed at shutdown says so in its warning; a save cut short by shutdown is logged with the number of rows lost
- `GET /api/model-calls?ticketId=` returns the rows newest first (ties by ID), at most 200; without `ticketId` it returns the newest 200 of all
- Failures are logged with the feature, ticket ID, exception type and status code only: an SDK's message can echo the request, and so the ticket
- Cost = input tokens × input price + output tokens × output price, from `AI:Pricing`. An unknown model gets `CostUsd = null` and a warning log, never a made-up price
- `AI:Pricing` is a list of `{ Model, InputPerMTok, OutputPerMTok }`, not `AI:Pricing:<model>`: configuration keys use `:` as a separator, and Ollama model names (`qwen2.5:1.5b`) contain one
- Prices in `appsettings.json` (USD per million tokens, from Anthropic's price list as of October 2026; check them again before publishing results): `claude-opus-5-5` 4.00 / 20.00, `claude-sonnet-5-5` 2.00 / 10.00, `claude-haiku-5-5` 0.10 / 0.50; Ollama models (`qwen2.5:1.5b`, `qwen2.5:7b`) and `fake` 0 / 0. OpenAI prices are added if OpenAI is used
- Prompt text and ticket text are not logged by default

### 6.7 Anthropic-specific notes

These come from the current API and must be checked against the SDK version in use:

- Don't set `Temperature`, `TopP` or `TopK`; newer API versions reject them
- `claude-opus-5-5` can't turn thinking off; control cost with effort (`low` for classification and summary, `medium` for replies). The `Anthropic` package's adapter (13.3) sends `ChatOptions.Reasoning.Effort` as `output_config.effort` with `thinking.type = adaptive`, so `AnthropicOptionsMiddleware` sets it per feature from `AI:Anthropic:Effort` and no raw provider options are needed. `ReasoningEffort.None` is rejected at startup: it sends `thinking.type = disabled`, which these models answer with a 400. The same middleware drops `Temperature`, `TopP` and `TopK`; it's registered only for Anthropic, because other providers don't all accept a reasoning setting
- Forced tool choice (`any` or a named tool) returns a 400 on current models. Use automatic tool choice, and use structured output (`output_config.format`) for classification. Checked against the `Anthropic` 13.3 adapter's code: `GetResponseAsync<T>` sets `ChatOptions.ResponseFormat` to a JSON schema, and the adapter sends it as `output_config.format = { type: "json_schema", schema }` with `additionalProperties: false`; it sets `tool_choice` only from `ChatOptions.ToolMode`, so no tool is forced and no provider-specific options are needed (`Anthropic_structured_output_uses_output_config_format_and_no_forced_tool`). The adapter moves schema keywords the API doesn't take into the description, among them `maxLength`, so the 200-character limit on `reason` is only a hint to the model
- A response can stop with `stop_reason = "refusal"`; that maps to `ModelRefusedException`

### 6.8 Fake provider

- `FakeChatClient` and `FakeEmbeddingGenerator` live in `SupportDesk.Core/AI/Fake`, selected with `AI__Chat__Provider=Fake` and `AI__Embeddings__Provider=Fake`
- Scenarios are in `tests/fixtures/fake-scenarios.json`, keyed by ticket number (read from `ChatOptions.AdditionalProperties`), with a default scenario
- A scenario has turns per feature (`Reply`, `Summary`, `Classification`, or `*` for any). A call plays the turn numbered by how many assistant messages are already in the history, so the call after a tool round trip, or a classification retry that sends the earlier answer back, plays the next turn; the last turn repeats. A feature that sends earlier assistant messages as context skips turns the same way. The fake is stateless: a transport retry before the first item sends the same messages and replays the same turn, so "fail once, then succeed" is tested with a stub client, not a scenario. A feature a scenario doesn't script falls back to the default scenario
- A turn is a list of steps: `text` (streamed in chunks of `chunkSize` characters, `delayMs` before each), `tool_call` (name and arguments), `usage` (sent on the last update, as the adapters do), `error` (a `FakeProviderException`, an `HttpRequestException` with a status code and an optional `retryAfterSeconds`; before or after the first item depending on where it is in the list), `stall` (no output for N seconds), `finish` (the finish reason; `content_filter` is a refusal). Delays use `TimeProvider`, so tests run them on a fake clock, and every wait honors cancellation
- Named scenarios, on seeded tickets so the app shows them with the seed data: `tool-call-order` T-1002 (looks up `KO-205002`, then replies), `tool-call-proposal` T-1008 (looks up `KO-208815`, which isn't in `orders.json`, then says so and proposes a carrier trace, then finishes the reply), `error-before-first-item` T-1021 (503, every feature), `error-after-first-item` T-1022 (529 after some reply text), `stall` T-1023 (30 seconds before the first item, every feature), `stall-after-first-item` T-1019 (some text, then 30 seconds), `invalid-json` T-1024 (classification: not JSON, then JSON that fails the schema), `refusal` T-1025 (every feature). Through the pipeline both stalls end as timeouts: T-1023 waits past `FirstItemSeconds` on every attempt, so it fails after about a minute; T-1019 waits past `GapSeconds` after its first text and isn't retried
- The file is checked when it's loaded: a `default` scenario without a ticket number, one scenario per ticket, known features and step types, no unknown fields. `AI:Fake:ScenariosPath` points tests at their own file
- `FakeEmbeddingGenerator` returns a deterministic unit vector from a hash of the text (SHA-256 of the text and a block counter, read as 16-bit values). It's for running the app; retrieval tests use the committed fixtures instead
- The fake provider is refused at startup when the environment is `Production`

## 7. Streaming

- `POST /api/tickets/{id}/reply` returns `text/event-stream` through `TypedResults.ServerSentEvents`
- The request's `CancellationToken` flows through every service and middleware to the provider
- The client reads the stream with `fetch` and a `ReadableStream` (not `EventSource`, which reconnects by itself and would run the model again)

| Event | Payload |
| --- | --- |
| `token` | `{ "text": "..." }` |
| `tool_call` | `{ "name": "lookup_order_status", "arguments": {...}, "result": {...} }` or `{ "name": "propose_follow_up_task", "proposal": { "id", "title", "notes", "dueDate" } }` |
| `citations` | `{ "citations": [ { "marker": 1, "articleSlug", "articleTitle" } ], "removed": [7] }` |
| `usage` | `{ "inputTokens", "outputTokens", "costUsd", "estimated" }` |
| `error` | `{ "code": "model_unavailable" \| "budget_exceeded" \| "model_refused" \| "timeout" \| "internal_error", "message" }` |
| `done` | `{ "replyRunId" }` |

- `token` and `tool_call` events come in the order they happened. A `tool_call` is sent after the tool ran, and only for a call that ran: a call with invalid arguments or to an unknown tool gets an error result and no event
- A stream ends with exactly one `done` or one `error`
- `citations` and `usage` come after the last `token` and before `done`
- **Error codes:** `timeout` is a `ModelUnavailableException` with `IsTimeout`; `model_unavailable` is any other one. Embedding providers aren't behind the chat pipeline, so `RetrievalService` maps their failures to `ModelUnavailableException` (an unrequested cancellation is a timeout, an HTTP error keeps its status) and a failed retrieval gets `model_unavailable` or `timeout` too; `budget_exceeded` and `model_refused` match their exceptions; `internal_error` is anything else (a bug), logged with its exception. The `message` is fixed text per code, never the exception's message. The caller's own cancellation sends nothing: the stream just ends, the model call's row says `Cancelled`, and the reply logs `was cancelled`
- `citations` has one entry per distinct valid marker, in marker order, so every marker can be linked; two markers can name the same article. `usage` is the request's total: the provider's streamed usage, or the estimate (`estimated: true`), priced like the usage rows (6.6)
- **Client:** the SSE parser (`web/src/app/api/sse.ts`) handles frames split across chunks, several frames in one chunk, and `\n`, `\r\n` or `\r` line endings. A stream that ends without `done` or `error` is shown as a dropped connection. Citations link to the article page, `/articles/<slug>`
- **Client behavior:** one active reply per ticket page. Clicking "Suggest reply" again aborts the current `fetch` first. Leaving the page aborts it too

## 8. Retrieval and citations

- **Chunking** (`ArticleChunker`): split each article on `##` headings (`###` and deeper stay inside their section); the text before the first heading is a chunk without a heading, and empty sections are dropped. A section longer than 1,200 characters is split on paragraph boundaries (a paragraph that is itself too long is cut between words); each piece after the first starts with the last 150 characters of the one before, from a word boundary. The chunk's heading is kept with it. Ordinals count up from 0 across the article. The seed has 57 chunks; the "What can be returned" section of `returns-policy` is the one long enough to be split
- **Embedded text and content hash:** the embedded text is `heading + "\n" + text`, or the text alone for a chunk without a heading. The content hash is the lowercase hex SHA-256 of that text, so it changes exactly when what's embedded changes
- **Storage:** `ArticleChunks` in Data (not Core, because the column is pgvector's `Vector`), with `vector(1024)`, an HNSW index with `vector_cosine_ops`, and a cascade from `Articles`. Core reads and writes chunks through `IArticleChunkStore`
- **Sync** (`EmbeddingSyncService`): at startup in Development, after seeding and under the same advisory lock. Each article's chunks are matched to its stored rows by content hash and embedding model. A match keeps its vector (its ordinal is updated when sections moved); everything else is embedded in batches of `Retrieval:EmbeddingBatchSize`; stored rows left without a match are deleted, which covers deleted sections, renamed headings and changed text. Changing `AI:Embeddings:Model` matches nothing, so everything is re-embedded and the old rows are deleted. All changes are saved in one transaction after every batch is embedded. A provider that returns vectors of a size other than `AI:Embeddings:Dimensions` stops startup with an error naming both sizes. So does an unreachable provider: with the default Ollama embeddings, Development startup needs Ollama running with `mxbai-embed-large` pulled, or `AI__Embeddings__Provider=Fake`
- **Search** (`IRetrievalService`, `GET /api/search?q=&k=5`): embeds the query, takes the `k` nearest chunks of the current embedding model by cosine distance (`<=>`, which the HNSW index serves), then drops those with cosine similarity (1 − distance) below `Retrieval:MinSimilarity`. `q` is required and at most 500 characters after trimming; `k` is a whole number from 1 to 10 (default 5). The response is a list of `articleSlug`, `articleTitle`, `heading`, `text`, `similarity`, most similar first
- **Minimum similarity 0.5**, set in step 4 from the committed `mxbai-embed-large` fixtures. For the 15 retrieval questions the best chunk of the expected article scored 0.560–0.752. Four unrelated questions (weather, a poem, baking, football) scored at most 0.405 against any chunk. `mxbai-embed-large` gives about 0.3–0.4 to unrelated text, so the 0.3 first planned here would have kept everything. 0.5 sits between the two groups. With the fixtures, the expected article is first for 13 of the 15 questions and in the top 3 for all of them, without a query prefix. Another embedding model needs this tuned again

| Key | Default | Meaning |
| --- | --- | --- |
| `Retrieval:MinSimilarity` | `0.5` | Results below this cosine similarity are dropped (0–1) |
| `Retrieval:EmbeddingBatchSize` | `16` | Chunks per embedding request during the startup sync (1–256) |
| `Retrieval:ReplyChunks` | `5` | Chunks a suggested reply retrieves before the token budget trims them (1–10) |

- **Reply retrieval:** the query is the ticket's subject, a blank line and the body, cut at a word boundary to 1,000 characters (embedding models read only so much; `mxbai-embed-large` takes 512 tokens). The top `Retrieval:ReplyChunks` chunks (default 5) above the minimum similarity are kept, then trimmed to the token budget (6.4). With none left, `{{sources}}` says no articles matched. Article text is escaped like ticket text, for `ticket` and `sources` tags
- **Citations:** retrieved chunks are numbered `[1]`…`[N]` in the prompt. After the stream, the server parses markers from the full text (`[2]`, `[ 3 ]`, `[1, 2]`, adjacent like `[1][2]`; ASCII digits, at most 9); numbers outside `1…N` go into `removed` and are logged. Valid numbers map to chunks, then to articles, with duplicates removed
- **UI:** valid markers become links to the article; invalid ones stay as plain text

## 9. Structured output

- Classification uses `GetResponseAsync<ClassificationResult>` with a JSON schema: `category` (enum), `priority` (enum), `reason` (string, at most 200 characters), no extra properties
- The result is validated even when the provider enforces the schema, because some providers (Ollama models, some adapters) only follow it from the prompt
- **Validation** (`ClassificationValidator`) reads the raw response text, not `ChatResponse<T>.Result`, which ignores extra properties and fills missing ones with defaults: one JSON object (no code fence or other text, no duplicate keys), exactly `category`, `priority` and `reason`, enum names with exact casing (no numbers), `reason` a non-empty string of at most 200 characters after trimming. The errors name the allowed properties and values but never repeat what the model wrote, since they go back to it in a user turn and its answer can copy the ticket
- **Invalid JSON or schema mismatch:** one retry with the validation error added to the prompt, then `Status = NeedsManual`. The retry sends the rejected answer back as the assistant turn (left out when it's empty) and the prompt's `## Retry` message with the errors as the next user turn. Each rejection is logged at debug level with the raw output; `NeedsManual` is logged at information level without it
- **Saved:** status, category, priority, reason, prompt version and time, in one `UPDATE` of the classification columns. `NeedsManual` saves the prompt version and time with the other fields null
- **Summary:** plain text, 2–3 sentences, at most 600 characters (`SummaryOutput`). The output is trimmed; longer output is cut after the last full sentence that fits: `.`, `!` or `?` (with any closing quotes or brackets) followed by whitespace, so `1.5` or `kestrel.example` aren't sentence ends. If not even the first sentence fits, it's cut at the last whitespace before the limit (trailing commas and the like dropped) and ends with `…`. Abbreviations such as `e.g.` count as sentence ends, a known limit of the rule. Empty or whitespace-only output is a failure (`ModelOutputException`)
- **Summary endpoint** (`POST /api/tickets/{id}/summary`): one non-streaming call (`SummaryService`, feature `Summary`, `MaxOutputTokens` from `AI:Budget:MaxOutputTokens:Summary`), then the check, then the save (`Text`, `PromptVersion`, `GeneratedAt`), replacing the old summary. It returns `200` with the summary. Nothing is saved before the check passes, so a failure leaves the old summary as it was
- **Model errors as problem details** (`ModelProblems`, for every non-streaming feature): the title and detail are fixed texts, never an exception's message, and `code` is the reply stream's error code (PLAN.md 7) or `invalid_output`:

  | Failure | Status | `code` |
  | --- | --- | --- |
  | `ModelUnavailableException` | 503 | `model_unavailable`, or `timeout` when `IsTimeout` |
  | `ModelRefusedException` | 422 | `model_refused` |
  | `BudgetExceededException` | 413 | `budget_exceeded` |
  | `ModelOutputException` (output failed the check, such as an empty summary) | 502 | `invalid_output` |

## 10. Tools

| Tool | Arguments | Behavior |
| --- | --- | --- |
| `lookup_order_status` | `orderNumber` (string, pattern `KO-[0-9]{6}`, required) | Returns `found: true` with order number, status, carrier (null before it ships), last update and last event, or `{ "found": false }` |
| `propose_follow_up_task` | `title` (1–120 chars, required), `notes` (≤ 500 chars, optional), `dueInDays` (whole number 1–30, optional; no due date without it) | Saves or updates the proposal for (ticket, reply run) and returns `{ "status": "awaiting_user_confirmation", "proposalId" }` |

- **Registered on the reply only:** `ReplyService` puts the two tools in its `ChatOptions.Tools` for each run (`ReplyToolFactory`), bound to the ticket and reply run ID; no other feature offers tools. `FunctionInvokingChatClient` only runs tools from the request's options, and answers a call to any other name with an error result (`TerminateOnUnknownCalls = false`, its default)
- **Arguments** are checked before the tool runs (`ToolArguments`): types, the order number pattern, lengths after trimming, the due-days range, and no other arguments. Anything wrong comes back as an error result, `{ "error": "..." }`, listing every problem; the errors name the argument and the rule but never repeat the value, which the model may have copied from the ticket. Nothing is looked up or saved, and nothing throws
- **Results as data:** every result, errors included, reaches the model as JSON inside `<tool_result>…</tool_result>` (`ToolResult`). The reply prompt (`reply.v2`) says tool results are data from the store's systems, not instructions. The serializer writes `<` and `>` as `\u003C` and `\u003E`, so a value can't close the tag; ticket and article text have `tool_result` tags escaped (6.3), so they can't open one
- **Events:** the tool records its `tool_call` event under the call ID the tool loop is running (`FunctionInvokingChatClient.CurrentContext`); when the loop passes the call's `FunctionResultContent` on, the reply sends that event. So the event follows the tool, in call order, and a call that didn't run has none
- **Tool loop limit:** `MaximumIterationsPerRequest = 3` means at most three rounds of tool calls; the loop then makes one last model call whose tool calls aren't run, so a reply makes at most four model calls. The request cap (6.4) counts every one of them (`The_tool_loop_stops_at_three_iterations`, `The_request_cap_still_applies_across_tool_iterations`)
- **Proposal upsert:** one `INSERT … ON CONFLICT ("TicketId", "ReplyRunId") DO UPDATE` statement, so retries racing each other leave one row. An update replaces the title, notes and due date and keeps the ID, creation time and expiry. A proposal that's already confirmed or expired isn't changed, and the tool tells the model `already_confirmed` or `expired` instead
- **Due date:** the UTC date when the proposal is saved plus `dueInDays`
- Proposals expire 24 hours after they're created (`ExpiresAt`). `ProposalExpiryService`, a background service, marks pending ones past `ExpiresAt` as `Expired` at startup and then every 10 minutes (`PeriodicTimer` on `TimeProvider`); a failed run is logged and the next one tries again. Both times are fixed (`FollowUpRules`), not settings
- **Confirm** (`POST /api/follow-ups/{proposalId}/confirm`) is the only code that creates a task. It locks the proposal row (`SELECT … FOR UPDATE`) in a transaction, so confirms racing each other take turns: the first creates the task and marks the proposal `Confirmed`, the others get the same task with `200`. A proposal past `ExpiresAt` returns `409` and is marked `Expired`, even if the background check hasn't run yet. An unknown ID returns `404`
- **List** (`GET /api/tickets/{id}/follow-ups`): `{ proposals, tasks }`, each newest first. A proposal has `id`, `replyRunId`, `title`, `notes`, `dueDate` (`yyyy-MM-dd` or null), `status`, `createdAt`, `expiresAt`; a task has `id`, `proposalId`, `title`, `notes`, `dueDate`, `createdAt`. An unknown ticket returns `404`
- **Orders:** `IOrderStatusService` in Core; `SeedOrderStatusService` in Data reads `seed/orders.json` once, on the first lookup, and matches the order number exactly
- **UI:** the reply panel shows a card per tool call above the reply text: the order's status (or that it wasn't found), and the proposal with title, notes, due date and a Confirm button. A repeated proposal in the same run updates its card. The ticket page lists the tasks and the proposals still pending, which can be confirmed there too; it reloads when a reply proposes something or a proposal is confirmed
- **Function approval (checked in step 8):** Microsoft.Extensions.AI 10.11 has it (`ApprovalRequiredAIFunction`, `ToolApprovalRequestContent`, `ToolApprovalResponseContent`). Not used: when an approval-required tool is called, the tool loop stops and returns the request; to go on, the caller sends the whole conversation back with the approval, and the loop then runs the tool and calls the model again. That needs the conversation stored between requests, makes confirming cost another model call (and wait for it), and leaves the reply cut off at the tool call until someone decides. Expiry and confirm-twice would still need code of their own. The proposal tool keeps the reply in one stream and makes Confirm a plain database write

## 11. Testing

| Suite | Runs | Contents |
| --- | --- | --- |
| Unit | CI, every push | Services and middleware with the fake provider; prompt building; citation parsing; JSON validation |
| Integration | CI, every push | Testcontainers PostgreSQL + pgvector: migrations, seed, retrieval against committed fixtures, fixture hash check, endpoints |
| End-to-end | CI, every push | Playwright against the app with the fake provider |
| Eval | Opt-in | Real model: category accuracy, citation recall, injection resistance; report in `docs/eval/` |
| Static analysis | CI, every push | .NET analyzers (`latest-recommended`, warnings as errors), ESLint, `gitleaks`, SonarQube Cloud with coverage from .NET (coverlet, OpenCover) and Angular (lcov) |

- **SonarQube Cloud quality gate:** the default "Sonar way" gate on new code. Checked on 2026-10-31 with the SonarQube Cloud API (`api/qualitygates/list`): security, reliability and maintainability rating A, security hotspots reviewed 100%, coverage at least 80%, duplicated lines at most 3%. The gate shows on pull requests from step 1 and is a required check from step 3b
- **Quality profile:** "Sonar way comprehensive" for every language, set by the user as the organization default on 2026-10-31. The built-in default, "Sonar way core", has mostly security and bug rules (166 for C#, without maintainability rules such as S1144); comprehensive has 379 for C#. Profiles are chosen in the SonarQube Cloud account, not in the repository
- **Excluded from analysis:** `**/Migrations/**`, generated files, `seed/**`, `tests/fixtures/**`, plus build output (`bin`, `obj`, `node_modules`, `web/coverage`, `web/dist`). Set as `sonar.exclusions` in `sonar/SonarQube.Analysis.xml`. Migrations are also left out of coverlet's report (`tests/coverage.runsettings`)
- **Test code:** the .NET test projects are detected from their project files (`IsTestProject`); `web/src/**/*.spec.ts` and `web/e2e/**` are marked as tests with `sonar.test.inclusions`. Neither counts for coverage
- **Coverage:** .NET through coverlet in OpenCover format (`tests/coverage.runsettings`), read from `**/TestResults/**/coverage.opencover.xml`. Angular through Vitest's lcov report at `web/coverage/web/lcov.info`; its paths are relative to `web/`, so CI prefixes them with `web/` before the Sonar `end` step. Tool configuration, `web/src/main.ts` and `src/SupportDesk.Api/Program.cs` (bootstrap only; wiring goes in testable extension methods) are left out of coverage (`sonar.coverage.exclusions`)
- **TypeScript in the analysis:** SonarScanner for .NET (12.x) indexes the files outside MSBuild projects by default (`sonar.scanner.scanAll`), so `web/` is analyzed without a JavaScript project file in the solution
- **Warnings as errors in CI:** the scanner turns off `TreatWarningsAsErrors` while it analyzes, so CI runs a plain `dotnet build` first and a second build between `begin` and `end`
- **Local checks:** SonarQube for IDE in connected mode, with the rules of the project's quality profile; it doesn't run the gate. Under "Sonar way core" the IDE flagged C# rules (S1144) the cloud didn't run, so after a profile change check that both report the same issues
- **Rule review for step 3b (2026-11-01):** across steps 1–3a, every issue SonarQube Cloud raised (14 fixed, 1 open, no security hotspots) was a real finding and was fixed in code; none was noise or clashed with the .NET analyzers. So no rule is turned off yet; the table below starts empty
- **Rules turned off:** kept as `sonar.issue.ignore.multicriteria` entries in `sonar/SonarQube.Analysis.xml`, so every change shows in a pull request diff. Each one is listed here with its reason (filled in from step 3b on). False positives are marked in SonarQube Cloud by the user:

| Rule | Reason |
| --- | --- |
| — | — |

- CI runs `dotnet test --filter "Category!=Eval"`
- Eval tests carry `[Trait("Category", "Eval")]` and run with `dotnet test tests/SupportDesk.Eval --filter Category=Eval`
- **Embedding fixtures:** `tests/fixtures/embeddings/articles.json` and `questions.json` hold the model name, dimension, and per item the content hash and vector (articles also have slug and ordinal; questions the question text), one item per line. `dotnet run --project tools/EmbedFixtures` regenerates them with the configured embedding provider (by default Ollama `mxbai-embed-large`, which the committed files are made with) from `seed/articles` and `tests/fixtures/retrieval-questions.json`. A test fails, naming that command, when a chunk's or question's hash no longer matches. Retrieval tests store the fixture vectors through the normal startup sync and embed each question to its fixture vector; a text without a fixture fails the test
- **Startup embedding in tests:** startup now embeds the articles, so the in-memory API in integration tests uses the fake chat and embedding providers unless a test overrides them

## 12. Steps

| Step | Spec | Branch |
| --- | --- | --- |
| 1 | [Skeleton and workflow](docs/specs/01-skeleton.md) | pushed straight to `main` |
| 2 | [Tickets and knowledge base](docs/specs/02-tickets-kb.md) | `step/02-tickets-kb` |
| 3a | [Pipeline and fake provider](docs/specs/03a-pipeline.md) | `step/03a-pipeline` |
| 3b | [Safeguards](docs/specs/03b-safeguards.md) | `step/03b-safeguards` |
| 4 | [Embeddings](docs/specs/04-embeddings.md) | `step/04-embeddings` |
| 5 | [Suggested reply with RAG](docs/specs/05-rag-reply.md) | `step/05-rag-reply` |
| 6 | [Summary](docs/specs/06-summary.md) | `step/06-summary` |
| 7 | [Classification](docs/specs/07-classification.md) | `step/07-classification` |
| 8 | [Tool calling](docs/specs/08-tool-calling.md) | `step/08-tool-calling` |
| 9 | [Adversarial tests](docs/specs/09-adversarial.md) | `step/09-adversarial` |
| 10 | [Evaluation](docs/specs/10-evaluation.md) | `step/10-evaluation` |
| 11 | [UI polish and end-to-end tests](docs/specs/11-ui-e2e.md) | `step/11-ui-e2e` |
| 12 | [Final review and README](docs/specs/12-readme.md) | `step/12-readme` |

## 13. Decisions

| Decision | Why |
| --- | --- |
| Microsoft.Extensions.AI over a provider SDK directly | One abstraction for Anthropic, OpenAI, Ollama and the fake provider; middleware composes |
| Retries in `IChatClient` middleware, not the HTTP resilience handler | The HTTP handler can't see streamed items or in-stream provider errors |
| `fetch` instead of `EventSource` | `EventSource` reconnects by itself and would run the model again; it only supports GET |
| Numbered citation markers | Easier for the model to copy exactly than IDs, and easy to check |
| A proposal tool instead of a create tool, and not function approval | `FunctionInvokingChatClient` runs tools automatically; a write needs the user. Approval (MEAI 10.11) would need the conversation stored and another model call to confirm (section 10) |
| `vector(1024)` | All three embedding providers can produce it |
| Committed embedding fixtures | Retrieval tests need real vectors without calling an API in CI |
| `ModelCalls` table instead of logs only | Real cost numbers for the README and the UI |
| Ollama for development | Free, local; one named model that supports tools and JSON output |
| SonarQube Cloud, not self-hosted SonarQube | Free for public repositories with pull request analysis; the free self-hosted edition only analyzes the main branch, and running it adds a server and database to the dev container |
| Sonar gate required only from step 3b | A strict gate on an empty skeleton blocks on noise; by step 3b there's real code to tune the rules against |

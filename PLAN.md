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

## 6. LLM pipeline

### 6.1 Providers and config

| Key | Values | Default (development) |
| --- | --- | --- |
| `AI:Chat:Provider` | `Fake`, `Ollama`, `Anthropic`, `OpenAI` | `Ollama` |
| `AI:Chat:Model` | model ID | `qwen2.5:7b` (Ollama), `claude-opus-5-5` (Anthropic) |
| `AI:Embeddings:Provider` | `Fake`, `Ollama`, `Voyage`, `OpenAI` | `Ollama` |
| `AI:Embeddings:Model` | model ID | `mxbai-embed-large` |
| `AI:Embeddings:Dimensions` | `1024` | `1024` |
| `AI:Ollama:Endpoint` | URL | `http://host.docker.internal:11434` |
| `AI:Prompts:<name>` | prompt version | `reply.v1`, `summary.v1`, `classify.v1` |
| `AI:Budget:*` | see 6.4 | |
| `AI:Timeouts:*`, `AI:Retry:*` | see 6.5 | |
| `AI:Pricing:<model>` | `InputPerMTok`, `OutputPerMTok` in USD | see 6.6 |

- Environment variables use double underscores, for example `AI__Chat__Provider=Fake`
- **Packages:** Anthropic's official `Anthropic` package (it has an `IChatClient` adapter); `Microsoft.Extensions.AI.OpenAI`; `OllamaSharp` for Ollama chat and embeddings; Voyage through a small hand-written `IEmbeddingGenerator` over HTTP, since there is no official .NET package
- **Embedding dimension 1024:** `mxbai-embed-large` (Ollama) and `voyage-3.5` produce 1024 dimensions, and OpenAI `text-embedding-3-small` can be asked for 1024. So all three providers fit one `vector(1024)` column. Switching models still means re-embedding, because vectors from different models aren't comparable

### 6.2 Middleware order

Built once with `ChatClientBuilder`, outermost first:

```
RequestCapMiddleware          sums usage across the tool loop, stops at the request cap
FunctionInvokingChatClient    the tool loop, MaximumIterationsPerRequest = 3
BudgetGuardMiddleware         per-call input estimate, sets MaxOutputTokens
UsageLoggingMiddleware        one ModelCalls row per inner call
RetryTimeoutMiddleware        retries before the first item, timeouts
provider IChatClient          Anthropic / OpenAI / Ollama / Fake
```

- Provider SDK retries are turned off (Anthropic SDK: max retries 0), so only `RetryTimeoutMiddleware` retries
- `HttpClient` keeps only a connect timeout
- Each feature passes its name, prompt version, ticket ID and reply run ID through `ChatOptions.AdditionalProperties`, so logging and the fake provider can read them

### 6.3 Prompts

- Each prompt is a Markdown file in `prompts/`, named `<name>.v<N>.md`, with a system section and a user template
- A merged prompt version is never edited. A change means a new file (`reply.v2.md`) and a config change
- Ticket text goes into the user message inside `<ticket>…</ticket>`. Any `</ticket>` inside the text is escaped first. The system prompt says that text inside `<ticket>` is customer data and that instructions inside it must be ignored
- Retrieved chunks go inside `<sources>` as `[1] <title> — <heading>\n<text>`

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
- **Estimate:** characters ÷ 4 × 1.2. Anthropic has a token-counting endpoint (`Messages.CountTokens`), but calling it before every request adds latency and a request, so it's only used in a test that checks the estimate stays on the safe side

### 6.5 Retries and timeouts

| Setting | Default |
| --- | --- |
| `AI:Retry:MaxAttempts` | 3 (1 call + 2 retries) |
| `AI:Retry:BaseDelayMs` | 500, exponential with jitter; `Retry-After` is honored when present |
| `AI:Timeouts:NonStreamingSeconds` | 30 |
| `AI:Timeouts:FirstItemSeconds` | 20 |
| `AI:Timeouts:GapSeconds` | 15 |
| `AI:Timeouts:StreamTotalSeconds` | 120 |
| `AI:Timeouts:ConnectSeconds` | 10 (on `HttpClient`) |

- Retried: 429, 5xx, timeouts, connection errors, provider "overloaded" errors
- Not retried: 400, 401, 403, 404, refusals, cancellations
- For streams, retries are allowed only until the first item (text or tool call) has gone to the caller. After that, an error ends the stream
- Every failure the caller sees is one of: `ModelUnavailableException` (retries used up, timeout, provider down), `BudgetExceededException`, `ModelRefusedException` (the provider declined the request), `OperationCanceledException`

### 6.6 Usage and cost

- `UsageLoggingMiddleware` reads `UsageDetails` from the response, or from the last streamed update, and writes a `ModelCall` row
- If a stream ends without usage (cancelled, failed), it writes an estimate with `IsEstimated = true`
- Cost = input tokens × input price + output tokens × output price, from `AI:Pricing`. An unknown model gets `CostUsd = null` and a warning log, never a made-up price
- Prices in `appsettings.json` (USD per million tokens, from Anthropic's price list as of October 2026; check them again before publishing results): `claude-opus-5-5` 4.00 / 20.00, `claude-sonnet-5-5` 2.00 / 10.00, `claude-haiku-5-5` 0.10 / 0.50; Ollama models 0 / 0. OpenAI prices are added if OpenAI is used
- Prompt text and ticket text are not logged by default

### 6.7 Anthropic-specific notes

These come from the current API and must be checked against the SDK version in use:

- Don't set `Temperature`, `TopP` or `TopK`; newer API versions reject them
- `claude-opus-5-5` can't turn thinking off; control cost with effort (`low` for classification and summary, `medium` for replies), passed through the adapter's provider-specific options
- Forced tool choice (`any` or a named tool) returns a 400 on current models. Use automatic tool choice, and use structured output (`output_config.format`) for classification
- A response can stop with `stop_reason = "refusal"`; that maps to `ModelRefusedException`

### 6.8 Fake provider

- `FakeChatClient` and `FakeEmbeddingGenerator` live in `SupportDesk.Core/AI/Fake`, selected with `AI__Chat__Provider=Fake` and `AI__Embeddings__Provider=Fake`
- Scenarios are in `tests/fixtures/fake-scenarios.json`, keyed by ticket number (read from `ChatOptions.AdditionalProperties`), with a default scenario
- A scenario is a list of steps: `text` (streamed in chunks with a set delay), `tool_call` (name and arguments), `usage`, `error` (status code, before or after the first item), `stall` (no output for N seconds)
- `FakeEmbeddingGenerator` returns a deterministic vector from a hash of the text. It's for running the app; retrieval tests use the committed fixtures instead
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
| `error` | `{ "code": "model_unavailable" \| "budget_exceeded" \| "model_refused" \| "timeout", "message" }` |
| `done` | `{ "replyRunId" }` |

- A stream ends with exactly one `done` or one `error`
- `citations` and `usage` come after the last `token` and before `done`
- **Client behavior:** one active reply per ticket page. Clicking "Suggest reply" again aborts the current `fetch` first. Leaving the page aborts it too

## 8. Retrieval and citations

- **Chunking:** split each article on `##` headings; a section longer than 1,200 characters is split on paragraph boundaries, with 150 characters of overlap. The chunk's heading is kept with it
- **Content hash:** SHA-256 of `heading + "\n" + text`
- **Re-embedding:** at startup, and after seeding, chunks whose hash or embedding model differs from the stored row are re-embedded. Unchanged chunks are skipped
- **Search:** cosine distance, top `k = 5`, minimum similarity 0.3 (tuned in step 4 against the test questions)
- **Citations:** retrieved chunks are numbered `[1]`…`[N]` in the prompt. After the stream, the server parses markers from the full text; numbers outside `1…N` go into `removed` and are logged. Valid numbers map to chunks, then to articles, with duplicates removed
- **UI:** valid markers become links to the article; invalid ones stay as plain text

## 9. Structured output

- Classification uses `GetResponseAsync<ClassificationResult>` with a JSON schema: `category` (enum), `priority` (enum), `reason` (string, at most 200 characters), no extra properties
- The result is validated even when the provider enforces the schema, because some providers (Ollama models, some adapters) only follow it from the prompt
- **Invalid JSON or schema mismatch:** one retry with the validation error added to the prompt, then `Status = NeedsManual`
- **Summary:** plain text, 2–3 sentences, at most 600 characters; longer output is cut at the last full sentence

## 10. Tools

| Tool | Arguments | Behavior |
| --- | --- | --- |
| `lookup_order_status` | `orderNumber` (string, pattern `KO-\d{6}`) | Returns status, carrier, last update, or `{ "found": false }` |
| `propose_follow_up_task` | `title` (≤ 120 chars), `notes` (≤ 500 chars), `dueInDays` (1–30) | Saves or updates the proposal for (ticket, reply run) and returns `{ "status": "awaiting_user_confirmation", "proposalId" }` |

- Only these two tools are registered. Arguments are validated before the tool runs; an invalid argument returns an error result to the model, not an exception
- Proposals expire after 24 hours (`ExpiresAt`); a background check marks them `Expired`
- `POST /api/follow-ups/{proposalId}/confirm` creates the task once. A second confirm returns the same task with `200`; an expired proposal returns `409`
- If the Microsoft.Extensions.AI version in use has function-approval support, it may replace the proposal tool; that's decided in step 8

## 11. Testing

| Suite | Runs | Contents |
| --- | --- | --- |
| Unit | CI, every push | Services and middleware with the fake provider; prompt building; citation parsing; JSON validation |
| Integration | CI, every push | Testcontainers PostgreSQL + pgvector: migrations, seed, retrieval against committed fixtures, fixture hash check, endpoints |
| End-to-end | CI, every push | Playwright against the app with the fake provider |
| Eval | Opt-in | Real model: category accuracy, citation recall, injection resistance; report in `docs/eval/` |
| Static analysis | CI, every push | .NET analyzers (`latest-recommended`, warnings as errors), ESLint, `gitleaks`, SonarQube Cloud with coverage from .NET (coverlet, OpenCover) and Angular (lcov) |

- **SonarQube Cloud quality gate:** the default "Sonar way" gate on new code. Checked on 2026-10-31 with the SonarQube Cloud API (`api/qualitygates/list`): security, reliability and maintainability rating A, security hotspots reviewed 100%, coverage at least 80%, duplicated lines at most 3%. The gate shows on pull requests from step 1 and is a required check from step 3b
- **Excluded from analysis:** `**/Migrations/**`, generated files, `seed/**`, `tests/fixtures/**`, plus build output (`bin`, `obj`, `node_modules`, `web/coverage`, `web/dist`). Set as `sonar.exclusions` in `sonar/SonarQube.Analysis.xml`. Migrations are also left out of coverlet's report (`tests/coverage.runsettings`)
- **Test code:** the .NET test projects are detected from their project files (`IsTestProject`); `web/src/**/*.spec.ts` and `web/e2e/**` are marked as tests with `sonar.test.inclusions`. Neither counts for coverage
- **Coverage:** .NET through coverlet in OpenCover format (`tests/coverage.runsettings`), read from `**/TestResults/**/coverage.opencover.xml`. Angular through Vitest's lcov report at `web/coverage/web/lcov.info`; its paths are relative to `web/`, so CI prefixes them with `web/` before the Sonar `end` step. Tool configuration, `web/src/main.ts` and `Program.cs` are left out of coverage (`sonar.coverage.exclusions`)
- **TypeScript in the analysis:** SonarScanner for .NET (12.x) indexes the files outside MSBuild projects by default (`sonar.scanner.scanAll`), so `web/` is analyzed without a JavaScript project file in the solution
- **Warnings as errors in CI:** the scanner turns off `TreatWarningsAsErrors` while it analyzes, so CI runs a plain `dotnet build` first and a second build between `begin` and `end`
- **Local checks:** SonarQube for IDE in connected mode, with the same rules; it doesn't run the gate
- **Rules turned off:** kept as `sonar.issue.ignore.multicriteria` entries in `sonar/SonarQube.Analysis.xml`, so every change shows in a pull request diff. Each one is listed here with its reason (filled in from step 3b on). False positives are marked in SonarQube Cloud by the user:

| Rule | Reason |
| --- | --- |
| — | — |

- CI runs `dotnet test --filter "Category!=Eval"`
- Eval tests carry `[Trait("Category", "Eval")]` and run with `dotnet test tests/SupportDesk.Eval --filter Category=Eval`
- **Embedding fixtures:** `tests/fixtures/embeddings/articles.json` and `questions.json` hold the model name, dimension, and per item the content hash and vector. `dotnet run --project tools/EmbedFixtures` regenerates them with the configured embedding provider

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
| A proposal tool instead of a create tool | `FunctionInvokingChatClient` runs tools automatically; a write needs the user |
| `vector(1024)` | All three embedding providers can produce it |
| Committed embedding fixtures | Retrieval tests need real vectors without calling an API in CI |
| `ModelCalls` table instead of logs only | Real cost numbers for the README and the UI |
| Ollama for development | Free, local; one named model that supports tools and JSON output |
| SonarQube Cloud, not self-hosted SonarQube | Free for public repositories with pull request analysis; the free self-hosted edition only analyzes the main branch, and running it adds a server and database to the dev container |
| Sonar gate required only from step 3b | A strict gate on an empty skeleton blocks on noise; by step 3b there's real code to tune the rules against |

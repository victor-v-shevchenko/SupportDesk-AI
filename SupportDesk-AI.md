# Project: SupportDesk AI (.NET + Angular + LLM)

A support-ticket assistant that shows you can build production-style LLM features: RAG, structured output, tool calling and streaming, plus the safeguards around them.

## Features

- **Ticket list and details** with fictional seeded tickets and a small knowledge base of help articles; new tickets can be created through the API
- **Summarize a ticket** in 2–3 sentences, on demand
- **Suggest a reply** grounded in knowledge-base articles (RAG), with citations to the articles used, streamed to the UI
- **Classify** category and priority as structured JSON output, validated before saving
- **Tool calling:** the model can call "look up order status" (a fake service) and propose "create follow-up task", which the user confirms
- **Safeguards:** timeouts, retries, a token budget per request, token usage and cost logged per call, prompt-injection defenses, and a clear message when the model is unavailable

## Non-goals

- Authentication, users and roles
- Multi-tenancy
- Real email or chat intake (tickets are seeded or created through the API)
- Fine-tuning or hosting models

## Stack

| Part | Choice | Why |
| --- | --- | --- |
| API | ASP.NET Core (.NET 10) Minimal API | Your main stack |
| Chat model | Microsoft.Extensions.AI `IChatClient` | Works with any provider: Anthropic or OpenAI, or Ollama locally for free development |
| Embeddings | Microsoft.Extensions.AI `IEmbeddingGenerator` | Configured separately from chat, because Anthropic has no embeddings API: use Voyage, OpenAI, or Ollama (`mxbai-embed-large`); all three can produce 1024-dimension vectors |
| Local model | Ollama with one named model that supports tool calling and JSON-schema output (for example `qwen2.5:7b`) | Tool calling and structured output only work well on some Ollama models, so development uses one known model |
| Data | PostgreSQL with pgvector, EF Core | Adds PostgreSQL to your profile; vector search in the same database |
| UI | Angular with Signals | Streams responses over SSE, read with `fetch` |
| Resilience | Custom `IChatClient` middleware for retries and stream timeouts; `HttpClient` connect timeout only | The HTTP handler can't see streamed tokens (see the policy below) |
| Tests | xUnit, Testcontainers (PostgreSQL + pgvector), Playwright | Retrieval tests need a real pgvector database |
| Run | Docker Compose | One command |

## Steps (one spec and one commit each)

Every feature step ships its API, tests and a small piece of UI, so streaming and tool calls can be checked in the browser (with Playwright MCP) as soon as they exist. Each safeguard is built in the step that first needs it, not added afterwards.

1. **Skeleton and workflow:** solution, Docker Compose with PostgreSQL + pgvector, Angular shell, `.devcontainer/`, GitHub Actions CI (build and tests, with Testcontainers), `CLAUDE.md`, `.claude/agents/reviewer.md`, `.mcp.json`. This step is pushed straight to `main`; branch protection is turned on once CI exists
   - *Why:* step 1 can't go through the protected pull request flow, because the CI check that protection requires doesn't exist yet
2. **Tickets and knowledge base:** entities, migrations, seed data, list, detail and create endpoints, ticket list and detail pages. Seeded tickets include order numbers, and the fake order service has matching records, plus one order number that isn't found
   - *Why:* the order status tool in step 8 needs real order numbers to look up
3. **LLM pipeline**, split into two pull requests so each review stays focused:
   - **3a. Pipeline and fake provider:** one shared `IChatClient` pipeline built with `ChatClientBuilder` that every feature uses, provider config, versioned prompt files, error mapping to a "model unavailable" response, and a fake provider (`AI__Chat__Provider=Fake`) with scripted responses, including a streamed reply and a tool call
   - **3b. Safeguards:** retry and timeout middleware, the per-call budget guard and request cap, usage and cost logging to the `ModelCalls` table
   - *Why:* retries and timeouts have to exist before streaming starts in step 5, and every later UI step needs the fake provider for Playwright checks
4. **Embeddings:** chunk articles, store vectors with the embedding model name and a content hash, re-embed on change, similarity search endpoint, and the `tools/embed-fixtures` script that generates the committed test embeddings
5. **Suggested reply with RAG:** retrieve the top chunks, trim them to the budget, number them `[1]`…`[N]` in the prompt with ticket text marked as data, stream the answer over SSE, check citations on the final `citations` event, streaming reply panel in the UI with cancellation
6. **Summary:** 2–3 sentence ticket summary, generated on demand and saved
7. **Classification:** structured JSON output with schema validation and the retry/fallback policy, classification badges in the UI
8. **Tool calling:** order status lookup and the follow-up task proposal with a Confirm button, shown in the reply panel
9. **Adversarial tests:** prompt-injection cases, provider faults (429, 5xx, errors in the middle of a stream), dropped and cancelled streams, double submits
   - *Why:* the defenses already exist from steps 3, 5 and 8; this step tries to break them
10. **Evaluation:** a golden set of tickets with expected categories and the articles each answer must cite, plus injection cases, run as an opt-in suite with a report
11. **UI polish and end-to-end tests:** layout, loading and error states, Playwright end-to-end tests in CI
12. **README:** a final whole-project review (architecture, consistency across steps, README accuracy), README, screenshot

## LLM pipeline and safeguards

- **One pipeline:** every model call goes through the same `IChatClient` built in step 3; features never create provider clients themselves
- **Prompts:** each prompt is a file under `prompts/` with a version ID (for example `reply.v3.md`). The version is logged with every call and recorded in evaluation reports
- **Middleware order (outermost first):** request cap → function invocation (tool loop) → per-call budget guard → usage and cost logging → retry and timeout → provider client
  - *Why:* one reply with tools can make several model calls. The request cap has to cover the whole loop, while the budget guard, logging and retries apply to each inner call, whose context grows with each tool result
- **Where retries live:** in a custom `IChatClient` middleware, not in the HTTP resilience handler
  - *Why:* with a streamed response, the HTTP handler counts the call as finished once the response headers arrive. It can't see tokens, so it can't tell whether a token has been sent, and it can't catch a provider error sent inside the stream (such as an "overloaded" event)
- **Retries:** retry only on transient errors (429, 5xx, timeouts, provider "overloaded" errors), with backoff, and only **before the first streamed item** (text or tool call) has been sent to the client. After that, a failure ends the stream with an `error` event instead of a retry
  - *Why:* in the tool loop, the first thing the model streams can be a tool call rather than text, so "first token" would leave a gap
- **Timeouts:** in the same middleware: a short timeout for non-streaming calls, a separate, longer one for streams, and a timeout on the gap between tokens. `HttpClient` only keeps a connect timeout
- **No double retries:** turn off the provider SDK's built-in retries, so only the middleware retries
- **Token budget**, in three parts:
  - **Prompt builder** (in the RAG service, before the pipeline): trims retrieved chunks, lowest score first, so the prompt fits the input limit
  - **Per-call guard** (middleware inside the tool loop): estimates each call's input tokens and refuses a call over the limit; sets `MaxOutputTokens`
  - **Request cap** (outermost middleware): adds up usage across the tool loop and stops the request when its total is reached
  - Estimates use characters ÷ 4 with a 20% safety margin, or the provider's token-counting endpoint where there is one
  - *Why:* middleware only sees chat messages and can't tell which text came from the knowledge base, so trimming chunks has to happen where the prompt is built. There's no local tokenizer for Claude, so a rough estimate with a margin protects the budget without an extra API call
- **Tool loop limit:** set the function-invoking client's maximum number of iterations per request (for example 3)
- **Usage and cost:** every call writes a row to a `ModelCalls` table: feature, model, prompt version, input and output tokens, cost, latency, and an "estimated" flag. Prices come from a price table in config ($0 for Ollama). If a stream is cut off before the provider reports usage, the row holds an estimate with the flag set
  - *Why:* providers usually send usage at the end of a stream, so an aborted stream reports none. A table instead of log lines gives real numbers for the README and a small cost readout in the UI
- **Model unavailable:** a typed error that the UI shows as a clear message, never a raw exception

## Streaming

- **Transport:** the server sends Server-Sent Events from `POST /api/tickets/{id}/reply` with .NET 10's built-in `TypedResults.ServerSentEvents`, not a hand-written response; Angular reads them with `fetch` and a `ReadableStream`, not `EventSource`
  - *Why:* when the server closes the stream, `EventSource` reconnects to the same URL on its own, which runs the model again and costs tokens again. It also supports only GET with no custom headers
- **Events:** `token`, `tool_call`, `citations`, `usage`, `error`, `done`
- **Cancellation:** the request's `CancellationToken` flows through the whole pipeline to the provider. Clicking "Suggest reply" again, or leaving the page, cancels the running stream before a new one starts
  - *Why:* without this, a double click pays for two replies

## Structured output and citations

- **Classification:** category and priority are enums, requested with `GetResponseAsync<T>`; the model's JSON is validated against a schema. If it's invalid, retry once with the validation error added to the prompt, then save the ticket as "needs manual classification"
  - *Why:* some providers enforce a JSON-schema response format and others only follow instructions in the prompt, so the output is validated either way
- **When it runs:** classification and summary run on demand from the ticket page and are saved with the ticket; a "Regenerate" button runs them again
- **Citations:** retrieved chunks are numbered `[1]`…`[N]` in the prompt, and the model cites those numbers as inline markers. The server maps each number back to its chunk ID
  - *Why:* short numbers are easier for the model to copy exactly than database IDs or GUIDs, and easier to check
  - The check runs on the final `citations` event: any number outside `1…N` is removed and logged
  - Valid chunks are mapped to their articles, with duplicates removed, so the UI lists articles, not chunks
  - The UI turns only valid markers into links; invalid markers stay as plain text
  - *Why:* streamed text has already reached the browser by the time the citations can be checked, so it can't be taken back

## Embeddings and retrieval

- Chunk on article headings, with a maximum chunk size and a small overlap
- Each vector stores the embedding model name and the chunk's content hash; changing the model or the content triggers re-embedding (the pgvector column dimension is fixed per model)
- Chat provider and embedding provider are separate config settings
- Embeddings for the seed articles **and** for the test questions are precomputed and committed, so retrieval tests in CI need no embedding API
  - *Why:* a fake embedding generator would only test the SQL, not whether retrieval finds the right article
- **Keeping fixtures current:** `tools/embed-fixtures` regenerates the committed embeddings and records the embedding model it used. A CI test fails when a fixture's content hash doesn't match the current chunk
  - *Why:* without it, a change to the chunking or an article leaves stale vectors, and retrieval tests quietly check old data
- The opt-in evaluation uses the same embedding model as the fixtures, or re-embeds first

## Security

- **Prompt injection:** ticket text is user input. From step 5 on, it goes into the prompt clearly marked as data, never as instructions, and the system prompt says to ignore instructions found inside it
- **Write tools need confirmation:** the model's tool is `propose_follow_up_task`. It only saves a proposal and returns "awaiting user confirmation". The Confirm button calls a normal API endpoint, which creates the task idempotently (one task per proposal ID)
  - Each proposal is keyed by ticket and reply run, so a retried or repeated call in the same run updates the proposal instead of adding another; unconfirmed proposals expire after a set time
  - *Why:* `FunctionInvokingChatClient` runs any tool the model calls automatically, so a tool that created the task directly would skip the user. Newer Microsoft.Extensions.AI versions have function-approval types; check whether the version in use makes this simpler
- **Tool allow-list:** only the two registered tools can run, with validated arguments
- **API keys, database passwords and connection strings** come from user secrets or a gitignored `.env` at the repository root (in CI, from GitHub Actions secrets), never from config files in the repo

## Tests that matter

**CI (deterministic, must pass to merge):** runs `dotnet test --filter "Category!=Eval"`

- The fake provider for deterministic unit tests
- Retrieval returns the right article for known questions, using committed precomputed embeddings against PostgreSQL + pgvector in Testcontainers
- Committed embedding fixtures match the current chunks (content hash check)
- Invalid model JSON is rejected, retried once, then falls back to "needs manual classification"
- Out-of-range citation numbers are removed, and chunks map to the right articles
- Retries stop after the first streamed item; the gap timeout ends a stalled stream; the prompt builder trims chunks; the per-call guard and the request cap stop oversized calls; the tool loop stops at its limit
- Write tools never run without confirmation; confirming twice creates one task; a retried call in the same run leaves one proposal
- A second "Suggest reply" cancels the first stream
- Playwright end-to-end tests for the main flows, against the fake provider

**Evaluation (opt-in, real model):**

- Run with `dotnet test --filter Category=Eval` against Ollama or a real provider
- Reports category accuracy, citation recall and injection resistance, with pass thresholds set per model (for example ≥80% category accuracy for a hosted model, and for every model no follow-up proposals created from injected instructions)
  - *Why:* the write tool only proposes, so the harm to measure is a proposal the attacker caused, not the tool call itself
  - *Why:* a small local model won't reach the same accuracy as a hosted one, so one threshold for all would always fail on Ollama
- Saves a report to `docs/eval/` that records the chat model, embedding model, prompt versions and date, so results can be compared between prompt and model changes

## Workflow

- `PLAN.md` with the design, `docs/specs/` with one spec per step and acceptance criteria, `CLAUDE.md` with the rules
- Claude Code implements one step per branch; a review subagent running a different model checks every step before it merges; bugs go into `docs/ai-log.md`
- The README follows the layout: screenshot, quick start, design notes, tests table, AI tools, evaluation results, bugs found and fixed, possible improvements

### Git branching strategy (GitHub Flow, one branch per step)

- The repo is **public**, since branch protection on a free GitHub account needs a public repo
- `main` is always green: after step 1 it's protected, there are no direct pushes, and a GitHub Actions check (build and deterministic tests) must pass before merging. The evaluation suite doesn't block merges
- One branch per spec step, named after it: `step/04-embeddings`, `step/05-rag-reply`
- Open a pull request into `main` for each step; its description links the spec and ticks off the acceptance criteria
- Review findings are fixed as extra commits on the same branch
- Squash-merge, so `main` keeps one commit per step titled `Step N: <spec title>`, then delete the branch
- Fixes to an earlier step get their own `fix/<short-name>` branch and the same pull request flow; spec changes go in the same pull request as the code
- Tag milestones, for example `v0.1` when the core features work

### Review per step: another model in subagent mode

- After a step is implemented, the main session runs a **review subagent** on a different model from the one that wrote the code (for example, Opus implements and Sonnet reviews)
- Define it once as a project subagent in `.claude/agents/reviewer.md`, with its `model` set in the frontmatter and no edit or write tools, so it can't change code
- Bash isn't read-only. If the agent's `tools` frontmatter supports Bash limited to `git diff`, `git log` and `git show`, use that; otherwise give it no Bash, and the main session puts the diff in the reviewer's prompt. Either way it doesn't run tests, because CI is the source of truth for build and tests
- The reviewer starts with fresh context: it sees only the step's spec, `CLAUDE.md` and the branch diff (`git diff main...HEAD`), not the conversation that produced the code
- It checks the acceptance criteria, correctness and edge cases, security (including prompt injection for LLM steps), tests and the rules in `CLAUDE.md`, and returns findings ranked by severity
- Every finding is either fixed or answered with a reason. A real bug gets a failing test first, then the fix, and an entry in `docs/ai-log.md` ("found by: review subagent")
- After major fixes, run the reviewer again before merging
- The README records which model implemented and which model reviewed each step

### Development environment: a Dev Container with Playwright

- `.devcontainer/` defines one container with the .NET SDK and Node.js. It reuses the project's `docker-compose.yml` for PostgreSQL + pgvector and adds the dev container as one more service, so development and "Run" use the same database setup
- The `docker-outside-of-docker` devcontainer feature gives the container access to the host's Docker, so Testcontainers can start its own PostgreSQL inside the dev container
  - *Why:* without it, retrieval tests pass in CI but fail locally
- `postCreateCommand` restores packages and installs Playwright's Chromium with its system dependencies (`npx playwright install --with-deps chromium`), so a fresh clone is ready after one "Reopen in Container"
- The API and the Angular dev server run inside the container, so Playwright and the Playwright MCP server reach them on `localhost`
- The Playwright MCP server is configured in `.mcp.json` to run headless, since the container has no display, so Claude Code can open the running app, click through it and take screenshots to verify UI work, for example the streaming reply panel
- Playwright end-to-end tests run in the same container and in CI
- Container notes: Chromium needs `--no-sandbox` inside the container; Ollama runs on the host and is reached through `host.docker.internal`; keep credentials and API keys in a gitignored `.env` at the repository root, which both `docker compose` and the dev container read
- `CLAUDE.md` documents how to start the app and run browser checks inside the container

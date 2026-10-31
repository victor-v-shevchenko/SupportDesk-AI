# Step 4: Embeddings

- **Branch:** `step/04-embeddings`
- **Depends on:** step 3a
- **Design:** PLAN.md sections 6.1 (embedding providers), 8, 11 (fixtures)

## Goal

Articles are chunked and embedded, vectors are kept in step with content and model, and similarity search finds the right article for known questions, proven by tests that use committed vectors.

## Scope

**Chunking**
- `ArticleChunker` per PLAN.md 8: split on `##`, long sections split on paragraphs at 1,200 characters with 150 characters of overlap, heading kept
- `ArticleChunk` entity, migration `AddArticleChunks` with `vector(1024)` and an HNSW index (cosine)

**Embedding sync**
- `EmbeddingSyncService` at startup (after seeding): chunks every article, compares hash and model with stored rows, embeds only new or changed chunks in batches, deletes chunks that no longer exist
- A dimension other than `AI:Embeddings:Dimensions` fails with a clear error

**Search**
- `IRetrievalService.SearchAsync(query, k)`: embeds the query, returns chunks with article slug, title, heading, text and similarity, ordered by similarity, filtered by the minimum similarity
- `GET /api/search?q=&k=5` (`k` between 1 and 10)

**Fixtures**
- `tools/EmbedFixtures` console app: embeds all chunks and the test questions with the configured embedding provider and writes `tests/fixtures/embeddings/articles.json` and `questions.json` (model, dimension, items with hash and vector)
- `tests/fixtures/retrieval-questions.json`: about 15 questions, each with the expected article slug, written in customers' words rather than the article's
- Commit the generated fixtures, made with `mxbai-embed-large`

**Angular**
- A small search box on the articles page that calls `/api/search` and lists results with scores (useful for checking retrieval by hand)

## Out of scope

Using retrieval in a prompt (step 5).

## Acceptance criteria

- [ ] Chunking follows the rules, tested on an article with short, long and heading-less sections
- [ ] Startup sync embeds only new or changed chunks; a second start embeds nothing; editing one article re-embeds only its chunks; changing the embedding model re-embeds all
- [ ] A wrong vector dimension fails with a clear error
- [ ] With the committed fixtures loaded into Testcontainers PostgreSQL, the expected article is in the top 3 for every retrieval question, and first for at least 12 of 15
- [ ] A CI test fails when a fixture's hash doesn't match the current chunk, and its message says to run `tools/EmbedFixtures`
- [ ] The minimum similarity is set from these results and written down in PLAN.md 8
- [ ] `GET /api/search` validates `q` (required, at most 500 characters) and `k`

## Tests

- Unit: chunker
- Integration: sync behavior (with `FakeEmbeddingGenerator`); retrieval quality (with the committed fixtures and a generator that returns the fixture vector for each question); fixture hash check; search endpoint validation

## Review focus

That retrieval tests really use the committed vectors, the sync logic's edge cases (deleted sections, renamed headings), and the index and distance operator.

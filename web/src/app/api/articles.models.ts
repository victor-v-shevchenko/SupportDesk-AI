/** Types for the knowledge-base endpoints (PLAN.md 5). */

/** One row of `GET /api/articles`. */
export interface ArticleListItem {
  slug: string;
  title: string;
  updatedAt: string;
}

/** `GET /api/articles/{slug}`; the body is Markdown without its front matter. */
export interface ArticleDetail extends ArticleListItem {
  bodyMarkdown: string;
}

/** One hit of `GET /api/search`; `similarity` is cosine similarity, 1 being identical. */
export interface SearchResult {
  articleSlug: string;
  articleTitle: string;
  heading: string | null;
  text: string;
  similarity: number;
}

/** A search to run: the query and how many chunks to return (1–10). */
export interface SearchQuery {
  q: string;
  k: number;
}

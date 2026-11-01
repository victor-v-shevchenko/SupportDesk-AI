import { Citation } from '../api/replies.models';

/** A piece of reply text: plain text, or a marker with at least one valid number. */
export type ReplySegment =
  | { kind: 'text'; text: string }
  | { kind: 'cite'; numbers: { marker: number; citation: Citation | undefined }[] };

/**
 * The marker forms the server's `CitationParser` reads: `[2]`, `[ 3 ]`, `[1, 2]`, ASCII digits only, at most 9 of them.
 * Keep the two in step.
 */
const markerPattern = /\[\s*([0-9]{1,9}(?:\s*,\s*[0-9]{1,9})*)\s*\]/g;

/**
 * Splits `text` into plain text and citation markers. A number in `citations` becomes a link; one that isn't (it was
 * removed as out of range) stays as plain text, and a marker with no valid number is left exactly as the model wrote it.
 */
export function segmentReply(text: string, citations: readonly Citation[]): ReplySegment[] {
  const byMarker = new Map(citations.map((c) => [c.marker, c]));
  const segments: ReplySegment[] = [];
  let last = 0;
  for (const match of text.matchAll(markerPattern)) {
    const numbers = match[1].split(',').map((n) => {
      const marker = Number(n.trim());
      return { marker, citation: byMarker.get(marker) };
    });
    if (!numbers.some((n) => n.citation)) {
      continue;
    }

    if (match.index > last) {
      segments.push({ kind: 'text', text: text.slice(last, match.index) });
    }

    segments.push({ kind: 'cite', numbers });
    last = match.index + match[0].length;
  }

  if (last < text.length) {
    segments.push({ kind: 'text', text: text.slice(last) });
  }

  return segments;
}

/** The cited articles, once each, ordered by their lowest marker number. */
export function citedArticles(citations: readonly Citation[]): { slug: string; title: string; markers: number[] }[] {
  const articles = new Map<string, { slug: string; title: string; markers: number[] }>();
  for (const c of [...citations].sort((a, b) => a.marker - b.marker)) {
    const article = articles.get(c.articleSlug);
    if (article) {
      article.markers.push(c.marker);
    } else {
      articles.set(c.articleSlug, { slug: c.articleSlug, title: c.articleTitle, markers: [c.marker] });
    }
  }

  return [...articles.values()];
}

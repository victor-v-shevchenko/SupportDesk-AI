import { httpResource, HttpResourceRef } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { ArticleDetail, ArticleListItem, SearchQuery, SearchResult } from './articles.models';

@Injectable({ providedIn: 'root' })
export class ArticlesService {
  /** `GET /api/articles`, ordered by title. Call it from an injection context, such as a component field. */
  list(): HttpResourceRef<ArticleListItem[] | undefined> {
    return httpResource<ArticleListItem[]>(() => '/api/articles');
  }

  /** `GET /api/articles/{slug}`; reloads when `slug` changes and stays idle while it's undefined. A 404 lands in the error state. */
  get(slug: () => string | undefined): HttpResourceRef<ArticleDetail | undefined> {
    return httpResource<ArticleDetail>(() => {
      const value = slug();
      return value ? `/api/articles/${encodeURIComponent(value)}` : undefined;
    });
  }

  /** `GET /api/search`; runs again when `query` changes and stays idle while it's undefined. */
  search(query: () => SearchQuery | undefined): HttpResourceRef<SearchResult[] | undefined> {
    return httpResource<SearchResult[]>(() => {
      const value = query();
      return value ? { url: '/api/search', params: { q: value.q, k: value.k } } : undefined;
    });
  }
}

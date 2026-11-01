import { httpResource, HttpResourceRef } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { ArticleListItem, SearchQuery, SearchResult } from './articles.models';

@Injectable({ providedIn: 'root' })
export class ArticlesService {
  /** `GET /api/articles`, ordered by title. Call it from an injection context, such as a component field. */
  list(): HttpResourceRef<ArticleListItem[] | undefined> {
    return httpResource<ArticleListItem[]>(() => '/api/articles');
  }

  /** `GET /api/search`; runs again when `query` changes and stays idle while it's undefined. */
  search(query: () => SearchQuery | undefined): HttpResourceRef<SearchResult[] | undefined> {
    return httpResource<SearchResult[]>(() => {
      const value = query();
      return value ? { url: '/api/search', params: { q: value.q, k: value.k } } : undefined;
    });
  }
}

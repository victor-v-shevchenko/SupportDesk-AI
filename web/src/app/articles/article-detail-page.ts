import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ArticlesService } from '../api/articles.service';

/** One knowledge-base article, where a reply's citations link to. The Markdown is shown as plain text. */
@Component({
  selector: 'app-article-detail-page',
  imports: [RouterLink, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="back" routerLink="/articles">← Knowledge base</a>
    @if (article.isLoading() && !article.hasValue()) {
      <p class="loading">Loading article…</p>
    } @else if (article.error()) {
      <div class="error" role="alert">
        @if (notFound()) {
          <p>Article not found.</p>
        } @else {
          <p>Couldn't load this article.</p>
          <button type="button" (click)="article.reload()">Try again</button>
        }
      </div>
    } @else if (article.hasValue()) {
      @let a = article.value();
      <h1>{{ a.title }}</h1>
      <p class="meta">Updated <time [attr.datetime]="a.updatedAt">{{ a.updatedAt | date: 'medium' }}</time></p>
      @if (a.bodyMarkdown.trim()) {
        <div class="body">{{ a.bodyMarkdown }}</div>
      } @else {
        <p class="empty">This article is empty.</p>
      }
    }
  `,
  styles: `
    .back { display: inline-block; margin-bottom: 1rem; color: #1d4ed8; text-decoration: none; }
    h1 { margin: 0 0 0.25rem; font-size: 1.5rem; }
    .meta { color: #6b7280; font-size: 0.85rem; margin: 0 0 1rem; }
    .body { white-space: pre-wrap; line-height: 1.5; max-width: 70ch; }
    .loading, .empty { color: #6b7280; }
    .error { color: #9b1c1c; }
  `,
})
export class ArticleDetailPage {
  /** Route parameter, bound by `withComponentInputBinding()`. */
  readonly slug = input<string>();

  protected readonly article = inject(ArticlesService).get(this.slug);

  protected readonly notFound = computed(() => {
    const error = this.article.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });
}

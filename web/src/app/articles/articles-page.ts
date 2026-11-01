import { DecimalPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ArticlesService } from '../api/articles.service';
import { SearchQuery } from '../api/articles.models';

/** The longest query `GET /api/search` accepts. */
const maxQueryLength = 500;

/**
 * The knowledge base, with a similarity search over its chunks for checking retrieval by hand. A search runs when the
 * form is submitted, not on every keystroke, since each one is an embedding call.
 */
@Component({
  selector: 'app-articles-page',
  imports: [DecimalPipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Knowledge base</h1>

    <section class="search" aria-labelledby="search-heading">
      <h2 id="search-heading">Search</h2>
      <form role="search" (submit)="submit($event)">
        <label for="search-query">Question</label>
        <input
          id="search-query"
          type="search"
          [maxLength]="maxQueryLength"
          [value]="draft()"
          (input)="draft.set($any($event.target).value)"
          placeholder="How long does a refund take?"
        />
        <label for="search-k">Results</label>
        <select id="search-k" (change)="k.set(+$any($event.target).value)">
          <!-- [selected] per option: a [value] on the select is set before the @for adds the options, so it wouldn't stick -->
          @for (n of kOptions; track n) {
            <option [value]="n" [selected]="n === k()">{{ n }}</option>
          }
        </select>
        <button type="submit" [disabled]="draft().trim().length === 0">Search</button>
      </form>

      @if (query()) {
        @if (results.isLoading()) {
          <p class="loading">Searching…</p>
        } @else if (results.error()) {
          <div class="error" role="alert">
            <p>{{ searchError() }}</p>
            <button type="button" (click)="results.reload()">Try again</button>
          </div>
        } @else if (results.hasValue() && results.value().length > 0) {
          <ol class="results">
            @for (result of results.value(); track $index) {
              <li>
                <div class="result-head">
                  <a class="title" [routerLink]="['/articles', result.articleSlug]">{{ result.articleTitle }}</a>
                  @if (result.heading) {
                    <span class="heading">· {{ result.heading }}</span>
                  }
                  <span class="score" title="Cosine similarity">{{ result.similarity | number: '1.3-3' }}</span>
                </div>
                <p class="text">{{ result.text }}</p>
              </li>
            }
          </ol>
        } @else {
          <p class="empty">No article is similar enough to this question.</p>
        }
      }
    </section>

    <section aria-labelledby="articles-heading">
      <h2 id="articles-heading">Articles</h2>
      @if (articles.isLoading() && !articles.hasValue()) {
        <p class="loading">Loading articles…</p>
      } @else if (articles.error()) {
        <div class="error" role="alert">
          <p>Couldn't load the articles.</p>
          <button type="button" (click)="articles.reload()">Try again</button>
        </div>
      } @else if (articles.hasValue() && articles.value().length > 0) {
        <ul class="articles">
          @for (article of articles.value(); track article.slug) {
            <li><a class="title" [routerLink]="['/articles', article.slug]">{{ article.title }}</a> <span class="slug">{{ article.slug }}</span></li>
          }
        </ul>
      } @else {
        <p class="empty">No articles yet.</p>
      }
    </section>
  `,
  styles: `
    h2 { font-size: 1rem; margin: 1.5rem 0 0.75rem; }
    form { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; }
    input[type='search'] { flex: 1 1 20rem; padding: 0.4rem 0.5rem; font: inherit; }
    select, button { padding: 0.4rem 0.6rem; font: inherit; }
    label { font-size: 0.875rem; color: #4b5563; }
    .results { padding-left: 1.25rem; }
    .results li { margin: 0.75rem 0; }
    .result-head { display: flex; flex-wrap: wrap; gap: 0.35rem; align-items: baseline; }
    .title { font-weight: 600; color: #1d4ed8; text-decoration: none; }
    .heading { color: #4b5563; }
    .score { margin-left: auto; font-variant-numeric: tabular-nums; color: #1d4ed8; }
    .text { margin: 0.25rem 0 0; white-space: pre-line; color: #374151; font-size: 0.9rem; }
    .articles { list-style: none; padding: 0; }
    .articles li { padding: 0.4rem 0; border-bottom: 1px solid #e5e7eb; }
    .slug { margin-left: 0.5rem; color: #6b7280; font-size: 0.85rem; }
    .loading, .empty { color: #6b7280; }
    .error { color: #9b1c1c; }
  `,
})
export class ArticlesPage {
  private readonly api = inject(ArticlesService);

  protected readonly maxQueryLength = maxQueryLength;
  protected readonly kOptions = [3, 5, 10];
  protected readonly draft = signal('');
  protected readonly k = signal(5);
  /** The submitted search; the results resource follows it. */
  protected readonly query = signal<SearchQuery | undefined>(undefined);

  protected readonly articles = this.api.list();
  protected readonly results = this.api.search(this.query);

  protected readonly searchError = computed(() => {
    const error = this.results.error();
    return error instanceof HttpErrorResponse && error.status === 400
      ? 'That search isn’t valid. Use at most 500 characters.'
      : 'The search failed. Is the embedding model running?';
  });

  protected submit(event: Event): void {
    event.preventDefault();
    const q = this.draft().trim();
    if (q.length > 0) {
      this.query.set({ q, k: this.k() });
    }
  }
}

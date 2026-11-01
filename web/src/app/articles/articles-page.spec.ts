import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ArticleListItem, SearchResult } from '../api/articles.models';
import { ArticlesPage } from './articles-page';

const articles: ArticleListItem[] = [
  { slug: 'refunds', title: 'Refunds', updatedAt: '2026-10-31T10:00:00Z' },
  { slug: 'shipping-times', title: 'Shipping times and delivery options', updatedAt: '2026-10-31T10:00:00Z' },
];

const results: SearchResult[] = [
  { articleSlug: 'refunds', articleTitle: 'Refunds', heading: 'How long refunds take', text: 'Refunds take 5–10 business days.', similarity: 0.7134 },
  { articleSlug: 'refunds', articleTitle: 'Refunds', heading: null, text: 'This article explains refunds.', similarity: 0.6 },
];

describe('ArticlesPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function create() {
    const fixture = TestBed.createComponent(ArticlesPage);
    TestBed.tick();
    http.expectOne('/api/articles').flush(articles);
    await fixture.whenStable();
    return fixture;
  }

  async function search(fixture: Awaited<ReturnType<typeof create>>, text: string, k?: string): Promise<TestRequest> {
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('#search-query')!;
    input.value = text;
    input.dispatchEvent(new Event('input'));
    if (k) {
      const select = el.querySelector<HTMLSelectElement>('#search-k')!;
      select.value = k;
      select.dispatchEvent(new Event('change'));
    }
    await fixture.whenStable();
    el.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    TestBed.tick();
    return http.expectOne((r) => r.url === '/api/search');
  }

  it('lists the articles', async () => {
    const fixture = await create();
    const el = fixture.nativeElement as HTMLElement;

    expect([...el.querySelectorAll('.articles .title')].map((t) => t.textContent)).toEqual(['Refunds', 'Shipping times and delivery options']);
    expect(el.querySelector('.results')).toBeNull();
  });

  it('shows the default of 5 results in the select, matching what a search sends', async () => {
    const fixture = await create();

    const select = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>('#search-k')!;
    expect(select.value).toBe('5');
    expect((await search(fixture, 'refund')).request.params.get('k')).toBe('5');
  });

  it('shows the error state when the articles fail to load', async () => {
    const fixture = TestBed.createComponent(ArticlesPage);
    TestBed.tick();
    http.expectOne('/api/articles').flush(null, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('[role=alert]')?.textContent).toContain("Couldn't load the articles");
  });

  it('does not search until the form is submitted, and not for blank text', async () => {
    const fixture = await create();
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('#search-query')!;
    input.value = '   ';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    expect(el.querySelector<HTMLButtonElement>('button[type=submit]')!.disabled).toBe(true);
    http.expectNone((r) => r.url === '/api/search');
  });

  it('sends the trimmed query with k and lists results with their scores', async () => {
    const fixture = await create();

    const request = await search(fixture, '  where is my refund  ', '3');
    expect(request.request.params.get('q')).toBe('where is my refund');
    expect(request.request.params.get('k')).toBe('3');
    expect((fixture.nativeElement as HTMLElement).querySelector('.loading')?.textContent).toContain('Searching');
    request.flush(results);
    await fixture.whenStable();

    const items = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.results li')];
    expect(items).toHaveLength(2);
    expect(items[0].querySelector('.heading')?.textContent).toContain('How long refunds take');
    expect(items[0].querySelector('.score')?.textContent).toBe('0.713');
    expect(items[1].querySelector('.heading')).toBeNull();
  });

  it('shows the empty state when nothing is similar enough', async () => {
    const fixture = await create();

    (await search(fixture, 'weather in Paris')).flush([]);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('.empty')?.textContent).toContain('No article is similar enough');
  });

  it('shows the error state when the search fails', async () => {
    const fixture = await create();

    (await search(fixture, 'refund')).flush(null, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('[role=alert]')?.textContent).toContain('The search failed');
  });
});
